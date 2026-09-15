namespace RoboArm.Poses;

public sealed record PoseLibrary(
    IReadOnlyList<Pose> Poses,
    int Version = 1
);
