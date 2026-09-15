using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using RoboArm.Machine;
using RoboArm.Motion;
using Xunit;

namespace RoboArm.Tests.Motion;

public sealed class MotionPlannerTests
{
    private static MachineConfig OneAxis(double vmax = 60, double amax = 200,
        double min = -120, double max = 120) =>
        new("m", [new AxisConfig(0, "a", MaxVelocityDegS: vmax, MaxAccelDegS2: amax,
            SoftLimitMinDeg: min, SoftLimitMaxDeg: max)]);

    private static MotionPlan Plan(MachineConfig config, double from, double to,
        double v0 = 0, double scale = 1.0) =>
        MotionPlan.Plan(config, new PlanRequest(
            [new AxisPlanStart(0, from, v0)],
            new Dictionary<int, double> { [0] = to },
            SpeedScale: scale));

    [Fact]
    public void Simple_Move_Reaches_Target_And_Stops()
    {
        var plan = Plan(OneAxis(), 0, 30);
        var end = plan.Sample(plan.DurationSeconds + 5); // beyond duration clamps
        Assert.Equal(30, end[0].PositionDeg, 6);
        Assert.Equal(0, end[0].VelocityDegS, 6);
    }

    [Fact]
    public void Trapezoid_Duration_Matches_Physics()
    {
        // v=60, a=200, d=30: triangle profile: vpeak=sqrt(a*d)=77.5 -> capped at 60.
        // accel 0.3s (9°), decel 0.3s (9°), cruise 12° at 60°/s = 0.2s => 0.8s total.
        var plan = Plan(OneAxis(vmax: 60, amax: 200), 0, 30);
        Assert.Equal(0.8, plan.DurationSeconds, 2);
    }

    [Fact]
    public void Negative_Move_Symmetric()
    {
        var plan = Plan(OneAxis(), 0, -30);
        var mid = plan.Sample(plan.DurationSeconds / 2);
        Assert.True(mid[0].PositionDeg < 0, "halfway through a negative move should be negative");
        Assert.Equal(-30, plan.Sample(plan.DurationSeconds)[0].PositionDeg, 6);
    }

    [Fact]
    public void Nonzero_Start_Velocity_Toward_Target_Integrated()
    {
        var plan = Plan(OneAxis(), 0, 60, v0: 30);
        Assert.Equal(60, plan.Sample(plan.DurationSeconds)[0].PositionDeg, 6);
        Assert.Equal(0, plan.Sample(plan.DurationSeconds)[0].VelocityDegS, 6);
    }

    [Fact]
    public void Overspeed_Start_Velocity_Brakes_And_Reverses()
    {
        // Start velocity so high it cannot stop at the target: overshoot, return.
        var plan = Plan(OneAxis(), 100, 110, v0: 60);
        Assert.Equal(110, plan.Sample(plan.DurationSeconds)[0].PositionDeg, 6);
    }

    [Fact]
    public void SpeedScale_Slows_Plan()
    {
        var full = Plan(OneAxis(), 0, 60);
        var half = Plan(OneAxis(), 0, 60, scale: 0.5);
        Assert.True(half.DurationSeconds > full.DurationSeconds,
            $"half-speed {half.DurationSeconds}s must be slower than full {full.DurationSeconds}s");
        Assert.Equal(60, half.Sample(half.DurationSeconds)[0].PositionDeg, 6);
    }

    [Fact]
    public void Target_Outside_SoftLimits_Rejected()
    {
        Assert.Throws<PlannerException>(() => Plan(OneAxis(), 0, 130));
        Assert.Throws<PlannerException>(() => Plan(OneAxis(), -130, 0));
    }

    [Fact]
    public void Coordinated_Move_All_Axes_Finish_Together()
    {
        var config = new MachineConfig("m",
        [
            new AxisConfig(0, "a", MaxVelocityDegS: 60, MaxAccelDegS2: 200),
            new AxisConfig(1, "b", MaxVelocityDegS: 10, MaxAccelDegS2: 50),
        ]);
        var plan = MotionPlan.Plan(config, new PlanRequest(
            [new AxisPlanStart(0, 0, 0), new AxisPlanStart(1, 0, 0)],
            new Dictionary<int, double> { [0] = 60, [1] = 60 }));

        var slowAxisAlone = MotionPlan.Plan(config, new PlanRequest(
            [new AxisPlanStart(1, 0, 0)], new Dictionary<int, double> { [1] = 60 }));

        Assert.Equal(slowAxisAlone.DurationSeconds, plan.DurationSeconds, 6);
        var end = plan.Sample(plan.DurationSeconds);
        Assert.Equal(60, end.Single(s => s.AxisId == 0).PositionDeg, 6);
        Assert.Equal(60, end.Single(s => s.AxisId == 1).PositionDeg, 6);
        Assert.All(end, s => Assert.Equal(0, s.VelocityDegS, 6));
    }

    [Fact]
    public void Frames_Stream_At_Fixed_Cadence_With_Monotonic_Timestamps()
    {
        var plan = Plan(OneAxis(), 0, 30);
        var frames = plan.Frames(baseTimestampMs: 1000, periodSeconds: 0.01).ToList();
        Assert.Equal(plan.DurationSeconds / 0.01, frames.Count - 1, 0);
        Assert.True(frames[0].TimestampMs <= frames[^1].TimestampMs);
        Assert.All(frames, f => Assert.Single(f.Setpoints));
    }

    // ---- Property tests (FsCheck) ----

    private sealed record Case(double Vmax, double Amax, double From, double To);

    private static Arbitrary<Case> CaseArb() =>
        (from v in Gen.Choose(5, 360)
         from a in Gen.Choose(10, 2000)
         from f in Gen.Choose(-115, 115)
         from t in Gen.Choose(-115, 115)
         select new Case(v, a, f, t)).ToArbitrary();

    private static bool Trivial(Case c) => Math.Abs(c.From - c.To) < 0.5;

    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public Property Profiles_Respect_Velocity_Limits() =>
        Prop.ForAll(CaseArb(), c =>
        {
            if (Trivial(c))
                return Prop.Label(true, "trivial");
            var plan = Plan(OneAxis(c.Vmax, c.Amax), c.From, c.To);
            for (var t = 0.0; t <= plan.DurationSeconds; t += 0.005)
            {
                var v = Math.Abs(plan.Sample(t)[0].VelocityDegS);
                if (v > c.Vmax + 0.5)
                    return Prop.Label(false, $"v={v} exceeds vmax={c.Vmax} at t={t} ({c.From}->{c.To})");
            }
            return Prop.Label(true, "ok");
        });

    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public Property Profiles_Respect_Accel_Limits() =>
        Prop.ForAll(CaseArb(), c =>
        {
            if (Trivial(c))
                return Prop.Label(true, "trivial");
            var plan = Plan(OneAxis(c.Vmax, c.Amax), c.From, c.To);
            var dt = 0.005;
            var prev = plan.Sample(0)[0].VelocityDegS;
            for (var t = dt; t <= plan.DurationSeconds + dt; t += dt)
            {
                var v = plan.Sample(Math.Min(t, plan.DurationSeconds))[0].VelocityDegS;
                var accel = Math.Abs(v - prev) / dt;
                if (accel > c.Amax + 2.0)
                    return Prop.Label(false, $"accel={accel} exceeds amax={c.Amax} at t={t} ({c.From}->{c.To})");
                prev = v;
            }
            return Prop.Label(true, "ok");
        });

    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public Property Decel_Margin_To_Soft_Limits_Always_Holds() =>
        Prop.ForAll(CaseArb(), c =>
        {
            if (Trivial(c))
                return Prop.Label(true, "trivial");
            var plan = Plan(OneAxis(c.Vmax, c.Amax), c.From, c.To);
            for (var t = 0.0; t <= plan.DurationSeconds; t += 0.005)
            {
                var sp = plan.Sample(t)[0];
                var stoppingDist = sp.VelocityDegS * sp.VelocityDegS / (2 * c.Amax);
                var roomAhead = 120 - sp.PositionDeg;
                var roomBehind = sp.PositionDeg + 120;
                if (sp.VelocityDegS > 0.01 && roomAhead < stoppingDist - 0.5)
                    return Prop.Label(false, $"t={t}: {roomAhead:F2}° room < {stoppingDist:F2}° stopping");
                if (sp.VelocityDegS < -0.01 && roomBehind < stoppingDist - 0.5)
                    return Prop.Label(false, $"t={t}: {roomBehind:F2}° room < {stoppingDist:F2}° stopping");
            }
            return Prop.Label(true, "ok");
        });

    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public Property Plans_Always_Reach_Target_From_Rest() =>
        Prop.ForAll(CaseArb(), c =>
        {
            if (Trivial(c))
                return Prop.Label(true, "trivial");
            var plan = Plan(OneAxis(c.Vmax, c.Amax), c.From, c.To);
            var end = plan.Sample(plan.DurationSeconds)[0];
            var reached = Math.Abs(end.PositionDeg - c.To) < 0.01 && Math.Abs(end.VelocityDegS) < 0.01;
            return Prop.Label(reached, $"end pos {end.PositionDeg} vs target {c.To} (vmax {c.Vmax}, amax {c.Amax})");
        });
}
