using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.Runtime;

public sealed class EngineFsmTests
{
    private static RuntimeState[] AllStates =>
    [
        RuntimeState.Offline, RuntimeState.Connected, RuntimeState.Enabled,
        RuntimeState.Executing, RuntimeState.Paused, RuntimeState.Faulted,
    ];

    [Theory]
    [MemberData(nameof(States))]
    public async Task Fault_Latches_From_Every_State(RuntimeState from)
    {
        using var h = new Harness();
        await ReachAsync(h, from);
        h.Sim.InjectEStop();
        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        Assert.Equal(FaultReason.EStop, h.Engine.ActiveFault);
        h.Step(200);
        Assert.Equal(RuntimeState.Faulted, h.Engine.State); // sticky
    }

    [Theory]
    [MemberData(nameof(States))]
    public async Task Ack_From_NonFaulted_Throws(RuntimeState from)
    {
        using var h = new Harness();
        await ReachAsync(h, from);
        if (from == RuntimeState.Faulted)
            return;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.AcknowledgeFaultAsync());
    }

    [Fact]
    public async Task Ack_After_Fault_Requires_Rehome_Before_Enable()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        h.Sim.InjectEStop();
        await h.Engine.AcknowledgeFaultAsync();

        Assert.Equal(RuntimeState.Connected, h.Engine.State);
        Assert.True(h.Engine.RequiresRehome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.EnableAsync(true));

        await h.Engine.MarkHomedAsync();
        Assert.False(h.Engine.RequiresRehome);
        await h.Engine.EnableAsync(true);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);
    }

    [Fact]
    public async Task Connect_From_NonOffline_Throws()
    {
        using var h = new Harness();
        await h.Engine.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.ConnectAsync());
    }

    [Fact]
    public async Task Move_Without_Enable_Throws()
    {
        using var h = new Harness();
        await h.Engine.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 10 }));
    }

    [Fact]
    public async Task Move_To_Outside_Limits_Rejected_And_State_Intact()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 500 }));
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);

        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 10 });
        Assert.Equal(RuntimeState.Executing, h.Engine.State);
    }

    [Fact]
    public async Task Pause_And_Stop_From_Paused_Reach_Enabled()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 80 });
        h.Step(100);
        Assert.Equal(RuntimeState.Executing, h.Engine.State);

        await h.Engine.PauseAsync();
        Assert.Equal(RuntimeState.Paused, h.Engine.State);

        await h.Engine.StopAsync(StopSeverity.SoftStop);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);
    }

    [Fact]
    public async Task Move_From_Paused_Throws_Until_Stopped()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 80 });
        h.Step(50);
        await h.Engine.PauseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 0 }));
    }

    [Fact]
    public async Task Disconnect_Only_From_Connected()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.DisconnectAsync());
        await h.Engine.ConnectAsync();
        await h.Engine.DisconnectAsync();
        Assert.Equal(RuntimeState.Offline, h.Engine.State);
    }

    public static TheoryData<RuntimeState> States => new(AllStates);

    private static async Task ReachAsync(Harness h, RuntimeState state)
    {
        switch (state)
        {
            case RuntimeState.Offline:
                return;
            case RuntimeState.Connected:
                await h.Engine.ConnectAsync();
                return;
            case RuntimeState.Enabled:
                await h.ConnectAndEnableAsync();
                return;
            case RuntimeState.Executing:
                await h.ConnectAndEnableAsync();
                await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 80 });
                h.Step(50);
                return;
            case RuntimeState.Paused:
                await h.ConnectAndEnableAsync();
                await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 80 });
                h.Step(50);
                await h.Engine.PauseAsync();
                return;
            case RuntimeState.Faulted:
                await h.ConnectAndEnableAsync();
                h.Sim.InjectEStop();
                return;
        }
    }
}

public sealed class EngineIntegrationTests
{
    [Fact]
    public async Task Three_Move_Program_Completes_In_Simulator()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        var completed = 0;
        h.Engine.MoveCompleted += () => completed++;

        await RunMoveAsync(h, new Dictionary<int, double> { [0] = 30 });
        await RunMoveAsync(h, new Dictionary<int, double> { [1] = -20 });
        await RunMoveAsync(h, new Dictionary<int, double> { [0] = 10, [1] = 10 });

        Assert.Equal(3, completed);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);

        var measured = h.Engine.MeasuredPositionsDeg;
        Assert.InRange(measured[0], 9, 11);
        Assert.InRange(measured[1], 9, 11);
    }

    internal static async Task RunMoveAsync(Harness h, Dictionary<int, double> targets, int maxMs = 20000)
    {
        await h.Engine.MoveToAsync(targets);
        var waited = 0;
        while (h.Engine.State == RuntimeState.Executing && waited < maxMs)
        {
            h.Step(50);
            waited += 50;
        }
        h.Step(300); // settle
        Assert.True(h.Engine.State == RuntimeState.Enabled,
            $"Expected Enabled after move, got {h.Engine.State} " +
            $"(fault {h.Engine.ActiveFault}: {h.Engine.FaultDetail})");
    }

    [Fact]
    public async Task Telemetry_Forwarded_And_State_Events_Logged()
    {
        using var h = new Harness();
        var frames = 0;
        h.Engine.Telemetry += _ => frames++;

        await h.ConnectAndEnableAsync();
        h.Step(200);

        Assert.True(frames >= 10, $"expected telemetry flow, got {frames}");
        Assert.Contains(RuntimeState.Connected, h.StateLog);
        Assert.Contains(RuntimeState.Enabled, h.StateLog);
    }

    [Fact]
    public async Task Frames_Stream_During_Move_Then_Stop()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 80 });
        h.Step(500);
        Assert.True(h.Recording.TotalFrames > 10, "frames should stream during the move");
    }

    [Fact]
    public async Task SpeedOverride_Scales_Move()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        h.Engine.SpeedOverride = 0.25;

        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 40 });
        h.Step(1000);
        var quarter = h.Engine.MeasuredPositionsDeg[0];
        Assert.True(quarter < 35, $"at 25% speed after 1s should be well short of 40, got {quarter}");
        Assert.NotEqual(0, quarter);

        h.Step(15000);
        Assert.InRange(h.Engine.MeasuredPositionsDeg[0], 39, 41);
    }
}

public sealed class EngineStopTests
{
    [Fact]
    public async Task StopAsync_SoftStop_No_Frames_After_Stop()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });
        h.Step(300);
        var framesBefore = h.Recording.TotalFrames;

        await h.Engine.StopAsync(StopSeverity.SoftStop);

        h.Step(1000);
        Assert.Equal(framesBefore, h.Recording.TotalFrames);
        Assert.Equal(0, h.Recording.FramesAfterStop);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);
    }

    [Fact]
    public async Task Kill_Midmove_Stops_Hard()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });
        h.Step(300);
        var posAtKill = h.Sim.EstimatedPositionDeg(0);

        await h.Engine.StopAsync(StopSeverity.Kill);
        h.Step(300);

        Assert.Equal(posAtKill, h.Sim.EstimatedPositionDeg(0), 2);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);
    }

    [Fact]
    public async Task Stop_Races_Against_Concurrent_Pumping_Never_Sends_After()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });

        // Hammer stop from another thread while pumping on this one.
        var stopper = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await h.Engine.StopAsync(StopSeverity.SoftStop);
                await Task.Delay(2);
            }
        });
        for (var i = 0; i < 200; i++)
        {
            h.Step(10);
            await Task.Yield();
        }
        await stopper;

        h.Step(500);
        Assert.Equal(0, h.Recording.FramesAfterStop);
    }
}

public sealed class EnginePauseResumeTests
{
    [Fact]
    public async Task Pause_Holds_Resume_Replans_From_Measured_And_Completes()
    {
        using var h = new Harness();
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 60 });
        h.Step(1000); // roughly halfway (40°/s, ramp-limited)
        await h.Engine.PauseAsync();

        var pausedPos = h.Sim.EstimatedPositionDeg(0);
        Assert.InRange(pausedPos, 5, 55); // mid-move
        h.Step(2000); // decel and hold
        var heldPos = h.Sim.EstimatedPositionDeg(0);
        Assert.InRange(heldPos, pausedPos - 8, pausedPos + 8); // decel ramp v²/2a ≈ 6.7°

        await h.Engine.ResumeAsync();
        h.Step(10000);

        Assert.InRange(h.Engine.MeasuredPositionsDeg[0], 59, 61);
        Assert.Equal(RuntimeState.Enabled, h.Engine.State);
    }

    [Fact]
    public async Task Resumed_Stream_Starts_From_Measured_Position_No_Stale_Frames()
    {
        using var h = new Harness();
        var frames = new List<double>();
        h.Recording.OnFrame = f => frames.Add(f.Setpoints.Single(s => s.AxisId == 0).PositionDeg);

        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 60 });
        h.Step(1000);
        await h.Engine.PauseAsync();
        h.Step(1000);
        var pausedMeasured = h.Engine.MeasuredPositionsDeg[0];
        var frameCountAtResume = frames.Count;

        await h.Engine.ResumeAsync();
        h.Step(200);

        var postResume = frames.Skip(frameCountAtResume).ToList();
        Assert.NotEmpty(postResume);
        Assert.All(postResume, p => Assert.InRange(p, pausedMeasured - 2, 61));
    }
}
