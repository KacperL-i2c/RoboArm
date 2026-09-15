using System.Text.Json;
using System.Text.Json.Nodes;
using RoboArm.Persistence;

namespace RoboArm.Machine;

public sealed class ConfigInvalidException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ConfigInvalidException(IReadOnlyList<string> errors)
        : base($"Machine config invalid:\n- {string.Join("\n- ", errors)}")
        => Errors = errors;
}

/// <summary>
/// Ordered migrations between config schema versions, operating on raw JSON nodes so that
/// unknown fields survive (docs/04: never break old files silently).
/// </summary>
public sealed class ConfigMigrations
{
    private readonly List<(string From, string To, Action<JsonObject> Apply)> _steps = [];

    public static ConfigMigrations None { get; } = new();

    public IReadOnlyList<(string From, string To)> Steps => _steps.Select(s => (s.From, s.To)).ToList();

    public ConfigMigrations Add(string from, string to, Action<JsonObject> apply)
    {
        _steps.Add((from, to, apply));
        return this;
    }

    public (JsonObject Node, IReadOnlyList<string> Applied) Migrate(JsonObject node, string targetVersion)
    {
        var applied = new List<string>();
        var current = ReadVersion(node);
        while (current != targetVersion)
        {
            var step = _steps.FirstOrDefault(s => s.From == current);
            if (step == default)
                throw new ConfigInvalidException([$"No migration path from configVersion '{current}' to '{targetVersion}'."]);
            step.Apply(node);
            node["configVersion"] = targetVersion == ReadVersion(node) ? targetVersion : step.To;
            if (ReadVersion(node) == current)
                throw new ConfigInvalidException([$"Migration {step.From}->{step.To} did not advance configVersion."]);
            current = ReadVersion(node);
            applied.Add($"{step.From}->{step.To}");
        }
        return (node, applied);
    }

    public static string ReadVersion(JsonObject node) =>
        node["configVersion"]?.GetValue<string>() ?? "1";
}

/// <summary>
/// Loads/saves the machine config JSON file. Writes are atomic (temp file + replace);
/// unknown fields from the last successful load are preserved on save.
/// </summary>
public sealed class ConfigStore
{
    public const string CurrentConfigVersion = "1";

    private readonly string _path;
    private readonly ConfigMigrations _migrations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _lastLoadedRaw;

    public ConfigStore(string path, ConfigMigrations? migrations = null)
    {
        _path = path;
        _migrations = migrations ?? ConfigMigrations.None;
    }

    public static string DefaultDevPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "data", "machine.json");

    public static string DefaultProgramDataPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RoboArm", "machine.json");

    public string FilePath => _path;

    /// <exception cref="ConfigInvalidException">File exists but fails validation or migration.</exception>
    /// <exception cref="FileNotFoundException">No config file exists.</exception>
    public async Task<MachineConfig> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return ParseAndValidate(json);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MachineConfig> LoadOrDefaultAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                return ParseAndValidate(json);
            }
            var created = MachineConfig.CreateDefault5Axis();
            await WriteAtomicAsync(RoboArmJson.Serialize(created), ct).ConfigureAwait(false);
            _lastLoadedRaw = null;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="ConfigInvalidException">Config fails validation.</exception>
    public async Task SaveAsync(MachineConfig config, CancellationToken ct = default)
    {
        if (!config.Validate(out var errors))
            throw new ConfigInvalidException(errors);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = JsonSerializer.SerializeToNode(config, RoboArmJson.File) as JsonObject
                ?? throw new InvalidOperationException("Config did not serialize to an object.");
            PreserveUnknownFields(node);
            await WriteAtomicAsync(node.ToJsonString(RoboArmJson.File), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private MachineConfig ParseAndValidate(string json)
    {
        MachineConfig config;
        try
        {
            var node = JsonNode.Parse(json)
                ?? throw new ConfigInvalidException(["Config file is empty."]);
            if (node is not JsonObject obj)
                throw new ConfigInvalidException(["Config file must be a JSON object."]);

            var version = ConfigMigrations.ReadVersion(obj);
            if (version != CurrentConfigVersion)
                obj = _migrations.Migrate(obj, CurrentConfigVersion).Node;

            config = obj.Deserialize<MachineConfig>(RoboArmJson.File)
                ?? throw new ConfigInvalidException(["Config deserialized to null."]);
            _lastLoadedRaw = obj;
        }
        catch (JsonException ex)
        {
            throw new ConfigInvalidException([$"Config is not valid JSON: {ex.Message}"]);
        }

        if (!config.Validate(out var errors))
            throw new ConfigInvalidException(errors);
        return config;
    }

    private void PreserveUnknownFields(JsonObject target)
    {
        if (_lastLoadedRaw is null)
            return;

        var known = target.Select(k => k.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _lastLoadedRaw)
        {
            if (!known.Contains(key) && value is not null)
                target[key] = JsonNode.Parse(value.ToJsonString())!;
        }
    }

    private async Task WriteAtomicAsync(string json, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = $"{_path}.tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, overwrite: true);
    }
}
