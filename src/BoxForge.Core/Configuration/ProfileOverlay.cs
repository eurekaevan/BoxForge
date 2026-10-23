using BoxForge.Exceptions;
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
            ?? throw new ProfileValidationException("profile", "profile: 必须为对象。");
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
            throw new ProfileValidationException("profile", "profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
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
                    if (!keys.Add(property.Name)) throw new ProfileValidationException("profile", "profile: 存在重复键。");
                    Validate(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null) throw new ProfileValidationException("profile", "profile: 数组元素不能为 null。");
                    Validate(item);
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
                _ = value.GetString();
        }
        catch (InvalidOperationException ex)
        {
            throw new ProfileValidationException("profile", "profile: JSON 名称与字符串必须是有效的 Unicode。", ex);
        }
    }

    internal static void ValidateStructure(JsonObject patch, Type type, string path)
    {
        JsonTypeInfo schema = BuiltInProfile.JsonOptions.GetTypeInfo(type);
        foreach (var (key, value) in patch)
        {
            var property = schema.Properties.SingleOrDefault(property => property.Name == key)
                ?? throw new ProfileValidationException(path + "[unknown]", $"{path}.{key}: 未知字段。");
            if (value is null)
            {
                if (!property.IsSetNullable) throw new ProfileValidationException($"{path}.{key}", $"{path}.{key}: 字段不能为 null。");
                continue;
            }
            JsonTypeInfo fieldType = BuiltInProfile.JsonOptions.GetTypeInfo(property.PropertyType);
            if (fieldType.Kind == JsonTypeInfoKind.Object)
            {
                if (value is not JsonObject child) throw new ProfileValidationException($"{path}.{key}", $"{path}.{key}: 字段必须为对象。");
                ValidateStructure(child, property.PropertyType, path + "." + key);
            }
            else if (fieldType.Kind == JsonTypeInfoKind.Enumerable)
            {
                _ = ArrayOperations(value, property.PropertyType, path + "." + key);
            }
            else _ = Deserialize(value, property.PropertyType, path + "." + key);
        }
    }

    private static JsonObject ArrayOperations(JsonNode? value, Type type, string path)
    {
        if (value is not JsonObject operations || operations.Count == 0
            || operations.Any(operation => operation.Key is not ("replace" or "append" or "remove" or "patch" or "set" or "remove_by_key" or "insert_before")
                || operation.Value is not JsonArray))
            throw new ProfileValidationException($"{path}", $"{path}: 数组必须使用显式 replace、append、remove、patch、set、remove_by_key 或 insert_before 操作。");
        Type elementType = type.GenericTypeArguments.Single();
        foreach (var (operation, items) in operations)
        {
            if (((JsonArray)items!).Any(item => item is null)) throw new ProfileValidationException($"{path}.{operation}", $"{path}.{operation}: 数组元素不能为 null。");
            if (operation is not ("replace" or "append" or "remove")) _ = IdentityKey(elementType, path);
            var entries = (JsonArray)items!;
            for (int index = 0; index < entries.Count; index++)
            {
                string entryPath = $"{path}.{operation}[{index}]";
                var item = entries[index];
                if (operation is "replace" or "append" or "remove")
                {
                    _ = Deserialize(item, elementType, entryPath);
                    continue;
                }
                if (item is not JsonObject entry) throw new ProfileValidationException(entryPath, $"{path}.{operation}: 元素必须为对象。");
                if (operation == "insert_before")
                {
                    if (entry.Count != 2 || entry["before"] is not JsonObject before || entry["value"] is not JsonObject entity)
                        throw new ProfileValidationException(entryPath, $"{path}.insert_before: 必须包含 before 选择器和完整 value 对象。");
                    _ = Identity(before, elementType, entryPath + ".before", selectorOnly: true);
                    _ = Deserialize(entity, elementType, entryPath + ".value");
                }
                else
                {
                    _ = Identity(entry, elementType, entryPath, selectorOnly: operation == "remove_by_key");
                    if (operation == "remove_by_key") continue;
                    JsonObject body = Payload(entry, elementType, operation == "patch");
                    if (operation == "patch") ValidateStructure(body, elementType, entryPath);
                    else _ = Deserialize(body, elementType, entryPath);
                }
            }
        }
        return operations;
    }

    private static string IdentityKey(Type type, string path)
    {
        var schema = BuiltInProfile.JsonOptions.GetTypeInfo(type);
        if (schema.Kind == JsonTypeInfoKind.Object)
        {
            if (schema.Properties.Any(property => property.Name == "id")) return "id";
            if (schema.Properties.Any(property => property.Name is "tag" or "tags")) return "tag";
        }
        throw new ProfileValidationException($"{path}", $"{path}: 按键操作仅支持带 id 或 tag 的对象数组。");
    }

    private static string Identity(JsonObject entry, Type type, string path, bool selectorOnly = false)
    {
        string key = IdentityKey(type, path);
        if (entry[key] is not JsonValue value || !value.TryGetValue<string>(out var identity)
            || string.IsNullOrWhiteSpace(identity) || (selectorOnly && entry.Count != 1))
            throw new ProfileValidationException($"{path}", $"{path}: 选择器必须包含非空字符串 {key}，独立选择器不能包含其他字段。");
        return identity;
    }

    private static JsonObject Payload(JsonObject entry, Type type, bool patch)
    {
        var body = (JsonObject)entry.DeepClone();
        string key = IdentityKey(type, "profile");
        if (patch || !BuiltInProfile.JsonOptions.GetTypeInfo(type).Properties.Any(property => property.Name == key))
            body.Remove(key);
        return body;
    }

    private static bool Matches(JsonNode? item, string key, string identity) => item is JsonObject entity
        && (entity[key] is JsonValue value && value.TryGetValue<string>(out var text) && text == identity
            || key == "tag" && entity["tags"] is JsonArray tags
                && tags.Any(tag => tag is JsonValue tagValue && tagValue.TryGetValue<string>(out var textValue) && textValue == identity));

    private static int Find(JsonArray array, JsonObject selector, Type type, string path)
    {
        string identity = Identity(selector, type, path);
        string key = IdentityKey(type, path);
        int[] matches = Enumerable.Range(0, array.Count).Where(index => Matches(array[index], key, identity)).ToArray();
        if (matches.Length != 1) throw new ProfileValidationException($"{path}", $"{path}: {key} '{identity}' 必须唯一匹配一个现有元素。");
        return matches[0];
    }

    internal static BuiltInProfile Apply(BuiltInProfile original, JsonObject overlay, string path)
    {
        var target = JsonSerializer.SerializeToNode(original, BuiltInProfile.JsonOptions)!.AsObject();
        foreach (var (name, value) in overlay)
        {
            if (name == "platforms") continue;
            Merge(target, name, value, path, typeof(BuiltInProfile));
        }
        return (BuiltInProfile?)Deserialize(target, typeof(BuiltInProfile), path)
            ?? throw new ProfileValidationException("profile", "profile: 必须为对象。");
    }

    private static void Merge(JsonObject target, string name, JsonNode? value, string path, Type type)
    {
        if (!target.TryGetPropertyValue(name, out var current))
            throw new ProfileValidationException(path + "[unknown]", $"{path}.{name}: 未知字段。");
        path += "." + name;
        if (current is JsonArray array)
        {
            Type arrayType = BuiltInProfile.JsonOptions.GetTypeInfo(type).Properties.Single(property => property.Name == name).PropertyType;
            Type elementType = arrayType.GenericTypeArguments.Single();
            JsonObject operations = ArrayOperations(value, arrayType, path);
            var result = (JsonArray)(operations["replace"] as JsonArray ?? array).DeepClone();
            if (operations["append"] is JsonArray additions)
                foreach (var item in additions) result.Add(item?.DeepClone());
            if (operations["remove"] is JsonArray removals)
            {
                for (int removal = 0; removal < removals.Count; removal++)
                {
                    var item = removals[removal];
                    int index = Enumerable.Range(0, result.Count).FirstOrDefault(index => JsonNode.DeepEquals(result[index], item), -1);
                    if (index < 0) throw new ProfileValidationException($"{path}.remove[{removal}]", $"{path}.remove: 找不到完全匹配的数组元素。");
                    result.RemoveAt(index);
                }
            }
            foreach (string operation in new[] { "patch", "set", "remove_by_key", "insert_before" })
            {
                if (operations[operation] is not JsonArray entries) continue;
                for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    string entryPath = $"{path}.{operation}[{entryIndex}]";
                    JsonObject entry = entries[entryIndex]!.AsObject();
                    JsonObject selector = operation == "insert_before" ? entry["before"]!.AsObject() : entry;
                    int index = Find(result, selector, elementType, entryPath);
                    if (operation == "remove_by_key") result.RemoveAt(index);
                    else if (operation == "insert_before") result.Insert(index, entry["value"]!.DeepClone());
                    else
                    {
                        JsonObject body = Payload(entry, elementType, operation == "patch");
                        if (operation == "set") result[index] = body;
                        else
                        {
                            // Appended entities may omit optional fields; restore their declared defaults before patching.
                            JsonObject entity = JsonSerializer.SerializeToNode(
                                result[index]!.Deserialize(elementType, BuiltInProfile.JsonOptions), elementType, BuiltInProfile.JsonOptions)!.AsObject();
                            foreach (var (key, item) in body) Merge(entity, key, item, entryPath, elementType);
                            result[index] = entity;
                        }
                        if (!Matches(result[index], IdentityKey(elementType, path), Identity(selector, elementType, entryPath)))
                            throw new ProfileValidationException(entryPath, $"{path}.{operation}: 不能修改被选择元素的身份键。");
                    }
                }
            }
            target[name] = result;
        }
        else if (current is JsonObject child)
        {
            if (value is not JsonObject patch) throw new ProfileValidationException($"{path}", $"{path}: 必须为对象。");
            Type childType = BuiltInProfile.JsonOptions.GetTypeInfo(type).Properties.Single(property => property.Name == name).PropertyType;
            foreach (var (key, item) in patch) Merge(child, key, item, path, childType);
        }
        else
        {
            var property = BuiltInProfile.JsonOptions.GetTypeInfo(type).Properties.Single(property => property.Name == name);
            if (value is null && !property.IsSetNullable)
                throw new ProfileValidationException(path, $"{path}: 字段不能为 null。");
            _ = Deserialize(value, property.PropertyType, path);
            target[name] = value?.DeepClone();
        }
    }
    private static object? Deserialize(JsonNode? value, Type type, string path)
    {
        try { return value.Deserialize(type, BuiltInProfile.JsonOptions); }
        catch (JsonException ex)
        {
            throw new ProfileValidationException(path, $"{path}: 字段类型无效、缺失必填字段或存在未知字段。", ex);
        }
    }
}
