using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;
using RoboArm.Simulator;
using Xunit;

namespace RoboArm.Tests.Runtime;

public sealed class MachineSessionTests
{
    private static (MachineSession Session, SimulatedMotionTarget Sim, RecordingTarget Recording, FakeClock Clock)
        ManualSession()
    {
        var config = Harness.DefaultConfig();
        var clock = new FakeClock();
        var sim = new SimulatedMotionTarget(config, tickHz: 100, clock: clock);
        var recording = new RecordingTarget(sim);
        var session = MachineSession.CreateForTest(config, recording, clock);
        return (session, sim, recording, clock);
    }

    private static async Task<(MachineSession, SimulatedMotionTarget, RecordingTarget, FakeClock)> ConnectedAsync()
    {
        var h = ManualSession();
        await h.Session.Engine.ConnectAsync();
        await h.Session.Engine.EnableAsync(true);
        return h;
    }

    private static void Step(ref FakeClock clock, SimulatedMotionTarget sim,
        ExecutionEngine engine, int ms = 10)
    {
        for (var e = 0; e < ms; e += 10)
        {
            clock.Advance(10);
            sim.Tick();
            engine.Pump(clock.NowMs);
        }
    }

    [Fact]
    public async Task Session_Wires_Engine_To_Target_And_Completes_Moves()
    {
        var (session, sim, _, clock) = await ConnectedAsync();
        using var _ = session;

        var done = new TaskCompletionSource();
        session.Engine.MoveCompleted += () => done.TrySetResult();
        await session.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 15 });
        var waited = 0;
        while (!done.Task.IsCompleted && waited < 20000)
        {
            Step(ref clock, sim, session.Engine, 50);
            waited += 50;
        }
        Step(ref clock, sim, session.Engine, 500); // settle

        Assert.True(done.Task.IsCompleted, "move never completed");
        Assert.InRange(session.Engine.MeasuredPositionsDeg[0], 14, 16);
    }

    [Fact]
    public async Task ShutdownAsync_Stops_Motion_Then_Disables_Then_Disposes()
    {
        var (session, sim, recording, clock) = await ConnectedAsync();

        await session.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });
        Step(ref clock, sim, session.Engine, 300);
        Assert.Equal(RuntimeState.Executing, session.Engine.State);
        var stopsBefore = recording.Stops.Count;

        await session.ShutdownAsync();

        Assert.True(stopsBefore < recording.Stops.Count, "a stop must reach the target");
        Assert.True(session.Engine.State is RuntimeState.Connected or RuntimeState.Enabled
            or RuntimeState.Offline,
            $"unexpected final state {session.Engine.State}");
        // Pumping after shutdown is a no-op; engine is disposed.
        Assert.Throws<ObjectDisposedException>(() => session.Engine.Pump(clock.NowMs));
    }

    [Fact]
    public async Task ShutdownAsync_From_Faulted_Still_Disposes()
    {
        var (session, sim, _, clock) = await ConnectedAsync();
        sim.InjectEStop();
        Step(ref clock, sim, session.Engine, 100);
        Assert.Equal(RuntimeState.Faulted, session.Engine.State);

        await session.ShutdownAsync();
        Assert.Throws<ObjectDisposedException>(() => session.Engine.Pump(clock.NowMs));
    }

    [Fact]
    public async Task Sleep_Guard_Tracks_Drive_Active_States()
    {
        var calls = new List<bool>();
        var config = Harness.DefaultConfig();
        var clock = new FakeClock();
        var sim = new SimulatedMotionTarget(config, tickHz: 100, clock: clock);
        using var session = MachineSession.CreateForTest(config, sim, clock,
            sleepGuard: new SleepGuard(calls.Add));

        await session.Engine.ConnectAsync();          // Connected: not drive-active
        await session.Engine.EnableAsync(true);       // Enabled: prevent sleep
        await session.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 5 });
        Step(ref clock, sim, session.Engine, 600);    // move completes -> still Enabled
        await session.Engine.EnableAsync(false);      // Connected: allow sleep
        await session.Engine.DisconnectAsync();       // Offline

        Assert.Equal([true, false], calls);
    }
}

public sealed class SleepGuardTests
{
    [Fact]
    public void Only_Toggles_On_State_Change()
    {
        var calls = new List<bool>();
        var guard = new SleepGuard(calls.Add);

        guard.SetDrivesActive(true);
        guard.SetDrivesActive(true);
        guard.SetDrivesActive(false);
        guard.SetDrivesActive(false);
        guard.SetDrivesActive(true);

        Assert.Equal([true, false, true], calls);
        Assert.True(guard.Preventing);
    }
}
