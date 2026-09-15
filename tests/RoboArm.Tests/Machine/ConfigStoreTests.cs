using RoboArm.Machine;
using Xunit;

namespace RoboArm.Tests.Machine;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "roboarm-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "machine.json");

    private ConfigStore Store(ConfigMigrations? migrations = null) => new(ConfigPath, migrations);

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Save_Then_Load_RoundTrips()
    {
        var store = Store();
        var config = MachineConfig.CreateDefault5Axis();
        await store.SaveAsync(config);

        var loaded = await new ConfigStore(ConfigPath).LoadAsync();
        Assert.Equal(config.Name, loaded.Name);
        Assert.Equal(config.Axes.Count, loaded.Axes.Count);
    }

    [Fact]
    public async Task Missing_File_LoadOrDefault_Creates_Default_And_Saves_It()
    {
        var store = Store();
        var loaded = await store.LoadOrDefaultAsync();
        Assert.Equal(5, loaded.Axes.Count);
        Assert.True(File.Exists(ConfigPath));

        var again = await Store().LoadAsync();
        Assert.Equal(loaded.Name, again.Name);
    }

    [Fact]
    public async Task Missing_File_LoadAsync_Throws_FileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Store().LoadAsync());
    }

    [Fact]
    public async Task Invalid_Config_File_Is_Rejected_With_Errors()
    {
        await File.WriteAllTextAsync(ConfigPath,
            """{"name":"bad","axes":[{"id":0,"name":"x","stepsPerDegree":-5}],"configVersion":"1"}""");

        var ex = await Assert.ThrowsAsync<ConfigInvalidException>(() => Store().LoadAsync());
        Assert.Contains(ex.Errors, e => e.Contains("invalid parameters"));
    }

    [Fact]
    public async Task Save_Rejects_Invalid_Config_And_Leaves_File_Untouched()
    {
        var store = Store();
        await store.SaveAsync(MachineConfig.CreateDefault5Axis());
        var before = await File.ReadAllTextAsync(ConfigPath);

        var bad = MachineConfig.CreateDefault5Axis() with { Axes = [] };
        await Assert.ThrowsAsync<ConfigInvalidException>(() => store.SaveAsync(bad));

        Assert.Equal(before, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task Migration_Pipeline_Advances_Version_And_Preserves_Unknown_Fields()
    {
        await File.WriteAllTextAsync(ConfigPath,
            """
            {"name":"old","axes":[{"id":0,"name":"base"}],"configVersion":"0","calibratedBy":"bench-day-1"}
            """);

        var migrations = new ConfigMigrations().Add("0", "1", node =>
        {
            // v1: safety block becomes required with defaults
            if (node["safety"] is null)
                node["safety"] = new System.Text.Json.Nodes.JsonObject();
        });

        var store = Store(migrations);
        var config = await store.LoadAsync();
        Assert.Equal("1", config.ConfigVersion);
        Assert.NotNull(config.SafetyOrDefault);

        await store.SaveAsync(config);
        var saved = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("\"calibratedBy\"", saved); // unknown field preserved through migrate+save
        Assert.Contains("\"configVersion\": \"1\"", saved);
    }

    [Fact]
    public async Task Unmigrateable_Version_Is_Rejected()
    {
        await File.WriteAllTextAsync(ConfigPath,
            """{"name":"future","axes":[],"configVersion":"99"}""");
        var ex = await Assert.ThrowsAsync<ConfigInvalidException>(() => Store().LoadAsync());
        Assert.Contains(ex.Errors, e => e.Contains("No migration path"));
    }

    [Fact]
    public async Task No_Temp_File_Remains_After_Save()
    {
        var store = Store();
        await store.SaveAsync(MachineConfig.CreateDefault5Axis());
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void Default5Axis_Config_Validates()
    {
        Assert.True(MachineConfig.CreateDefault5Axis().Validate(out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void CrossChecks_Reject_Bad_Homing_And_Ports()
    {
        var bad = MachineConfig.CreateDefault5Axis() with
        {
            ApiPort = 70000,
            Axes =
            [
                new AxisConfig(0, "base", Homing: new HomingConfig(LimitSwitchInput: -5)),
            ],
        };
        Assert.False(bad.Validate(out var errors));
        Assert.Contains(errors, e => e.Contains("ApiPort"));
        Assert.Contains(errors, e => e.Contains("LimitSwitchInput"));
    }
}
