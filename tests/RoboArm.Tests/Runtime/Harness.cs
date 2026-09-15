using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Runtime;
using RoboArm.Safety;
using RoboArm.Simulator;
using RoboArm.Time;

namespace RoboArm.Tests.Runtime;

public sealed class FakeClock : IWallClock
{
    private long _nowMs = 1_000_000;
    public long NowMs
    {
        get => _nowMs;
        set => _nowMs = value;
    }
    public void Advance(long ms) => _nowMs += ms;
}

/// <summary>Wraps a motion target and records frame/stop traffic for race assertions.</summary>
public sealed class RecordingTarget : IMotionTarget
{
    private readonly IMotionTarget _inner;
    private long _stopSequence;
    private long _framesAfterStop;
    private long _totalFrames;

    public RecordingTarget(IMotionTarget inner) => _inner = inner;

    public Action<MotionFrame>? OnFrame;

    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long FramesAfterStop => Interlocked.Read(ref _framesAfterStop);
    public List<StopSeverity> Stops { get; } = [];

    public event Action<TelemetryFrame>? TelemetryReceived
    {
        add => _inner.TelemetryReceived += value;
        remove => _inner.TelemetryReceived -= value;
    }
    public event Action<FaultReason>? FaultDetected
    {
        add => _inner.FaultDetected += value;
        remove => _inner.FaultDetected -= value;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task EnableAsync(bool enabled, CancellationToken ct = default) => _inner.EnableAsync(enabled, ct);

    public Task SendFrameAsync(MotionFrame frame, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalFrames);
        OnFrame?.Invoke(frame);
        if (Interlocked.Read(ref _stopSequence) > 0)
            Interlocked.Increment(ref _framesAfterStop);
        return _inner.SendFrameAsync(frame, ct);
    }

    public Task StopAsync(StopSeverity severity, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stopSequence);
        lock (Stops) Stops.Add(severity);
        return _inner.StopAsync(severity, ct);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Deterministic harness: simulator ticks, engine pumps, one shared virtual clock.
/// One Step() = one 10 ms tick of both.
/// </summary>
public sealed class Harness : IDisposable
{
    public readonly FakeClock Clock = new();
    public readonly SimulatedMotionTarget Sim;
    public readonly RecordingTarget Recording;
    public readonly ExecutionEngine Engine;
    public readonly List<RuntimeState> StateLog = [];

    public const int TickMs = 10;

    public Harness(MachineConfig? config = null, IAuditSink? audit = null)
    {
        Config = config ?? DefaultConfig();
        Sim = new SimulatedMotionTarget(Config, tickHz: 1000 / TickMs, clock: Clock);
        Recording = new RecordingTarget(Sim);
        Engine = new ExecutionEngine(Config, Recording, Clock, audit);
        Engine.StateChanged += s => StateLog.Add(s);
    }

    public MachineConfig Config { get; }

    public static MachineConfig DefaultConfig(SafetyConfig? safety = null) =>
        new("test",
        [
            new AxisConfig(0, "base", MaxVelocityDegS: 40, MaxAccelDegS2: 120,
                SoftLimitMinDeg: -110, SoftLimitMaxDeg: 110),
            new AxisConfig(1, "elbow", MaxVelocityDegS: 20, MaxAccelDegS2: 80,
                SoftLimitMinDeg: -100, SoftLimitMaxDeg: 100),
        ], Safety: safety);

    public async Task ConnectAndEnableAsync()
    {
        await Engine.ConnectAsync();
        await Engine.EnableAsync(true);
    }

    /// <summary>Steps the whole stack by <paramref name="ms"/> in 10 ms ticks.</summary>
    public void Step(int ms = TickMs)
    {
        for (var elapsed = 0; elapsed < ms; elapsed += TickMs)
        {
            Clock.Advance(TickMs);
            Sim.Tick();
            Engine.Pump(Clock.NowMs);
        }
    }

    /// <summary>Advances engine time without simulator ticks (comm-loss simulation).</summary>
    public void StepEngineOnly(int ms)
    {
        for (var elapsed = 0; elapsed < ms; elapsed += TickMs)
        {
            Clock.Advance(TickMs);
            Engine.Pump(Clock.NowMs);
        }
    }

    public void Dispose() => Engine.Dispose();
}
