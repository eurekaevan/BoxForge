using System.Collections;
using SubConvert.Exceptions;

namespace SubConvert.Models.Clash;

public class ClashObject
{
    private readonly IReadOnlyDictionary<string, object?> values;

    protected ClashObject(IDictionary source)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key?.ToString() is not { } key)
            {
                continue;
            }

            normalized[key] = Normalize(entry.Value);
        }

        values = normalized;
    }

    public object? GetValue(string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    public string? GetString(string key)
    {
        var value = GetRawString(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string? GetRawString(string key) =>
        GetValue(key)?.ToString();

    public string GetRequiredString(string key) =>
        GetString(key)
        ?? throw new NodeParseException($"缺失必填字段或为空: {key}");

    public int? GetInt(string key) =>
        int.TryParse(GetRawString(key), out var value) ? value : null;

    public int GetRequiredInt(string key)
    {
        var value = GetInt(key);
        if (value is > 0 and <= 65535)
        {
            return value.Value;
        }

        throw new NodeParseException($"缺失必填字段或端口格式无效: {key}");
    }

    public bool GetBool(string key, bool defaultValue = false) =>
        bool.TryParse(GetRawString(key), out var value) ? value : defaultValue;

    public bool? GetNullableBool(string key) =>
        bool.TryParse(GetRawString(key), out var value) ? value : null;

    public List<string>? GetStringList(string key)
    {
        return GetValue(key) switch
        {
            IReadOnlyList<object?> list =>
                [.. list
                    .Select(item => item?.ToString() ?? "")
                    .Where(item => !string.IsNullOrEmpty(item))],
            string value => [value],
            _ => null
        };
    }

    public ClashObject? GetObject(string key) => GetValue(key) as ClashObject;

    public IEnumerable<KeyValuePair<string, object?>> Properties => values;

    private static object? Normalize(object? value)
    {
        if (value is IDictionary dictionary)
        {
            return new ClashObject(dictionary);
        }

        if (value is IEnumerable sequence and not string)
        {
            return sequence
                .Cast<object?>()
                .Select(Normalize)
                .ToList();
        }

        return value;
    }
}

public sealed class ClashProxyNode(IDictionary source) : ClashObject(source)
{
    public string? Type => GetRawString("type");
    public string Name => GetString("name") ?? "Unknown-Node";
}
