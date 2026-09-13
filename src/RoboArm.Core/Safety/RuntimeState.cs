namespace RoboArm.Safety;

public enum RuntimeState
{
    Offline,
    Connected,
    Enabled,
    Executing,
    Paused,
    Faulted
}

public enum FaultReason
{
    EStop,
    HardLimit,
    SoftLimit,
    StreamWatchdog,
    CommLoss,
    PositionDivergence,
    ConfigInvalid,
    InternalError
}
