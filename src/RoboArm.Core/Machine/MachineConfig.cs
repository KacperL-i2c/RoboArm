namespace RoboArm.Machine;

public sealed record SafetyConfig(
    int StreamWatchdogTimeoutMs = 250,
    int MotionLoopHeartbeatTimeoutMs = 500,
    int SoftStopMaxMs = 10,
    int KillMaxMs = 10,
    int AiSessionSpeedCapPercent = 25,
    bool JogRequiresEnablingSwitch = false
);

public sealed record MachineConfig(
    string Name,
    IReadOnlyList<AxisConfig> Axes,
    SafetyConfig Safety = new(),
    int ApiPort = 8787,
    string ConfigVersion = "1"
)
{
    public bool Validate(out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        if (Axes.Count == 0)
            list.Add("At least one axis is required.");
        if (Axes.Select(a => a.Id).Distinct().Count() != Axes.Count)
            list.Add("Axis ids must be unique.");
        foreach (var axis in Axes)
        {
            if (!axis.IsValid)
                list.Add($"Axis '{axis.Name}' (id {axis.Id}) has invalid parameters.");
        }
        errors = list;
        return list.Count == 0;
    }
}
