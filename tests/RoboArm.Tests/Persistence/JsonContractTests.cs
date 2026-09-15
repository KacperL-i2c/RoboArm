using RoboArm.Machine;
using RoboArm.Persistence;
using RoboArm.Poses;
using RoboArm.Programs;
using Xunit;

namespace RoboArm.Tests.Persistence;

public sealed class JsonContractTests
{
    public static MachineConfig SampleConfig() => new(
        Name: "test-arm",
        Axes:
        [
            new AxisConfig(0, "base", StepsPerDegree: 88.89, MaxVelocityDegS: 40,
                MaxAccelDegS2: 120, SoftLimitMinDeg: -170, SoftLimitMaxDeg: 170,
                InvertDirection: true, EnableActiveLow: false,
                Homing: new HomingConfig(SeekSpeedDegS: 6, CreepSpeedDegS: 1.5,
                    SeekPositive: true, BackoffDeg: 1.5, LimitSwitchInput: 2)),
            new AxisConfig(1, "shoulder"),
            new AxisConfig(2, "elbow"),
            new AxisConfig(3, "wrist"),
            new AxisConfig(4, "gripper", SoftLimitMinDeg: 0, SoftLimitMaxDeg: 90),
        ],
        Safety: new SafetyConfig(StreamWatchdogTimeoutMs: 200, AiSessionSpeedCapPercent: 30),
        ApiPort: 9999,
        ConfigVersion: "1");

    public static RobotProgram SampleProgram() => new(
        Name: "demo",
        Steps:
        [
            new ProgramStep(StepType.MoveJoints, new Dictionary<string, string>
            {
                ["0"] = "10", ["1"] = "-20", ["2"] = "30",
            }),
            new ProgramStep(StepType.Gripper, new Dictionary<string, string> { ["closed"] = "true" }, Comment: "grip"),
            new ProgramStep(StepType.Wait, new Dictionary<string, string> { ["seconds"] = "0.5" }, Enabled: false),
            new ProgramStep(StepType.Loop, new Dictionary<string, string> { ["count"] = "3", ["body"] = "0" }),
        ]);

    private static void AssertRoundTripStable<T>(T value)
    {
        var json = RoboArmJson.Serialize(value);
        var back = RoboArmJson.Deserialize<T>(json);
        var json2 = RoboArmJson.Serialize(back);
        Assert.Equal(json, json2);
    }

    [Fact]
    public void MachineConfig_RoundTrips() => AssertRoundTripStable(SampleConfig());

    [Fact]
    public void RobotProgram_RoundTrips() => AssertRoundTripStable(SampleProgram());

    [Fact]
    public void PoseLibrary_RoundTrips()
    {
        var lib = new PoseLibrary(
        [
            new Pose("home", new Dictionary<int, double> { [0] = 0, [1] = -10 },
                GripperClosed: false, Note: "park", CapturedAt: DateTimeOffset.Parse("2026-09-15T10:00:00Z")),
            new Pose("pick", new Dictionary<int, double> { [2] = 45.5 }, GripperClosed: true),
        ]);
        var back = RoboArmJson.Deserialize<PoseLibrary>(RoboArmJson.Serialize(lib));
        Assert.Equal(RoboArmJson.Serialize(lib), RoboArmJson.Serialize(back));
    }

    [Fact]
    public void Serialization_UsesCamelCase_And_StringEnums()
    {
        var json = RoboArmJson.Serialize(SampleConfig());
        Assert.Contains("\"configVersion\"", json);
        Assert.Contains("\"stepsPerDegree\"", json);
        Assert.Contains("\"seekSpeedDegS\"", json);

        var program = RoboArmJson.Serialize(SampleProgram());
        Assert.Contains("\"moveJoints\"", program);
        Assert.Contains("\"gripper\"", program);
    }

    [Fact]
    public void All_StepTypes_RoundTrip()
    {
        var steps = Enum.GetValues<StepType>()
            .Select(t => new ProgramStep(t, new Dictionary<string, string> { ["x"] = "1" }))
            .ToList();
        var program = new RobotProgram("all", steps);
        var back = RoboArmJson.Deserialize<RobotProgram>(RoboArmJson.Serialize(program));
        Assert.Equal(Enum.GetValues<StepType>().Select(t => t).ToList(), back!.Steps.Select(s => s.Type).ToList());
    }

    [Fact]
    public void Unknown_Enum_Value_Rejected_Not_Silent()
    {
        var json = """{"name":"x","steps":[{"type":"teleport","parameters":{}}],"programVersion":"1"}""";
        Assert.Throws<System.Text.Json.JsonException>(() => RoboArmJson.Deserialize<RobotProgram>(json));
    }
}
