using System.Text.Json.Serialization;

namespace RoboArm.Machine;

public sealed record HomingConfig(
    double SeekSpeedDegS = 5.0,
    double CreepSpeedDegS = 1.0,
    bool SeekPositive = false,
    double BackoffDeg = 2.0,
    int LimitSwitchInput = -1
);

public sealed record AxisConfig(
    int Id,
    string Name,
    double StepsPerDegree = 100.0,
    double MaxVelocityDegS = 30.0,
    double MaxAccelDegS2 = 60.0,
    double SoftLimitMinDeg = -120.0,
    double SoftLimitMaxDeg = 120.0,
    bool InvertDirection = false,
    bool EnableActiveLow = true,
    HomingConfig? Homing = null
)
{
    public const double AbsoluteMaxVelocityDegS = 360.0;
    public const double AbsoluteMaxAccelDegS2 = 2000.0;

    [JsonIgnore]
    public bool IsValid =>
        StepsPerDegree > 0
        && MaxVelocityDegS is > 0 and <= AbsoluteMaxVelocityDegS
        && MaxAccelDegS2 is > 0 and <= AbsoluteMaxAccelDegS2
        && SoftLimitMinDeg < SoftLimitMaxDeg;
}
