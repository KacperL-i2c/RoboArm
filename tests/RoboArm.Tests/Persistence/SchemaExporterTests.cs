using System.Text.Json;
using RoboArm.Machine;
using RoboArm.Persistence;
using RoboArm.Poses;
using RoboArm.Programs;
using Xunit;

namespace RoboArm.Tests.Persistence;

public sealed class SchemaExporterTests
{
    [Theory]
    [InlineData(typeof(MachineConfig))]
    [InlineData(typeof(AxisConfig))]
    [InlineData(typeof(SafetyConfig))]
    [InlineData(typeof(HomingConfig))]
    [InlineData(typeof(PoseLibrary))]
    [InlineData(typeof(RobotProgram))]
    [InlineData(typeof(ProgramStep))]
    public void Export_Produces_Valid_Json_Schema_With_Required_Core_Fields(Type type)
    {
        var schemaJson = SchemaExporter.Export(type);
        using var doc = JsonDocument.Parse(schemaJson);
        var root = doc.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("properties", out _));
    }

    [Fact]
    public void MachineConfig_Schema_Has_Versioned_Envelope_And_Axis_Array()
    {
        using var doc = JsonDocument.Parse(SchemaExporter.Export<MachineConfig>());
        var props = doc.RootElement.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("array", props.GetProperty("axes").GetProperty("type").GetString());
        Assert.Equal("object", props.GetProperty("axes").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("apiPort").GetProperty("type").GetString());
        var required = doc.RootElement.GetProperty("required");
        Assert.Contains("name", required.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("axes", required.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("configVersion", required.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void ProgramStep_Schema_Lists_All_StepTypes_As_CamelCase_Enum()
    {
        using var doc = JsonDocument.Parse(SchemaExporter.Export<ProgramStep>());
        var stepType = doc.RootElement.GetProperty("properties").GetProperty("type");
        Assert.Equal("string", stepType.GetProperty("type").GetString());
        var values = stepType.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(Enum.GetValues<StepType>().Length, values.Count);
        Assert.Contains("movePose", values);
        Assert.Contains("setSpeed", values);
    }
}
