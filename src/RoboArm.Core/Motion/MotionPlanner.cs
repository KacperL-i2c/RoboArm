using RoboArm.Machine;

namespace RoboArm.Motion;

public sealed class PlannerException : Exception
{
    public PlannerException(string message) : base(message) { }
}

public sealed record AxisPlanStart(int AxisId, double PositionDeg, double VelocityDegS);

public sealed record PlanRequest(
    IReadOnlyList<AxisPlanStart> Starts,
    IReadOnlyDictionary<int, double> TargetsDeg,
    double SpeedScale = 1.0
);

/// <summary>
/// Trapezoidal multi-axis motion plan. All axes share the slowest axis's duration
/// (time-scaled) so coordinated moves finish together. Sampling gives position and
/// velocity setpoints in degrees; profiles never exceed per-axis v/a limits and
/// always satisfy the soft-limit decel-margin property (docs/03 L6).
/// </summary>
public sealed class MotionPlan
{
    private readonly IReadOnlyList<StretchedProfile> _profiles;

    private MotionPlan(IReadOnlyList<StretchedProfile> profiles, double durationSeconds)
    {
        _profiles = profiles;
        DurationSeconds = durationSeconds;
        AxisIds = profiles.Select(p => p.Profile.AxisId).ToList();
    }

    public double DurationSeconds { get; }
    public IReadOnlyList<int> AxisIds { get; }

    /// <summary>All axes at time <paramref name="tSeconds"/> (clamped to duration).</summary>
    public IReadOnlyList<AxisSetpoint> Sample(double tSeconds) =>
        _profiles.Select(p => p.Sample(Math.Min(tSeconds, DurationSeconds))).ToList();

    /// <summary>Frames for a full move at a fixed cadence, timestamps from the given base.</summary>
    public IEnumerable<MotionFrame> Frames(long baseTimestampMs, double periodSeconds)
    {
        for (var t = 0.0; t <= DurationSeconds + 1e-9; t += periodSeconds)
        {
            var clamped = Math.Min(t, DurationSeconds);
            yield return new MotionFrame(
                baseTimestampMs + (long)(clamped * 1000),
                Sample(clamped));
        }
    }

    public static MotionPlan Plan(MachineConfig config, PlanRequest request)
    {
        if (!config.Validate(out var errors))
            throw new PlannerException($"Machine config invalid: {string.Join("; ", errors)}");
        if (request.SpeedScale is <= 0 or > 1)
            throw new PlannerException($"SpeedScale {request.SpeedScale} must be in (0, 1].");

        var profiles = new List<Profile>();
        foreach (var start in request.Starts)
        {
            var axis = config.Axis(start.AxisId)
                ?? throw new PlannerException($"Unknown axis id {start.AxisId}.");
            if (!request.TargetsDeg.TryGetValue(start.AxisId, out var target))
                continue;

            if (target < axis.SoftLimitMinDeg - 1e-9 || target > axis.SoftLimitMaxDeg + 1e-9)
                throw new PlannerException(
                    $"Axis {axis.Id} target {target:F2}° outside soft limits [{axis.SoftLimitMinDeg}, {axis.SoftLimitMaxDeg}].");
            if (start.PositionDeg < axis.SoftLimitMinDeg - 1e-9 || start.PositionDeg > axis.SoftLimitMaxDeg + 1e-9)
                throw new PlannerException(
                    $"Axis {axis.Id} start {start.PositionDeg:F2}° outside soft limits.");

            profiles.Add(Profile.Build(
                axis.Id,
                start.PositionDeg, start.VelocityDegS, target,
                axis.MaxVelocityDegS * request.SpeedScale,
                axis.MaxAccelDegS2));
        }

        if (profiles.Count == 0)
            throw new PlannerException("Plan request moves no axes.");

        var maxDuration = profiles.Max(p => p.DurationSeconds);
        var stretched = profiles
            .Select(p => new StretchedProfile(p, maxDuration / p.DurationSeconds))
            .ToList();
        return new MotionPlan(stretched, maxDuration);
    }

    internal readonly record struct ProfileSegment(double DurationSeconds, double Accel);

    /// <summary>Piecewise constant-acceleration 1D profile from (p0, v0) to a stopped target.</summary>
    private sealed class Profile
    {
        private readonly List<ProfileSegment> _segments;
        private readonly double _startPos;
        private readonly double _startVel;

        private Profile(int axisId, List<ProfileSegment> segments, double duration,
            double startPos, double startVel, double endPos)
        {
            _segments = segments;
            _startPos = startPos;
            _startVel = startVel;
            AxisId = axisId;
            DurationSeconds = duration;
            EndPosition = endPos;
        }

        public int AxisId { get; }
        public double DurationSeconds { get; }
        public double EndPosition { get; }

        public (double Pos, double Vel) Sample(double t)
        {
            var pos = _startPos;
            var vel = _startVel;
            var time = 0.0;
            foreach (var seg in _segments)
            {
                if (t <= time + seg.DurationSeconds + 1e-12)
                {
                    var dt = t - time;
                    return (pos + vel * dt + 0.5 * seg.Accel * dt * dt, vel + seg.Accel * dt);
                }
                pos += vel * seg.DurationSeconds + 0.5 * seg.Accel * seg.DurationSeconds * seg.DurationSeconds;
                vel += seg.Accel * seg.DurationSeconds;
                time += seg.DurationSeconds;
            }
            return (EndPosition, 0.0);
        }

        public static Profile Build(int axisId, double p0, double v0, double pT, double vmax, double amax)
        {
            var segments = new List<ProfileSegment>();
            var pos = p0;
            var vel = v0;
            const double eps = 1e-9;
            var guard = 0;
            while (Math.Abs(pT - pos) > 1e-6 || Math.Abs(vel) > 1e-6)
            {
                if (++guard > 8)
                    throw new PlannerException("Profile construction failed to converge.");

                var distance = pT - pos;
                if (Math.Abs(vel) > eps && Math.Sign(vel) != Math.Sign(distance) && Math.Abs(distance) > eps)
                {
                    // Moving away from target: brake to zero first.
                    AddBrake(ref pos, ref vel, segments, amax);
                    continue;
                }

                var d = Math.Abs(distance);
                var dir = Math.Sign(distance);
                var v0m = Math.Abs(vel); // vel is toward target here
                var vpeak = Math.Min(vmax, Math.Sqrt(amax * d + v0m * v0m / 2.0));
                if (v0m > vpeak + eps)
                {
                    // Too fast to stop at the target: brake (may overshoot; loop reverses).
                    AddBrake(ref pos, ref vel, segments, amax);
                    continue;
                }

                // Accelerate v0 -> vpeak.
                var accelDist = (vpeak * vpeak - v0m * v0m) / (2 * amax);
                if (vpeak - v0m > eps)
                {
                    var dt = (vpeak - v0m) / amax;
                    segments.Add(new ProfileSegment(dt, dir * amax));
                    pos += vel * dt + 0.5 * dir * amax * dt * dt;
                    vel = dir * vpeak;
                }

                // Cruise.
                var cruiseDist = d - accelDist - vpeak * vpeak / (2 * amax);
                if (cruiseDist > eps)
                {
                    var dt = cruiseDist / vpeak;
                    segments.Add(new ProfileSegment(dt, 0));
                    pos += dir * cruiseDist;
                }

                // Decelerate vpeak -> 0.
                var dtDecel = vpeak / amax;
                segments.Add(new ProfileSegment(dtDecel, -dir * amax));
                pos += vel * dtDecel - 0.5 * dir * amax * dtDecel * dtDecel;
                vel = 0;
            }

            var duration = segments.Sum(s => s.DurationSeconds);
            return new Profile(axisId, segments, duration, p0, v0, pos);
        }

        private static void AddBrake(ref double pos, ref double vel, List<ProfileSegment> segments, double amax)
        {
            var dt = Math.Abs(vel) / amax;
            var dir = Math.Sign(vel);
            segments.Add(new ProfileSegment(dt, -dir * amax));
            pos += vel * dt - 0.5 * dir * amax * dt * dt;
            vel = 0;
        }
    }

    /// <summary>Profile stretched in time so all axes share one duration; path unchanged,
    /// velocities scale down — limits still hold.</summary>
    private sealed class StretchedProfile
    {
        public StretchedProfile(Profile profile, double stretch)
        {
            Profile = profile;
            Stretch = stretch;
        }

        public Profile Profile { get; }
        private double Stretch { get; }

        public AxisSetpoint Sample(double tSeconds)
        {
            var (pos, vel) = Profile.Sample(tSeconds / Stretch);
            return new AxisSetpoint(Profile.AxisId, pos, vel / Stretch);
        }
    }
}
