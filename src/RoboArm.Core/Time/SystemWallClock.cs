namespace RoboArm.Time;

public sealed class SystemWallClock : IWallClock
{
    public static SystemWallClock Instance { get; } = new();

    public long NowMs => Environment.TickCount64;
}
