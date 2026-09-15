using RoboArm.Motion;
using RoboArm.Protocol.Transport;
using RoboArm.Safety;

namespace RoboArm.Protocol;

/// <summary>
/// nMotion board motion target — SKELETON STAGE (pre-G0).
///
/// What it does today: opens/closes the CH340 serial port, logs every received byte
/// to a ring buffer for Phase-0 analysis, and drops all motion-related calls safely.
///
/// What it must NEVER do before the protocol is decoded: transmit a single byte whose
/// meaning we do not know (docs/03 rule zero — no uncommanded motion). Enable/frames
/// are therefore counted and dropped, not sent. Kill closes the port, which stops the
/// stream — the strongest safe action available before watchdog behavior is probed (S7).
/// </summary>
public sealed class NmotionMotionTarget : IMotionTarget
{
    private readonly ISerialChannel _channel;
    private readonly RingLog _rx = new();
    private readonly RingLog _tx = new(capacity: 16 * 1024);
    private long _enableRequests;
    private long _framesDropped;
    private bool _disposed;

    // Codec-pending: these fire only once the protocol decoder exists (post-G0).
    // Explicit add/remove keeps CS0067 honest until then.
    public event Action<TelemetryFrame>? TelemetryReceived { add { } remove { } }
    public event Action<FaultReason>? FaultDetected { add { } remove { } }

    public NmotionMotionTarget(ISerialChannel channel)
    {
        _channel = channel;
        _channel.DataReceived += OnData;
    }

    public bool IsOpen => _channel.IsOpen;
    public string PortName => _channel.PortName;
    public long EnableRequests => _enableRequests;
    public long FramesDropped => _framesDropped;
    public RingLog RxLog => _rx;
    public RingLog TxLog => _tx;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_channel.IsOpen)
            _channel.Open();
        return Task.CompletedTask;
    }

    public Task EnableAsync(bool enabled, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        // Codec pending (Phase 0): enabling is board-specific. Drop + count, never send.
        Interlocked.Increment(ref _enableRequests);
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(MotionFrame frame, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        // Codec pending: frame contents cannot be encoded yet. Drop + count, never send.
        Interlocked.Increment(ref _framesDropped);
        return Task.CompletedTask;
    }

    public Task StopAsync(StopSeverity severity, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (severity == StopSeverity.Kill && _channel.IsOpen)
        {
            // Strongest pre-protocol stop: cut the stream (board watchdog behavior
            // is probed at S7; until then assume silence is safest).
            _channel.Close();
        }
        return Task.CompletedTask;
    }

    private void OnData(byte[] data) => _rx.Append(data);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NmotionMotionTarget));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _channel.DataReceived -= OnData;
        _channel.Dispose();
    }
}
