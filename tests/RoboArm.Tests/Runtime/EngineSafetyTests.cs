using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Persistence;
using RoboArm.Runtime;
using RoboArm.Safety;
using Xunit;

namespace RoboArm.Tests.Runtime;

public sealed class EngineWatchdogTests
{
    [Fact]
    public async Task Telemetry_Staleness_Latches_CommLoss()
    {
        using var h = new Harness(Harness.DefaultConfig(new SafetyConfig(StreamWatchdogTimeoutMs: 200)));
        await h.ConnectAndEnableAsync();
        h.Step(300); // telemetry flows, no fault
        Assert.NotEqual(RuntimeState.Faulted, h.Engine.State);

        h.StepEngineOnly(400); // sim stops reporting; engine keeps pumping
        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        Assert.Equal(FaultReason.CommLoss, h.Engine.ActiveFault);
    }

    [Fact]
    public async Task Pump_Gap_Latches_MotionLoop_Stall()
    {
        using var h = new Harness(Harness.DefaultConfig(new SafetyConfig(MotionLoopHeartbeatTimeoutMs: 500)));
        await h.ConnectAndEnableAsync();
        h.Step(100);

        h.Clock.Advance(1000); // pump was starved
        h.Engine.Pump(h.Clock.NowMs);

        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        Assert.Equal(FaultReason.InternalError, h.Engine.ActiveFault);
    }

    [Fact]
    public async Task Frame_Stream_Starvation_MidMove_Latches_StreamWatchdog()
    {
        using var h = new Harness(Harness.DefaultConfig(
            new SafetyConfig(StreamWatchdogTimeoutMs: 150, MotionLoopHeartbeatTimeoutMs: 5000)));
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });
        h.Step(100);
        Assert.Equal(RuntimeState.Executing, h.Engine.State);

        // Simulate the stream starving while the loop keeps running: advance clock
        // without any frame being sent (long single jump defeats the frame-due check).
        h.Clock.Advance(400);
        h.Engine.Pump(h.Clock.NowMs);

        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        Assert.Equal(FaultReason.StreamWatchdog, h.Engine.ActiveFault);
    }

    [Fact]
    public async Task Watchdog_Fault_Kills_Target()
    {
        using var h = new Harness(Harness.DefaultConfig(new SafetyConfig(StreamWatchdogTimeoutMs: 200)));
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 90 });
        h.Step(100);

        h.StepEngineOnly(400);

        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        lock (h.Recording.Stops)
            Assert.Contains(StopSeverity.Kill, h.Recording.Stops);
    }
}

public sealed class EngineDivergenceTests
{
    [Fact]
    public async Task Commanded_Vs_Measured_Divergence_Latches_Fault_And_Kills()
    {
        using var h = new Harness(Harness.DefaultConfig(new SafetyConfig(PositionDivergenceThresholdDeg: 3)));
        await h.ConnectAndEnableAsync();
        h.Engine.SpeedOverride = 0.5; // following error ~1.7° stays under threshold
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 40 });
        h.Step(200);
        Assert.Equal(RuntimeState.Executing, h.Engine.State);

        h.Sim.InjectDivergence(0, 10.0); // simulates lost steps
        h.Step(100);

        Assert.Equal(RuntimeState.Faulted, h.Engine.State);
        Assert.Equal(FaultReason.PositionDivergence, h.Engine.ActiveFault);

        var framesAtFault = h.Recording.TotalFrames;
        h.Step(500);
        Assert.Equal(framesAtFault, h.Recording.TotalFrames); // no motion while faulted
    }
}

public sealed class AuditTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "roboarm-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Commands_And_Faults_Are_Audited_And_RoundTrip()
    {
        var sink = new JsonlAuditSink(_dir);
        using var h = new Harness(audit: sink);
        await h.ConnectAndEnableAsync();
        await h.Engine.MoveToAsync(new Dictionary<int, double> { [0] = 30 });
        h.Step(3000);
        h.Sim.InjectEStop();
        h.Step(100);
        await h.Engine.AcknowledgeFaultAsync();

        var file = Directory.GetFiles(_dir, "*.jsonl").Single();

        var records = JsonlAuditSink.ReadFile(file);
        Assert.Contains(records, r => r.Command.Contains("connect"));
        Assert.Contains(records, r => r.Command.Contains("enable"));
        Assert.Contains(records, r => r.Command == "move");
        Assert.Contains(records, r => r.Command == "fault");
        Assert.Contains(records, r => r.Command == "acknowledgeFault");
        var fault = records.Single(r => r.Command == "fault");
        Assert.Equal("EStop", fault.Parameters["reason"]);
        Assert.All(records, r => Assert.False(string.IsNullOrEmpty(r.Outcome)));
        Assert.All(records, r => Assert.Equal("engine", r.Source));
    }

    [Fact]
    public void Audit_Record_Json_RoundTrips()
    {
        var record = new AuditRecord(
            DateTimeOffset.Parse("2026-09-15T12:00:00Z"), "api", "move",
            new Dictionary<string, string> { ["0"] = "12.50" }, "accepted");
        var json = RoboArmJson.Serialize(record);
        var back = RoboArmJson.Deserialize<AuditRecord>(json);
        Assert.Equal(json, RoboArmJson.Serialize(back));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
