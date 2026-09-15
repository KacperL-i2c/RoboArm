using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Safety;
using RoboArm.Simulator;
using Xunit;

namespace RoboArm.Tests.Simulator;

public sealed class SimulatedMotionTargetTests
{
    private static MachineConfig Config(double maxVel = 60, double maxAccel = 200,
        double min = -120, double max = 120) =>
        new("sim", [new AxisConfig(0, "a", MaxVelocityDegS: maxVel, MaxAccelDegS2: maxAccel,
            SoftLimitMinDeg: min, SoftLimitMaxDeg: max)]);

    private static MotionFrame Frame(double pos, double vel, long ts = 0) =>
        new(ts, [new AxisSetpoint(0, pos, vel)]);

    private static async Task<SimulatedMotionTarget> ConnectedTargetAsync(MachineConfig config)
    {
        var sim = new SimulatedMotionTarget(config, tickHz: 100);
        await sim.ConnectAsync();
        await sim.EnableAsync(true);
        return sim;
    }

    [Fact]
    public async Task Tracks_Setpoint_Position_And_Converges()
    {
        var sim = await ConnectedTargetAsync(Config());
        await sim.SendFrameAsync(Frame(0, 30));
        sim.Advance(TimeSpan.FromMilliseconds(100));
        await sim.SendFrameAsync(Frame(30, 30));
        sim.Advance(TimeSpan.FromSeconds(3));
        Assert.InRange(sim.EstimatedPositionDeg(0), 29.0, 31.0);
    }

    [Fact]
    public async Task Velocity_Never_Exceeds_Axis_Max()
    {
        var sim = await ConnectedTargetAsync(Config(maxVel: 40));
        var maxSeen = 0.0;

        await sim.SendFrameAsync(Frame(0, 0));
        sim.Advance(TimeSpan.FromMilliseconds(100));
        await sim.SendFrameAsync(Frame(120, 40));
        for (var i = 0; i < 300; i++)
        {
            double before = sim.EstimatedPositionDeg(0);
            sim.Advance(TimeSpan.FromMilliseconds(10));
            double after = sim.EstimatedPositionDeg(0);
            var velocity = (after - before) / 0.01;
            maxSeen = Math.Max(maxSeen, velocity);
        }
        Assert.True(maxSeen <= 40.5, $"velocity {maxSeen} exceeded axis max 40");
    }

    [Fact]
    public async Task Telemetry_Streamed_Every_Tick_With_State()
    {
        var sim = await ConnectedTargetAsync(Config());
        var frames = new List<TelemetryFrame>();
        sim.TelemetryReceived += f => frames.Add(f);
        sim.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(50, frames.Count);
        Assert.All(frames, f => Assert.Equal(RuntimeState.Enabled, f.State));
        Assert.All(frames, f => Assert.Equal(0, f.Axes[0].AxisId));
    }

    [Fact]
    public async Task SoftStop_Ramps_To_Zero_And_Stays_Enabled()
    {
        var sim = await ConnectedTargetAsync(Config());
        await sim.SendFrameAsync(Frame(100, 50));
        sim.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(RuntimeState.Enabled, sim.State);

        await sim.StopAsync(StopSeverity.SoftStop);
        sim.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(RuntimeState.Enabled, sim.State);
        var settled = sim.EstimatedPositionDeg(0);
        sim.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(settled, sim.EstimatedPositionDeg(0), 3); // fully stopped
    }

    [Fact]
    public async Task ControlledStop_Ramps_Then_Disables()
    {
        var sim = await ConnectedTargetAsync(Config());
        await sim.SendFrameAsync(Frame(100, 50));
        sim.Advance(TimeSpan.FromMilliseconds(300));

        await sim.StopAsync(StopSeverity.ControlledStop);
        sim.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(RuntimeState.Connected, sim.State); // disabled but connected
    }

    [Fact]
    public async Task Kill_Freezes_Immediately_And_Drops_Enable()
    {
        var sim = await ConnectedTargetAsync(Config());
        await sim.SendFrameAsync(Frame(100, 50));
        sim.Advance(TimeSpan.FromMilliseconds(300));
        var posAtKill = sim.EstimatedPositionDeg(0);

        await sim.StopAsync(StopSeverity.Kill);
        sim.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(RuntimeState.Connected, sim.State);
        Assert.Equal(posAtKill, sim.EstimatedPositionDeg(0), 3); // frozen, no drift
    }

    [Fact]
    public async Task EStop_Latches_Fault_And_Further_Frames_Are_Ignored()
    {
        var sim = await ConnectedTargetAsync(Config());
        sim.InjectEStop();

        Assert.Equal(RuntimeState.Faulted, sim.State);
        var faulted = new List<FaultReason>();
        sim.FaultDetected += r => faulted.Add(r);

        await sim.SendFrameAsync(Frame(90, 40));
        sim.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, sim.EstimatedPositionDeg(0), 6); // no motion while faulted

        sim.ClearFaults();
        await sim.ConnectAsync(); // power cycle
        await sim.EnableAsync(true);
        await sim.SendFrameAsync(Frame(10, 10));
        sim.Advance(TimeSpan.FromSeconds(1));
        Assert.InRange(sim.EstimatedPositionDeg(0), 8, 12);
    }

    [Fact]
    public async Task Commanded_Setpoint_Beyond_SoftLimit_Trips_Fault()
    {
        var sim = await ConnectedTargetAsync(Config(max: 120));
        var faults = new List<FaultReason>();
        sim.FaultDetected += r => faults.Add(r);

        await sim.SendFrameAsync(Frame(500, 0)); // far beyond +120
        sim.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(RuntimeState.Faulted, sim.State);
        Assert.Contains(FaultReason.SoftLimit, faults);
    }

    [Fact]
    public async Task CommLoss_Suppresses_Telemetry_And_Frame_Effect_Then_Recovers()
    {
        var sim = await ConnectedTargetAsync(Config());
        var frames = new List<TelemetryFrame>();
        sim.TelemetryReceived += f => frames.Add(f);

        sim.InjectCommLoss(TimeSpan.FromMilliseconds(300));
        await sim.SendFrameAsync(Frame(50, 25));
        sim.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Empty(frames);                            // telemetry silent during loss
        Assert.Equal(0, sim.EstimatedPositionDeg(0), 6); // frame dropped

        await sim.SendFrameAsync(Frame(50, 25));
        sim.Advance(TimeSpan.FromMilliseconds(100));
        Assert.NotEmpty(frames); // recovered
    }

    [Fact]
    public async Task Frame_Delay_Holds_Then_Applies_Frames()
    {
        var sim = await ConnectedTargetAsync(Config());
        sim.InjectFrameDelay(50); // 500 ms at 100 Hz

        await sim.SendFrameAsync(Frame(30, 15));
        sim.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, sim.EstimatedPositionDeg(0), 6); // still held

        sim.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(sim.EstimatedPositionDeg(0) > 1, "delayed frame should now be in effect");
    }

    [Fact]
    public async Task InjectDivergence_Shifts_Estimated_Position()
    {
        var sim = await ConnectedTargetAsync(Config());
        await sim.SendFrameAsync(Frame(0, 0));
        sim.Advance(TimeSpan.FromMilliseconds(100));
        sim.InjectDivergence(0, offsetDeg: 5.0);
        Assert.Equal(5.0, sim.EstimatedPositionDeg(0), 6);
    }

    [Fact]
    public async Task Enable_Required_For_Motion()
    {
        var sim = new SimulatedMotionTarget(Config(), tickHz: 100);
        await sim.ConnectAsync();
        await sim.SendFrameAsync(Frame(50, 25));
        sim.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, sim.EstimatedPositionDeg(0), 6);
    }
}
