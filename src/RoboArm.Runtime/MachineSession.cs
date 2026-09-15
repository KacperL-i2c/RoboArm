using System.Runtime.InteropServices;
using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Protocol;
using RoboArm.Protocol.Transport;
using RoboArm.Safety;
using RoboArm.Simulator;
using RoboArm.Time;

namespace RoboArm.Runtime;

/// <summary>
/// Composition root: owns the motion target and the execution engine for one process
/// (docs/04: Runtime is the only owner of a live transport; frontends get the Engine).
/// Currently wires the Simulator; the USB transport lands here in Phase 3
/// (`CreateSimulated` becomes `CreateUsb`).
/// </summary>
public sealed class MachineSession : IDisposable
{
    private readonly IMotionTarget _target;
    private readonly SleepGuard _sleepGuard;
    private bool _disposed;

    public MachineConfig Config { get; }
    public ExecutionEngine Engine { get; }
    public bool OwnsTarget { get; }

    private MachineSession(MachineConfig config, IMotionTarget target, bool ownsTarget,
        IWallClock clock, IAuditSink? audit, SleepGuard? sleepGuard, bool autoPump)
    {
        Config = config;
        _target = target;
        OwnsTarget = ownsTarget;
        _sleepGuard = sleepGuard ?? SleepGuard.Instance;
        Engine = new ExecutionEngine(config, target, clock, audit, autoPump: autoPump);
        Engine.StateChanged += OnStateChanged;
    }

    /// <summary>Production path: default 5-axis machine against the built-in simulator.</summary>
    public static MachineSession CreateSimulated(string? auditDirectory = null) =>
        new(
            MachineConfig.CreateDefault5Axis(),
            new SimulatedMotionTarget(
                MachineConfig.CreateDefault5Axis(),
                tickHz: SimulatedMotionTarget.DefaultTickHz,
                autoTick: true),
            ownsTarget: true,
            SystemWallClock.Instance,
            auditDirectory is null ? null : new JsonlAuditSink(auditDirectory),
            sleepGuard: null,
            autoPump: true);

    /// <summary>Production path with a config file; simulator target.</summary>
    public static MachineSession CreateSimulated(MachineConfig config,
        string? auditDirectory = null) =>
        new(
            config,
            new SimulatedMotionTarget(config, tickHz: SimulatedMotionTarget.DefaultTickHz, autoTick: true),
            ownsTarget: true,
            SystemWallClock.Instance,
            auditDirectory is null ? null : new JsonlAuditSink(auditDirectory),
            sleepGuard: null,
            autoPump: true);

    /// <summary>
    /// USB path (pre-G0 skeleton): opens the CH340 COM port and logs traffic, but the
    /// board cannot move — the nMotion protocol is decoded in Phase 0 (docs/02).
    /// Pass an explicit port or leave null to auto-discover VID 1A86:7523.
    /// </summary>
    public static MachineSession CreateUsb(string? portName = null, string? auditDirectory = null)
    {
        if (portName is null)
        {
            var ports = new Ch340Discovery().List();
            if (ports.Count == 0)
                throw new InvalidOperationException(
                    "No CH340 (nMotion) board found. Plug it in or pass a COM port name.");
            portName = ports[0].PortName;
        }
        var channel = new SerialPortChannel(portName);
        return new(
            MachineConfig.CreateDefault5Axis(),
            new NmotionMotionTarget(channel),
            ownsTarget: true,
            SystemWallClock.Instance,
            auditDirectory is null ? null : new JsonlAuditSink(auditDirectory),
            sleepGuard: null,
            autoPump: true);
    }

    /// <summary>Test path: inject target + clock, manual pump.</summary>
    public static MachineSession CreateForTest(MachineConfig config, IMotionTarget target,
        IWallClock clock, IAuditSink? audit = null, SleepGuard? sleepGuard = null) =>
        new(config, target, ownsTarget: false, clock, audit, sleepGuard, autoPump: false);

    private void OnStateChanged(RuntimeState state) =>
        _sleepGuard.SetDrivesActive(state is RuntimeState.Enabled
            or RuntimeState.Executing or RuntimeState.Paused);

    /// <summary>
    /// Safe-close (docs/03 L5): stop any motion, drop enable, dispose the engine.
    /// Never throws; faults during shutdown are swallowed after an audit record.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_disposed)
            return;
        try
        {
            if (Engine.State is RuntimeState.Executing or RuntimeState.Paused)
                await Engine.StopAsync(StopSeverity.SoftStop);
            if (Engine.State == RuntimeState.Enabled)
                await Engine.EnableAsync(false);
        }
        catch (InvalidOperationException)
        {
            // Shutdown must proceed even if the FSM disagrees (e.g. mid-fault).
        }
        finally
        {
            _sleepGuard.SetDrivesActive(false);
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Engine.StateChanged -= OnStateChanged;
        Engine.Dispose();
        if (OwnsTarget)
            _target.Dispose();
    }
}

/// <summary>
/// Prevents Windows sleep / display-off while drives are active (H9). The interop is
/// injectable so the state logic is unit-testable.
/// </summary>
public sealed class SleepGuard(Action<bool>? interop = null)
{
    public static SleepGuard Instance { get; } = new();

    private readonly Action<bool> _interop = interop ?? SetExecutionState;
    private bool _preventing;

    public bool Preventing => _preventing;

    public void SetDrivesActive(bool active)
    {
        if (active == _preventing)
            return;
        _preventing = active;
        _interop(active);
    }

    private static void SetExecutionState(bool prevent)
    {
        const uint continuous = 0x80000000;
        const uint systemRequired = 0x00000001;
        var flags = prevent ? continuous | systemRequired : continuous;
        SetThreadExecutionState(flags);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
