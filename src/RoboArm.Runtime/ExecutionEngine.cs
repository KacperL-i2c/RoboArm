using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Runtime.Safety;
using RoboArm.Safety;
using RoboArm.Time;

namespace RoboArm.Runtime;

/// <summary>
/// Single authoritative execution engine (docs/04): one per process. Owns the motion
/// target, the FSM, watchdogs and the frame stream. Every frontend (WPF, API, MCP)
/// goes through this class — nothing else may touch the target.
///
/// Time model: Pump(nowMs) advances the engine deterministically (tests drive it with
/// the simulator's virtual clock; production uses the auto loop over the wall clock).
/// </summary>
public sealed class ExecutionEngine : IDisposable
{
    private readonly MachineConfig _config;
    private readonly IMotionTarget _target;
    private readonly IWallClock _clock;
    private readonly IAuditSink? _audit;
    private readonly double _framePeriodSeconds;

    private readonly object _sync = new();
    private RuntimeState _state = RuntimeState.Offline;
    private FaultReason? _faultReason;
    private string? _faultDetail;
    private bool _requiresRehome;

    private MotionPlan? _plan;
    private long _planStartMs;
    private double _planElapsedSeconds;
    private double _speedOverride = 1.0;
    private Dictionary<int, double>? _pendingTargets;

    private readonly Dictionary<int, double> _measured = [];
    private readonly Dictionary<int, double> _commanded = [];
    private long _lastFrameSentMs = -1;
    private long _lastTelemetryMs = -1;
    private long _lastPumpMs = -1;
    private bool _telemetryEverReceived;

    private Thread? _autoLoop;
    private bool _disposed;

    public const double DefaultFramePeriodSeconds = 0.01;

    public ExecutionEngine(MachineConfig config, IMotionTarget target,
        IWallClock? clock = null, IAuditSink? audit = null,
        double framePeriodSeconds = DefaultFramePeriodSeconds,
        bool autoPump = false)
    {
        _config = config;
        _target = target;
        _clock = clock ?? SystemWallClock.Instance;
        _audit = audit;
        _framePeriodSeconds = framePeriodSeconds;
        foreach (var axis in config.Axes)
        {
            _measured[axis.Id] = 0;
            _commanded[axis.Id] = 0;
        }
        _target.FaultDetected += OnTargetFault;
        _target.TelemetryReceived += OnTelemetry;
        if (autoPump)
        {
            _autoLoop = new Thread(AutoPumpLoop) { IsBackground = true, Name = "ExecutionEnginePump" };
            _autoLoop.Start();
        }
    }

    public event Action<RuntimeState>? StateChanged;
    public event Action<TelemetryFrame>? Telemetry;
    public event Action<FaultReason, string>? Faulted;
    public event Action? MoveCompleted;

    public RuntimeState State { get { lock (_sync) return _state; } }
    public FaultReason? ActiveFault { get { lock (_sync) return _faultReason; } }
    public string? FaultDetail { get { lock (_sync) return _faultDetail; } }

    /// <summary>After a fault acknowledgment, re-homing is mandatory before enabling (docs/03).</summary>
    public bool RequiresRehome { get { lock (_sync) return _requiresRehome; } }

    /// <summary>Global speed override 0..1 applied to every plan (L6).</summary>
    public double SpeedOverride
    {
        get { lock (_sync) return _speedOverride; }
        set { lock (_sync) _speedOverride = Math.Clamp(value, 0, 1); }
    }

    public IReadOnlyDictionary<int, double> MeasuredPositionsDeg
    {
        get { lock (_sync) return new Dictionary<int, double>(_measured); }
    }

    // ---- Connection / enable ----

    public Task ConnectAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExecutionEngine));
            var (ok, error) = TryTransitionLocked(RuntimeState.Connected, "connect");
            if (!ok)
                throw new InvalidOperationException(error);
        }
        _target.ConnectAsync(ct); // completes synchronously for our transports
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var (ok, error) = TryTransitionLocked(RuntimeState.Offline, "disconnect");
            if (!ok)
                throw new InvalidOperationException(error);
            _plan = null;
            _pendingTargets = null;
            _lastTelemetryMs = -1;
            _telemetryEverReceived = false;
            _lastFrameSentMs = -1;
        }
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    public Task EnableAsync(bool enabled, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_state == RuntimeState.Faulted)
                throw new InvalidOperationException("Cannot enable while Faulted — acknowledge the fault first.");
            if (enabled && _requiresRehome)
                throw new InvalidOperationException("Re-home required after fault recovery before enabling.");
            var (ok, error) = enabled
                ? TryTransitionLocked(RuntimeState.Enabled, "enable")
                : TryTransitionLocked(RuntimeState.Connected, "disable");
            if (!ok)
                throw new InvalidOperationException(error);
        }
        _target.EnableAsync(enabled, ct);
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    // ---- Motion ----

    public Task MoveToAsync(IReadOnlyDictionary<int, double> targetsDeg,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_state != RuntimeState.Enabled)
                throw new InvalidOperationException(
                    $"Move requires state Enabled (current: {_state}).");
            var (ok, error) = TryTransitionLocked(RuntimeState.Executing, "move");
            if (!ok)
                throw new InvalidOperationException(error);

            var starts = _config.Axes
                .Select(a => new AxisPlanStart(a.Id, _measured.GetValueOrDefault(a.Id), 0))
                .ToList();
            try
            {
                _plan = MotionPlan.Plan(_config, new PlanRequest(starts, targetsDeg, _speedOverride));
            }
            catch (PlannerException ex)
            {
                TransitionLocked(RuntimeState.Enabled, "plan-reject");
                throw new InvalidOperationException(ex.Message, ex);
            }
            _planStartMs = _clock.NowMs;
            _planElapsedSeconds = 0;
            _pendingTargets = null;
            _lastFrameSentMs = -1; // TX watchdog re-arms for the new stream
        }
        Audit("move", targetsDeg.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString("F2")), "accepted");
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_state != RuntimeState.Executing)
                return Task.CompletedTask;
            _pendingTargets = RemainingTargetsLocked();
            _plan = null; // buffered frames are never replayed (H7)
            TransitionLocked(RuntimeState.Paused, "pause");
        }
        _target.StopAsync(StopSeverity.SoftStop, ct);
        Audit("pause", new Dictionary<string, string>(), "accepted");
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_state != RuntimeState.Paused)
                return Task.CompletedTask;
            var targets = _pendingTargets ?? [];
            var starts = _config.Axes
                .Select(a => new AxisPlanStart(a.Id, _measured.GetValueOrDefault(a.Id), 0))
                .ToList();
            try
            {
                _plan = MotionPlan.Plan(_config, new PlanRequest(starts, targets, _speedOverride));
            }
            catch (PlannerException ex)
            {
                EnterFaultLocked(FaultReason.InternalError, $"Resume re-plan failed: {ex.Message}");
                throw new InvalidOperationException(ex.Message, ex);
            }
            _planStartMs = _clock.NowMs;
            _planElapsedSeconds = 0;
            _pendingTargets = null;
            _lastFrameSentMs = -1; // TX watchdog re-arms for the resumed stream
            TransitionLocked(RuntimeState.Executing, "resume");
        }
        Audit("resume", new Dictionary<string, string>(), "accepted");
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    public Task StopAsync(StopSeverity severity, CancellationToken ct = default)
    {
        lock (_sync)
        {
            // Atomic flush: the plan is dropped under the same lock that Guard checks,
            // so no frame can be dispatched after this point.
            _plan = null;
            _pendingTargets = null;
            switch (_state)
            {
                case RuntimeState.Executing:
                case RuntimeState.Paused:
                    TransitionLocked(RuntimeState.Enabled, "stop");
                    break;
            }
        }
        _target.StopAsync(severity, ct);
        Audit("stop", new Dictionary<string, string> { ["severity"] = severity.ToString() }, "accepted");
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    /// <summary>Human-only fault acknowledgment. Fault → Connected, re-home required (docs/03 L3).</summary>
    public Task AcknowledgeFaultAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_state != RuntimeState.Faulted)
                throw new InvalidOperationException("No fault to acknowledge.");
            _faultReason = null;
            _faultDetail = null;
            _requiresRehome = true;
            _plan = null;
            _pendingTargets = null;
            TransitionLocked(RuntimeState.Connected, "ack");
        }
        Audit("acknowledgeFault", new Dictionary<string, string>(), "fault cleared; re-home required");
        FlushPendingEvents();
        return Task.CompletedTask;
    }

    /// <summary>Mark the machine as re-homed (called after a homing procedure completes).</summary>
    public Task MarkHomedAsync(CancellationToken ct = default)
    {
        lock (_sync)
            _requiresRehome = false;
        return Task.CompletedTask;
    }

    // ---- Engine pump ----

    /// <summary>
    /// Advances the engine to <paramref name="nowMs"/>: checks watchdogs, streams due
    /// plan frames through the Guard, detects completion. Call at ≥ frame cadence.
    /// </summary>
    public void Pump(long nowMs)
    {
        List<Action> events;
        MotionFrame? frame;

        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExecutionEngine));

            // Watchdog: motion-loop heartbeat (a stalled pump is a stalled engine).
            if (_lastPumpMs >= 0 && nowMs - _lastPumpMs > _config.SafetyOrDefault.MotionLoopHeartbeatTimeoutMs)
                EnterFaultLocked(FaultReason.InternalError,
                    $"Motion loop stalled: {nowMs - _lastPumpMs} ms between pumps.");
            _lastPumpMs = nowMs;

            // Watchdog: TX cadence — the frame stream must not starve mid-move
            // (checked first so a starved stream is reported as StreamWatchdog
            // even when telemetry has gone stale for the same reason).
            if (_state == RuntimeState.Executing && _lastFrameSentMs >= 0
                && nowMs - _lastFrameSentMs > _config.SafetyOrDefault.StreamWatchdogTimeoutMs)
                EnterFaultLocked(FaultReason.StreamWatchdog,
                    $"Frame stream starved: {nowMs - _lastFrameSentMs} ms since last frame.");

            // Watchdog: RX staleness — telemetry must keep flowing.
            if (_telemetryEverReceived && _lastTelemetryMs >= 0
                && nowMs - _lastTelemetryMs > _config.SafetyOrDefault.StreamWatchdogTimeoutMs)
                EnterFaultLocked(FaultReason.CommLoss,
                    $"Telemetry stale: {nowMs - _lastTelemetryMs} ms.");

            frame = null;
            if (_state == RuntimeState.Executing && _plan is { } plan)
            {
                // Stream every frame that came due since the last pump (catch-up
                // after scheduling jitter); bounded by the plan duration.
                var dueSeconds = (nowMs - _planStartMs) / 1000.0;
                while (_planElapsedSeconds + _framePeriodSeconds
                    <= Math.Min(dueSeconds, plan.DurationSeconds) + 1e-9)
                {
                    _planElapsedSeconds += _framePeriodSeconds;
                    var sample = plan.Sample(_planElapsedSeconds);
                    var candidate = new MotionFrame(nowMs, sample);
                    if (!Guard.CheckFrame(_state, _config, candidate, _speedOverride, out var violation))
                    {
                        EnterFaultLocked(FaultReason.InternalError, $"Guard rejected frame: {violation}.");
                        break;
                    }
                    frame = candidate; // last due frame wins — targets are absolute
                    foreach (var sp in sample)
                        _commanded[sp.AxisId] = sp.PositionDeg;
                    _lastFrameSentMs = nowMs;
                }

                // Delivery-based completion: once the plan window has elapsed, push the
                // exact final setpoint and complete — no completion without delivery.
                if (_state == RuntimeState.Executing && dueSeconds >= plan.DurationSeconds - 1e-9)
                {
                    if (_planElapsedSeconds < plan.DurationSeconds)
                    {
                        var final = plan.Sample(plan.DurationSeconds);
                        var lastFrame = new MotionFrame(nowMs, final);
                        if (Guard.CheckFrame(_state, _config, lastFrame, _speedOverride, out var violation))
                        {
                            frame = lastFrame;
                            _lastFrameSentMs = nowMs;
                        }
                        else
                        {
                            EnterFaultLocked(FaultReason.InternalError,
                                $"Guard rejected final frame: {violation}.");
                        }
                    }
                    if (_state == RuntimeState.Executing)
                    {
                        foreach (var sp in plan.Sample(plan.DurationSeconds))
                        {
                            _commanded[sp.AxisId] = sp.PositionDeg;
                            _measured[sp.AxisId] = sp.PositionDeg;
                        }
                        _plan = null;
                        _planElapsedSeconds = plan.DurationSeconds;
                        TransitionLocked(RuntimeState.Enabled, "move-complete");
                        _pendingEvents.Add(() => MoveCompleted?.Invoke());
                    }
                }
            }

            events = CollectPendingEvents();
            if (frame is not null)
            {
                // Dispatch under the lock: StopAsync/PauseAsync must never observe
                // "stopped" while a frame is still in flight (H7). Target implementations
                // must not raise events under their own locks (see IMotionTarget contract).
                var t = _target.SendFrameAsync(frame, CancellationToken.None);
            }
        }

        foreach (var action in events)
            action();
    }

    // ---- FSor wiring ----

    private void OnTargetFault(FaultReason reason)
    {
        List<Action> events;
        lock (_sync)
        {
            EnterFaultLocked(reason, "Reported by motion target.");
            events = CollectPendingEvents();
        }
        foreach (var action in events)
            action();
    }

    private void OnTelemetry(TelemetryFrame frame)
    {
        List<Action> events = [];
        var forward = true;
        lock (_sync)
        {
            _lastTelemetryMs = frame.TimestampMs;
            _telemetryEverReceived = true;
            var threshold = _config.SafetyOrDefault.PositionDivergenceThresholdDeg;
            foreach (var report in frame.Axes)
            {
                _measured[report.AxisId] = report.EstimatedPositionDeg;
                var commanded = _commanded.GetValueOrDefault(report.AxisId);
                if (_state == RuntimeState.Executing
                    && Math.Abs(commanded - report.EstimatedPositionDeg) > threshold)
                {
                    EnterFaultLocked(FaultReason.PositionDivergence,
                        $"Axis {report.AxisId}: commanded {commanded:F2}° vs measured {report.EstimatedPositionDeg:F2}°.");
                    forward = false;
                    break;
                }
            }
            if (!forward)
                events = CollectPendingEvents();
        }
        foreach (var action in events)
            action();
        if (forward)
            Telemetry?.Invoke(frame);
    }

    // ---- FSM internals (all under _sync) ----

    private static readonly (RuntimeState From, RuntimeState To)[] LegalTransitions =
    [
        (RuntimeState.Offline, RuntimeState.Connected),
        (RuntimeState.Connected, RuntimeState.Offline),
        (RuntimeState.Connected, RuntimeState.Enabled),
        (RuntimeState.Enabled, RuntimeState.Connected),
        (RuntimeState.Enabled, RuntimeState.Executing),
        (RuntimeState.Executing, RuntimeState.Enabled),
        (RuntimeState.Executing, RuntimeState.Paused),
        (RuntimeState.Paused, RuntimeState.Executing),
        (RuntimeState.Paused, RuntimeState.Enabled),
        (RuntimeState.Faulted, RuntimeState.Connected), // via acknowledgeFault only
    ];

    public bool CanTransition(RuntimeState to)
    {
        lock (_sync)
            return CanTransitionLocked(to);
    }

    private bool CanTransitionLocked(RuntimeState to) =>
        LegalTransitions.Contains((_state, to));

    private (bool Ok, string Error) TryTransition(RuntimeState to, string action)
    {
        lock (_sync)
        {
            var (ok, error) = TryTransitionLocked(to, action);
            return (ok, error);
        }
    }

    private (bool Ok, string Error) TryTransitionLocked(RuntimeState to, string action)
    {
        if (!CanTransitionLocked(to))
        {
            if (_state == RuntimeState.Faulted)
                return (false, $"Cannot {action} while Faulted ({_faultReason}); acknowledge the fault first.");
            return (false, $"Illegal transition {_state} -> {to} for action '{action}'.");
        }
        TransitionLocked(to, action);
        return (true, "");
    }

    private void TransitionLocked(RuntimeState to, string action)
    {
        if (_state == to)
            return;
        if (!CanTransitionLocked(to))
            throw new InvalidOperationException($"Illegal transition {_state} -> {to} ('{action}').");
        _state = to;
        _pendingEvents.Add(() => StateChanged?.Invoke(to));
        Audit($"state:{action}", new Dictionary<string, string> { ["to"] = to.ToString() }, "ok");
    }

    private void EnterFaultLocked(FaultReason reason, string detail)
    {
        if (_state == RuntimeState.Faulted)
            return; // first fault wins; faults are sticky
        _faultReason = reason;
        _faultDetail = detail;
        _plan = null;
        _pendingTargets = null;
        _state = RuntimeState.Faulted;
        _pendingEvents.Add(() => StateChanged?.Invoke(RuntimeState.Faulted));
        _pendingEvents.Add(() => Faulted?.Invoke(reason, detail));
        _pendingEvents.Add(() => _target.StopAsync(StopSeverity.Kill, CancellationToken.None));
        Audit("fault", new Dictionary<string, string>
        {
            ["reason"] = reason.ToString(),
            ["detail"] = detail,
        }, "latched");
    }

    private Dictionary<int, double> RemainingTargetsLocked() =>
        _plan is { } plan
            ? plan.Sample(plan.DurationSeconds).ToDictionary(s => s.AxisId, s => s.PositionDeg)
            : [];

    // Events are queued under the lock and raised outside it to avoid re-entrancy.
    private readonly List<Action> _pendingEvents = [];

    private List<Action> CollectPendingEvents()
    {
        var events = new List<Action>(_pendingEvents);
        _pendingEvents.Clear();
        return events;
    }

    /// <summary>Drains queued events outside the lock (commands flush immediately).</summary>
    private void FlushPendingEvents()
    {
        List<Action> events;
        lock (_sync)
            events = CollectPendingEvents();
        foreach (var action in events)
            action();
    }

    private void Audit(string command, IReadOnlyDictionary<string, string> parameters, string outcome) =>
        _audit?.Write(new AuditRecord(DateTimeOffset.UtcNow, "engine", command, parameters, outcome));

    private void AutoPumpLoop()
    {
        while (!_disposed)
        {
            try
            {
                Pump(_clock.NowMs);
            }
            catch (ObjectDisposedException)
            {
                return; // disposed between loop check and pump
            }
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _plan = null;
        }
        _target.FaultDetected -= OnTargetFault;
        _target.TelemetryReceived -= OnTelemetry;
        _target.Dispose();
    }
}
