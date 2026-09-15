using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboArm.Persistence;

/// <summary>
/// Single source of truth for all RoboArm JSON files (config, poses, programs, audit).
/// Degrees and seconds only; schema versions travel inside the payloads.
/// </summary>
public static class RoboArmJson
{
    public static JsonSerializerOptions File { get; } = CreateFileOptions(indented: true);

    public static JsonSerializerOptions Compact { get; } = CreateFileOptions(indented: false);

    private static JsonSerializerOptions CreateFileOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? File : Compact);

    public static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, File)
                ?? throw new JsonException("Payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Invalid {typeof(T).Name} JSON: {ex.Message}", ex);
        }
    }
}
