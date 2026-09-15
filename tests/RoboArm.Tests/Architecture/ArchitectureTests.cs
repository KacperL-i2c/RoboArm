using System.Reflection;
using Xunit;

namespace RoboArm.Tests.Architecture;

public sealed class ArchitectureTests
{
    private static readonly string[] All =
    [
        "RoboArm.Core",
        "RoboArm.Protocol",
        "RoboArm.Simulator",
        "RoboArm.RoboScript",
        "RoboArm.Runtime",
        "RoboArm.Server",
        "RoboArm.App",
    ];

    private static HashSet<string> ReferencesOf(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("RoboArm.", StringComparison.Ordinal))
            .ToHashSet();

    public static TheoryData<string> ProjectNames =>
        new(All);

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Projects_Only_Reference_Allowed_Dependencies(string project)
    {
        var allowed = project switch
        {
            "RoboArm.Core" => Array.Empty<string>(),
            "RoboArm.Protocol" or "RoboArm.Simulator" or "RoboArm.RoboScript" => ["RoboArm.Core"],
            "RoboArm.Runtime" => ["RoboArm.Core", "RoboArm.Protocol", "RoboArm.Simulator", "RoboArm.RoboScript"],
            "RoboArm.Server" or "RoboArm.App" => ["RoboArm.Core", "RoboArm.Runtime"],
            _ => throw new Xunit.Sdk.XunitException($"Unknown project {project}"),
        };

        var actual = ReferencesOf(project);
        var illegal = actual.Except(allowed).ToList();

        Assert.True(illegal.Count == 0,
            $"{project} references forbidden assemblies: [{string.Join(", ", illegal)}]. Allowed: [{string.Join(", ", allowed)}]. (docs/04-architecture.md)");
    }

    [Fact]
    public void Core_Does_Not_Reference_Any_RoboArm_Assembly()
    {
        Assert.Empty(ReferencesOf("RoboArm.Core"));
    }

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void No_Project_References_Runtime_From_Below(string project)
    {
        if (project is "RoboArm.Server" or "RoboArm.App")
            return;
        Assert.DoesNotContain("RoboArm.Runtime", ReferencesOf(project));
    }
}
