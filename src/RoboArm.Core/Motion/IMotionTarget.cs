using RoboArm.Safety;

namespace RoboArm.Motion;

public enum StopSeverity
{
    SoftStop,
    ControlledStop,
    Kill
}

public sealed record MotionFrame(
    long TimestampMs,
    IReadOnlyList<AxisSetpoint> Setpoints
);

public sealed record AxisSetpoint(
    int AxisId,
    double PositionDeg,
    double VelocityDegS
);

/// <summary>
/// The live motion target (board transport or simulator). Contract:
/// async methods complete without holding internal locks, and events are never raised
/// under an internal lock — the Runtime dispatches frames while holding its own lock.
/// </summary>
public interface IMotionTarget : IDisposable
{
    event Action<TelemetryFrame>? TelemetryReceived;
    event Action< FaultReason>? FaultDetected;

    Task ConnectAsync(CancellationToken ct);
    Task EnableAsync(bool enabled, CancellationToken ct);
    Task SendFrameAsync(MotionFrame frame, CancellationToken ct);
    Task StopAsync(StopSeverity severity, CancellationToken ct);
}

public sealed record TelemetryFrame(
    long TimestampMs,
    IReadOnlyList<AxisReport> Axes,
    RuntimeState State
);

public sealed record AxisReport(
    int AxisId,
    double CommandedPositionDeg,
    double EstimatedPositionDeg,
    bool Enabled
);
