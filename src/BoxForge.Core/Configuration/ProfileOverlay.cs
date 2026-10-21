using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace BoxForge.Configuration;

internal static class ProfileOverlay
{
    internal static JsonObject Read(Stream source)
    {
        using var document = JsonDocument.Parse(source);
        Validate(document.RootElement);
        return document.RootElement.Deserialize<JsonObject>(BuiltInProfile.JsonOptions)
            ?? throw new FormatException("profile: 必须为对象。");
    }

    internal static JsonObject Snapshot(JsonObject source)
    {
        try
        {
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(source));
            return Read(stream);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException("profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
        }
    }

    private static void Validate(JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new FormatException("profile: 存在重复键。");
                    Validate(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null) throw new FormatException("profile: 数组元素不能为 null。");
                    Validate(item);
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
                _ = value.GetString();
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException("profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
        }
    }

    internal static void ValidateStructure(JsonObject patch, Type type)
    {
        JsonTypeInfo schema = BuiltInProfile.JsonOptions.GetTypeInfo(type);
        foreach (var (key, value) in patch)
        {
            var property = schema.Properties.SingleOrDefault(property => property.Name == key)
                ?? throw new FormatException("profile.platforms: 未知字段。");
            if (value is null)
            {
                if (!property.IsSetNullable) throw new FormatException("profile.platforms: 字段不能为 null。");
                continue;
            }
            JsonTypeInfo fieldType = BuiltInProfile.JsonOptions.GetTypeInfo(property.PropertyType);
            if (fieldType.Kind == JsonTypeInfoKind.Object)
            {
                if (value is not JsonObject child) throw new FormatException("profile.platforms: 字段必须为对象。");
                ValidateStructure(child, property.PropertyType);
            }
            else if (fieldType.Kind == JsonTypeInfoKind.Enumerable)
            {
                foreach (var operation in ArrayOperations(value, "profile.platforms"))
                {
                    var items = (JsonArray)operation.Value!;
                    if (items.Any(item => item is null)) throw new FormatException("profile.platforms: 数组元素不能为 null。");
                    _ = items.Deserialize(property.PropertyType, BuiltInProfile.JsonOptions);
                }
            }
            else _ = value.Deserialize(property.PropertyType, BuiltInProfile.JsonOptions);
        }
    }

    private static JsonObject ArrayOperations(JsonNode? value, string path)
    {
        if (value is not JsonObject operations || operations.Count == 0
            || operations.Any(operation => operation.Key is not ("replace" or "append" or "remove") || operation.Value is not JsonArray))
            throw new FormatException($"{path}: 数组必须使用显式 replace、append 或 remove 操作。");
        return operations;
    }

    internal static BuiltInProfile Apply(BuiltInProfile original, JsonObject overlay)
    {
        var target = JsonSerializer.SerializeToNode(original, BuiltInProfile.JsonOptions)!.AsObject();
        foreach (var (name, value) in overlay)
        {
            if (name == "platforms") continue;
            Merge(target, name, value, "profile");
        }
        return target.Deserialize<BuiltInProfile>(BuiltInProfile.JsonOptions)
            ?? throw new FormatException("profile: 必须为对象。");
    }

    private static void Merge(JsonObject target, string name, JsonNode? value, string path)
    {
        path += "." + name;
        if (!target.TryGetPropertyValue(name, out var current))
            throw new FormatException($"{path}: 未知字段。");
        if (current is JsonArray array)
        {
            JsonObject operations = ArrayOperations(value, path);
            var result = (JsonArray)array.DeepClone();
            if (operations["replace"] is JsonArray replacement) result = (JsonArray)replacement.DeepClone();
            if (operations["append"] is JsonArray additions)
                foreach (var item in additions) result.Add(item?.DeepClone());
            if (operations["remove"] is JsonArray removals)
            {
                foreach (var item in removals)
                {
                    int index = Enumerable.Range(0, result.Count).FirstOrDefault(index => JsonNode.DeepEquals(result[index], item), -1);
                    if (index < 0) throw new FormatException($"{path}.remove: 找不到完全匹配的数组元素。");
                    result.RemoveAt(index);
                }
            }
            target[name] = result;
        }
        else if (current is JsonObject child)
        {
            if (value is not JsonObject patch) throw new FormatException($"{path}: 必须为对象。");
            foreach (var (key, item) in patch) Merge(child, key, item, path);
        }
        else target[name] = value?.DeepClone();
    }
}
