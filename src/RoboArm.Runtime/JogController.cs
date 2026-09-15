using RoboArm.Machine;
using RoboArm.Safety;
using RoboArm.Time;

namespace RoboArm.Runtime;

/// <summary>
/// Hold-to-jog over the full safety path: every jog tick is an ordinary MoveToAsync
/// through planner + Guard + watchdogs — there is no bypassing motion channel.
/// Ticks are skipped while the engine is Executing (moves replace only from Enabled),
/// so effective jog feed is somewhat below nominal; the position always converges
/// to the clamped target when the button is released.
/// </summary>
public sealed class JogController : IDisposable
{
    /// <summary>Fraction of the axis v-max used as nominal jog feed.</summary>
    public const double JogSpeedFraction = 0.5;

    private readonly ExecutionEngine _engine;
    private readonly MachineConfig _config;
    private readonly IWallClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<int, int> _directions = []; // axisId -> ±1
    private readonly Dictionary<int, double> _targets = []; // axisId -> deg
    private long _lastTickMs = -1;
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public event Action<string>? JogFailed;

    public JogController(ExecutionEngine engine, MachineConfig config, IWallClock? clock = null,
        bool autoTick = true)
    {
        _engine = engine;
        _config = config;
        _clock = clock ?? SystemWallClock.Instance;
        if (autoTick)
            _timer = new System.Threading.Timer(_ => TickSafe(), null, 100, 100);
    }

    public bool IsJogging(int axisId)
    {
        lock (_sync)
            return _directions.ContainsKey(axisId);
    }

    /// <exception cref="InvalidOperationException">Axis unknown or machine not Enabled.</exception>
    public void Start(int axisId, int direction)
    {
        if (direction is not (1 or -1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var axis = _config.Axis(axisId)
            ?? throw new InvalidOperationException($"Unknown axis {axisId}.");
        if (_engine.State != RuntimeState.Enabled)
            throw new InvalidOperationException($"Jog requires Enabled (current: {_engine.State}).");

        lock (_sync)
        {
            _directions[axisId] = direction;
            if (!_targets.ContainsKey(axisId))
                _targets[axisId] = _engine.MeasuredPositionsDeg.GetValueOrDefault(axisId, axis.SoftLimitMinDeg);
        }
    }

    /// <summary>Releases an axis; remaining jogging axes keep their targets.</summary>
    public void Stop(int axisId)
    {
        lock (_sync)
            _directions.Remove(axisId);
    }

    public void StopAll()
    {
        lock (_sync)
            _directions.Clear();
    }

    /// <summary>Advances jog targets by elapsed time and issues a move when possible.</summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (_disposed || _directions.Count == 0)
            {
                _lastTickMs = _clock.NowMs;
                return;
            }
            var now = _clock.NowMs;
            var dt = _lastTickMs < 0 ? 0.1 : Math.Max(0.0, (now - _lastTickMs) / 1000.0);
            _lastTickMs = now;

            foreach (var (axisId, direction) in _directions)
            {
                var axis = _config.Axis(axisId)!;
                var feed = axis.MaxVelocityDegS * JogSpeedFraction * Math.Clamp(_engine.SpeedOverride, 0, 1);
                var next = _targets[axisId] + direction * feed * dt;
                _targets[axisId] = Math.Clamp(next, axis.SoftLimitMinDeg, axis.SoftLimitMaxDeg);
            }

            if (_engine.State != RuntimeState.Enabled)
                return; // previous jog move still finishing; servo keeps following it
            try
            {
                var move = _targets.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3));
                var t = _engine.MoveToAsync(move);
            }
            catch (InvalidOperationException ex)
            {
                _directions.Clear();
                JogFailed?.Invoke($"Jog aborted: {ex.Message}");
            }
        }
    }

    private void TickSafe()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            JogFailed?.Invoke($"Jog tick failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _directions.Clear();
        }
        _timer?.Dispose();
    }
}
