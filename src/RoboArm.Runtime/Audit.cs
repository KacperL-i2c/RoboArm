using RoboArm.Persistence;

namespace RoboArm.Runtime;

public sealed record AuditRecord(
    DateTimeOffset UtcTimestamp,
    string Source,
    string Command,
    IReadOnlyDictionary<string, string> Parameters,
    string Outcome
);

public interface IAuditSink
{
    void Write(AuditRecord record);
}

/// <summary>Append-only JSONL audit sink (docs/03 L7). One file per day.</summary>
public sealed class JsonlAuditSink(string directory) : IAuditSink
{
    private readonly object _gate = new();

    public string DirectoryPath => directory;

    public void Write(AuditRecord record)
    {
        var line = RoboArmJson.Serialize(record, indented: false);
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(FileFor(directory, record.UtcTimestamp), line + "\n");
        }
    }

    public static string FileFor(string directory, DateTimeOffset utc) =>
        Path.Combine(directory, $"{utc:yyyyMMdd}.jsonl");

    /// <summary>Reads a JSONL audit log back (replay tooling / round-trip tests).</summary>
    public static IReadOnlyList<AuditRecord> ReadFile(string path) =>
        File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => RoboArmJson.Deserialize<AuditRecord>(l))
            .ToList();
}
