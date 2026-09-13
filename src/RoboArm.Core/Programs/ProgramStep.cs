using System.Text.Json.Serialization;

namespace RoboArm.Programs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepType
{
    MovePose,
    MoveJoints,
    MoveAxis,
    JogHold,
    Gripper,
    SetOutput,
    WaitInput,
    Wait,
    Loop,
    Call,
    Home,
    Park,
    SetSpeed
}

public sealed record ProgramStep(
    StepType Type,
    IReadOnlyDictionary<string, string> Parameters,
    bool Enabled = true,
    string? Comment = null
);

public sealed record RobotProgram(
    string Name,
    IReadOnlyList<ProgramStep> Steps,
    string ProgramVersion = "1"
);
