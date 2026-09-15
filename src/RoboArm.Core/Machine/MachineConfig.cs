using System.Text.Json.Serialization;

namespace RoboArm.Machine;

public sealed record SafetyConfig(
    int StreamWatchdogTimeoutMs = 250,
    int MotionLoopHeartbeatTimeoutMs = 500,
    int SoftStopMaxMs = 10,
    int KillMaxMs = 10,
    int AiSessionSpeedCapPercent = 25,
    bool JogRequiresEnablingSwitch = false,
    double PositionDivergenceThresholdDeg = 15.0
);

public sealed record MachineConfig(
    string Name,
    IReadOnlyList<AxisConfig> Axes,
    SafetyConfig? Safety = null,
    int ApiPort = 8787,
    string ConfigVersion = "1"
)
{
    [JsonIgnore]
    public SafetyConfig SafetyOrDefault => Safety ?? new SafetyConfig();

    public const int MaxAxes = 8;

    public AxisConfig? Axis(int id) => Axes.FirstOrDefault(a => a.Id == id);

    public bool Validate(out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        if (Axes.Count == 0)
            list.Add("At least one axis is required.");
        if (Axes.Count > MaxAxes)
            list.Add($"At most {MaxAxes} axes are supported (got {Axes.Count}).");
        if (Axes.Select(a => a.Id).Distinct().Count() != Axes.Count)
            list.Add("Axis ids must be unique.");
        if (ApiPort is < 1 or > 65535)
            list.Add($"ApiPort {ApiPort} is out of range.");
        foreach (var axis in Axes)
        {
            if (!axis.IsValid)
                list.Add($"Axis '{axis.Name}' (id {axis.Id}) has invalid parameters.");
            if (Math.Abs(axis.SoftLimitMinDeg) > 360 || Math.Abs(axis.SoftLimitMaxDeg) > 360)
                list.Add($"Axis '{axis.Name}' (id {axis.Id}): soft limits must stay within ±360°.");
            if (axis.Homing is { } homing)
            {
                if (homing.LimitSwitchInput is < -1)
                    list.Add($"Axis '{axis.Name}' (id {axis.Id}): LimitSwitchInput must be >= -1.");
                if (homing.SeekSpeedDegS <= 0 || homing.CreepSpeedDegS <= 0 || homing.BackoffDeg < 0)
                    list.Add($"Axis '{axis.Name}' (id {axis.Id}): invalid homing parameters.");
            }
        }
        var safety = SafetyOrDefault;
        if (safety.StreamWatchdogTimeoutMs <= 0 || safety.MotionLoopHeartbeatTimeoutMs <= 0
            || safety.SoftStopMaxMs < 0 || safety.KillMaxMs < 0)
            list.Add("Safety timeouts must be positive.");
        if (safety.AiSessionSpeedCapPercent is < 1 or > 100)
            list.Add($"AiSessionSpeedCapPercent {safety.AiSessionSpeedCapPercent} must be 1..100.");
        errors = list;
        return list.Count == 0;
    }

    /// <summary>Conservative defaults for a typical 5-axis hobby arm (simulator-friendly).</summary>
    public static MachineConfig CreateDefault5Axis() => new(
        Name: "roboarm-default",
        Axes:
        [
            new AxisConfig(0, "base", MaxVelocityDegS: 60, MaxAccelDegS2: 200,
                SoftLimitMinDeg: -170, SoftLimitMaxDeg: 170, Homing: new HomingConfig()),
            new AxisConfig(1, "shoulder", MaxVelocityDegS: 45, MaxAccelDegS2: 150,
                SoftLimitMinDeg: -100, SoftLimitMaxDeg: 100, Homing: new HomingConfig()),
            new AxisConfig(2, "elbow", MaxVelocityDegS: 45, MaxAccelDegS2: 150,
                SoftLimitMinDeg: -110, SoftLimitMaxDeg: 110, Homing: new HomingConfig()),
            new AxisConfig(3, "wrist", MaxVelocityDegS: 90, MaxAccelDegS2: 300,
                SoftLimitMinDeg: -120, SoftLimitMaxDeg: 120, Homing: new HomingConfig()),
            new AxisConfig(4, "gripper", MaxVelocityDegS: 30, MaxAccelDegS2: 120,
                SoftLimitMinDeg: 0, SoftLimitMaxDeg: 60),
        ]);
}
