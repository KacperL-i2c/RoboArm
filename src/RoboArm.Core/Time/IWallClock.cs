namespace RoboArm.Time;

/// <summary>
/// Monotonic millisecond clock. Abstracted so watchdogs and simulators are
/// deterministic under test (fake clock, no sleeping).
/// </summary>
public interface IWallClock
{
    long NowMs { get; }
}
