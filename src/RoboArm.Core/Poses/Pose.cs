namespace RoboArm.Poses;

public sealed record Pose(
    string Name,
    IReadOnlyDictionary<int, double> JointPositionsDeg,
    bool GripperClosed = false,
    string? Note = null,
    DateTimeOffset? CapturedAt = null
);
