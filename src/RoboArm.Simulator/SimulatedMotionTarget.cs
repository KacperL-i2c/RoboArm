using System.Diagnostics;
using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Safety;
using RoboArm.Time;

namespace RoboArm.Simulator;

/// <summary>
/// Digital twin of the arm + board. Tracks commanded vs estimated positions with
/// per-axis velocity/acceleration limits, emits telemetry every tick, and supports
/// deterministic fault injection for safety testing (docs/03 verification matrix).
///
/// Time model: virtual simulation time advances one tick per Tick()/Advance() call in
/// manual mode, or via a background pacing thread in auto mode. All telemetry
/// timestamps come from this virtual clock, started at connect time.
/// </summary>
public sealed class SimulatedMotionTarget : IMotionTarget
{
    public const int DefaultTickHz = 100;

    private const double LimitToleranceDeg = 0.5;

    private readonly MachineConfig _config;
    private readonly IWallClock _clock;
    private readonly double _tickSeconds;
    private readonly object _sync = new();
    private readonly Dictionary<int, SimAxis> _axes = [];
    private readonly Thread? _autoThread;
    private readonly Stopwatch _pacing = new();

    private long _simTimeMs;
    private long _commLossUntilMs;
    private int _frameDelayTicks;
    private Queue<(long DueMs, MotionFrame Frame)>? _delayedFrames;
    private StopMode _stopMode = StopMode.None;
    private RuntimeState _state = RuntimeState.Offline;
    private bool _faulted;
    private bool _disposed;

    private enum StopMode { None, SoftStop, ControlledStop }

    private sealed class SimAxis(AxisConfig config)
    {
        public AxisConfig Config = config;
        public double SetpointPos;
        public double SetpointVel;
        public double EstimatedPos;
        public double Velocity;
        public bool Enabled; // disabled until EnableAsync(true)
    }

    public SimulatedMotionTarget(MachineConfig config, int tickHz = DefaultTickHz,
        bool autoTick = false, IWallClock? clock = null)
    {
        if (tickHz < 1 || tickHz > 1000)
            throw new ArgumentOutOfRangeException(nameof(tickHz));
        _config = config;
        _clock = clock ?? SystemWallClock.Instance;
        _simTimeMs = _clock.NowMs;
        _tickSeconds = 1.0 / tickHz;
        foreach (var axis in config.Axes)
            _axes[axis.Id] = new SimAxis(axis);
        if (autoTick)
        {
            _pacing.Start();
            _autoThread = new Thread(AutoLoop) { IsBackground = true, Name = "SimulatorTick" };
            _autoThread.Start();
        }
    }

    public event Action<TelemetryFrame>? TelemetryReceived;
    public event Action<FaultReason>? FaultDetected;

    /// <summary>Current virtual time in ms (matches telemetry timestamps).</summary>
    public long SimTimeMs
    {
        get { lock (_sync) return _simTimeMs; }
    }

    /// <summary>Current runtime state as seen by the target (never Executing/Paused).</summary>
    public RuntimeState State
    {
        get { lock (_sync) return _state; }
    }

    public double EstimatedPositionDeg(int axisId)
    {
        lock (_sync)
            return _axes[axisId].EstimatedPos;
    }

    // ---- Fault injection (test bench) ----

    /// <summary>Drops frame processing and telemetry emission until sim time advances by <paramref name="duration"/>.</summary>
    public void InjectCommLoss(TimeSpan duration)
    {
        lock (_sync)
            _commLossUntilMs = _simTimeMs + (long)duration.TotalMilliseconds;
    }

    /// <summary>Frames take effect <paramref name="ticks"/> ticks late (stale command simulation).</summary>
    public void InjectFrameDelay(int ticks)
    {
        lock (_sync)
        {
            _frameDelayTicks = Math.Max(0, ticks);
            _delayedFrames ??= new Queue<(long DueMs, MotionFrame Frame)>();
        }
    }

    /// <summary>Estimated position jumps by <paramref name="offsetDeg"/> (lost steps / mechanical slip).</summary>
    public void InjectDivergence(int axisId, double offsetDeg)
    {
        lock (_sync)
            _axes[axisId].EstimatedPos += offsetDeg;
    }

    /// <summary>Hardware E-stop pressed: immediate disable, velocity frozen, sticky fault.</summary>
    public void InjectEStop()
    {
        Action<FaultReason>? raise;
        lock (_sync)
        {
            foreach (var axis in _axes.Values)
            {
                axis.Enabled = false;
                axis.Velocity = 0;
            }
            _faulted = true;
            _state = RuntimeState.Faulted;
            raise = FaultDetected;
        }
        raise?.Invoke(FaultReason.EStop);
    }

    /// <summary>Simulates a board power cycle: clears all latched faults.</summary>
    public void ClearFaults()
    {
        lock (_sync)
        {
            _faulted = false;
            if (_state == RuntimeState.Faulted)
                _state = RuntimeState.Offline;
        }
    }

    // ---- Manual time control (deterministic tests) ----

    public void Tick() => TickInternal();

    public void Advance(TimeSpan duration)
    {
        var ticks = (int)Math.Round(duration.TotalSeconds / _tickSeconds);
        for (var i = 0; i < ticks; i++)
            TickInternal();
    }

    // ---- IMotionTarget ----

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_state != RuntimeState.Offline)
                return Task.CompletedTask;
            _state = RuntimeState.Connected;
        }
        return Task.CompletedTask;
    }

    public Task EnableAsync(bool enabled, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_faulted)
                return Task.CompletedTask; // sticky: requires ClearFaults (power cycle)
            if (_state == RuntimeState.Offline)
                throw new InvalidOperationException("Target is not connected.");
            if (_state != RuntimeState.Faulted)
                _state = enabled ? RuntimeState.Enabled : RuntimeState.Connected;
            foreach (var axis in _axes.Values)
            {
                axis.Enabled = enabled;
                if (!enabled)
                    axis.SetpointVel = 0;
            }
        }
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(MotionFrame frame, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_faulted || _state == RuntimeState.Offline || _simTimeMs < _commLossUntilMs)
                return Task.CompletedTask;

            if (_delayedFrames is { } queue && _frameDelayTicks > 0)
            {
                queue.Enqueue((_simTimeMs + (long)(_frameDelayTicks * _tickSeconds * 1000), frame));
                return Task.CompletedTask;
            }
            ApplyFrame(frame);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(StopSeverity severity, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _delayedFrames?.Clear(); // stale frames never replay after a stop (H7)
            switch (severity)
            {
                case StopSeverity.SoftStop:
                    _stopMode = StopMode.SoftStop;
                    foreach (var axis in _axes.Values)
                    {
                        axis.SetpointPos = axis.EstimatedPos; // freeze target, ramp out
                        axis.SetpointVel = 0;
                    }
                    break;
                case StopSeverity.ControlledStop:
                    _stopMode = StopMode.ControlledStop;
                    foreach (var axis in _axes.Values)
                    {
                        axis.SetpointPos = axis.EstimatedPos;
                        axis.SetpointVel = 0;
                    }
                    break;
                case StopSeverity.Kill:
                    foreach (var axis in _axes.Values)
                    {
                        axis.Enabled = false;
                        axis.Velocity = 0;
                        axis.SetpointVel = 0;
                    }
                    if (_state == RuntimeState.Enabled)
                        _state = RuntimeState.Connected;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(severity));
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
            _disposed = true;
    }

    // ---- Simulation core ----

    private void ApplyFrame(MotionFrame frame)
    {
        foreach (var sp in frame.Setpoints)
        {
            if (_axes.TryGetValue(sp.AxisId, out var axis))
            {
                axis.SetpointPos = sp.PositionDeg;
                axis.SetpointVel = sp.VelocityDegS;
            }
        }
        _stopMode = StopMode.None;
    }

    private void TickInternal()
    {
        TelemetryFrame? pendingFrame = null;
        Action<FaultReason>? fault = null;
        FaultReason faultReason = default;

        lock (_sync)
        {
            if (_disposed)
                return;

            // Release delayed frames whose due time has passed, in order.
            if (_delayedFrames is { Count: > 0 })
            {
                if (_faulted)
                    _delayedFrames.Clear(); // never replay stale motion after faults (H7)
                else
                {
                    while (_delayedFrames.Count > 0 && _delayedFrames.Peek().DueMs <= _simTimeMs)
                        ApplyFrame(_delayedFrames.Dequeue().Frame);
                }
            }

            var anyMoving = false;
            foreach (var axis in _axes.Values)
            {
                var cfg = axis.Config;
                var targetVel = 0.0;
                if (_stopMode != StopMode.None)
                {
                    // Ramping out: hold wherever the decel ramp currently is —
                    // never servo back toward the pre-stop target.
                    axis.SetpointPos = axis.EstimatedPos;
                }
                else if (axis.Enabled && !_faulted && _simTimeMs >= _commLossUntilMs)
                {
                    // Velocity-limited position servo: the fastest speed that can still
                    // decelerate to the setpoint within this axis's accel limit.
                    var error = axis.SetpointPos - axis.EstimatedPos;
                    var approach = Math.Sqrt(2.0 * cfg.MaxAccelDegS2 * Math.Abs(error));
                    targetVel = Math.Sign(error) * Math.Min(cfg.MaxVelocityDegS, approach);
                }

                var maxDelta = cfg.MaxAccelDegS2 * _tickSeconds;
                axis.Velocity = Math.Clamp(targetVel,
                    axis.Velocity - maxDelta, axis.Velocity + maxDelta);
                axis.EstimatedPos += axis.Velocity * _tickSeconds;
                if (Math.Abs(axis.Velocity) > 1e-9)
                    anyMoving = true;
            }

            if (_stopMode != StopMode.None && !anyMoving)
            {
                if (_stopMode == StopMode.ControlledStop)
                {
                    foreach (var axis in _axes.Values)
                        axis.Enabled = false;
                    if (_state == RuntimeState.Enabled)
                        _state = RuntimeState.Connected;
                }
                _stopMode = StopMode.None;
            }

            // Soft-limit enforcement: no commanded trajectory may cross a soft limit.
            if (!_faulted)
            {
                foreach (var axis in _axes.Values)
                {
                    if (axis.SetpointPos < axis.Config.SoftLimitMinDeg - LimitToleranceDeg
                        || axis.SetpointPos > axis.Config.SoftLimitMaxDeg + LimitToleranceDeg)
                    {
                        _faulted = true;
                        _state = RuntimeState.Faulted;
                        faultReason = FaultReason.SoftLimit;
                        foreach (var a in _axes.Values)
                        {
                            a.Enabled = false;
                            a.Velocity = 0;
                        }
                        fault = FaultDetected;
                        break;
                    }
                }
            }

            var telemetryDue = _state != RuntimeState.Offline
                && !_faulted && _simTimeMs >= _commLossUntilMs;
            if (telemetryDue)
            {
                var reports = _axes.Values
                    .Select(a => new AxisReport(a.Config.Id, a.SetpointPos, a.EstimatedPos, a.Enabled))
                    .ToList();
                pendingFrame = new TelemetryFrame(_simTimeMs, reports, _state);
            }

            _simTimeMs += (long)(_tickSeconds * 1000);
        }

        if (pendingFrame is not null)
            TelemetryReceived?.Invoke(pendingFrame);
        if (fault is not null)
            fault(faultReason);
    }

    private void AutoLoop()
    {
        var lastMs = _pacing.ElapsedMilliseconds;
        while (!_disposed)
        {
            Thread.Sleep(2);
            var nowMs = _pacing.ElapsedMilliseconds;
            var elapsedTicks = (int)((nowMs - lastMs) / (_tickSeconds * 1000));
            if (elapsedTicks <= 0)
                continue;
            lastMs += (long)(elapsedTicks * _tickSeconds * 1000);
            for (var i = 0; i < elapsedTicks; i++)
                TickInternal();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimulatedMotionTarget));
    }
}
