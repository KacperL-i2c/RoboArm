using System.Reflection;
using System.Text.Json;

namespace RoboArm.Persistence;

/// <summary>
/// Reflection-based JSON Schema (draft 2020-12) exporter for the persisted contract types.
/// Output is intentionally minimal: type, enum values, item/property schemas, required lists.
/// </summary>
public static class SchemaExporter
{
    public static string Export<T>(string? titleOverride = null) => Export(typeof(T), titleOverride);

    public static string Export(Type type, string? titleOverride = null)
    {
        var root = new JsonObjectBuilder();
        BuildObject(type, root, titleOverride ?? type.Name);
        var obj = root.Build();
        obj["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void BuildObject(Type type, JsonObjectBuilder node, string title)
    {
        node.Add("title", title);
        node.Add("type", "object");

        var properties = node.AddObject("properties");
        var required = node.AddArray("required");

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = ToCamelCase(prop.Name);
            var propNode = properties.AddObject(name);
            BuildNode(prop.PropertyType, propNode);
            var ignored = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null;
            var isSchemaVersion = prop.Name.EndsWith("Version", StringComparison.Ordinal);
            if (!ignored && (isSchemaVersion || !HasSafeDefault(prop)))
                required.Add(name);
        }
    }

    private static void BuildNode(Type type, JsonObjectBuilder node)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            type = underlying;

        if (type.IsEnum)
        {
            node.Add("type", "string");
            var values = node.AddArray("enum");
            foreach (var name in Enum.GetNames(type))
                values.Add(ToCamelCase(name));
            return;
        }

        if (type == typeof(string))
        {
            node.Add("type", "string");
            return;
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
        {
            node.Add("type", "string");
            node.Add("format", "date-time");
            return;
        }

        if (type == typeof(bool))
        {
            node.Add("type", "boolean");
            return;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
        {
            node.Add("type", "integer");
            return;
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            node.Add("type", "number");
            return;
        }

        if (type.IsArray || ImplementsOpenGeneric(type, typeof(IReadOnlyList<>)) || ImplementsOpenGeneric(type, typeof(IList<>)))
        {
            node.Add("type", "array");
            var itemType = GetCollectionItemType(type);
            var items = node.AddObject("items");
            BuildNode(itemType, items);
            return;
        }

        if (ImplementsOpenGeneric(type, typeof(IReadOnlyDictionary<,>)) || ImplementsOpenGeneric(type, typeof(IDictionary<,>)))
        {
            node.Add("type", "object");
            var args = GetDictionaryArgs(type);
            var valueNode = node.AddObject("additionalProperties");
            BuildNode(args[1], valueNode);
            return;
        }

        if (type.IsSealed && type.IsClass)
        {
            BuildObject(type, node, ToCamelCase(type.Name));
            return;
        }

        node.Add("description", $"unsupported type {type.Name}");
    }

    private static bool HasSafeDefault(PropertyInfo prop)
    {
        // Positional record parameters with default values become init properties that
        // STJ treats as optional; detect via the constructor parameter table.
        foreach (var ctor in prop.DeclaringType!.GetConstructors())
        {
            foreach (var param in ctor.GetParameters())
            {
                if (param.Name == prop.Name && param.HasDefaultValue)
                    return true;
            }
        }
        return false;
    }

    private static Type GetCollectionItemType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;
        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() is var def
                && (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>)))
                return iface.GetGenericArguments()[0];
        }
        return typeof(object);
    }

    private static Type[] GetDictionaryArgs(Type type)
    {
        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() is var def
                && (def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>)))
                return iface.GetGenericArguments();
        }
        return [typeof(string), typeof(object)];
    }

    private static bool ImplementsOpenGeneric(Type type, Type openGeneric)
    {
        return type == openGeneric
            || (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric)
            || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private sealed class JsonObjectBuilder
    {
        private readonly Dictionary<string, object?> _values = [];

        public void Add(string key, string? value) => _values[key] = value;

        public JsonObjectBuilder AddObject(string key)
        {
            var child = new JsonObjectBuilder();
            _values[key] = child;
            return child;
        }

        public JsonArrayBuilder AddArray(string key)
        {
            var child = new JsonArrayBuilder();
            _values[key] = child;
            return child;
        }

        public Dictionary<string, object?> Build()
        {
            var result = new Dictionary<string, object?>();
            foreach (var (key, value) in _values)
                result[key] = value switch
                {
                    JsonObjectBuilder b => b.Build(),
                    JsonArrayBuilder a => a.Build(),
                    _ => value,
                };
            return result;
        }
    }

    private sealed class JsonArrayBuilder
    {
        private readonly List<object?> _items = [];

        public void Add(string value) => _items.Add(value);

        public List<object?> Build() =>
            _items.Select(i => i is JsonObjectBuilder b ? b.Build() : i).ToList();
    }
}
