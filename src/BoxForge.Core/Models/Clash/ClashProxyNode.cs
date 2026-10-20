using System.Globalization;
using System.Collections;
using System.Text;
using BoxForge.Exceptions;

namespace BoxForge.Models.Clash;

public class ClashObject
{
    internal const int MaximumNormalizedValues = 100_000;
    internal const int MaximumDepth = 64;
    internal const int MaximumExpandedTextBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, object?> values;

    protected ClashObject(IDictionary source) : this(source, new NormalizationBudget()) { }

    private protected ClashObject(IDictionary source, NormalizationBudget budget)
        : this(NormalizeDictionary(source, new HashSet<object>(ReferenceEqualityComparer.Instance), budget, 0)) { }

    private ClashObject(Dictionary<string, object?> normalized) => values = normalized;

    internal sealed class NormalizationBudget
    {
        private int remainingValues = MaximumNormalizedValues;
        private int remainingTextBytes = MaximumExpandedTextBytes;

        public void ConsumeValue(object? value)
        {
            if (--remainingValues < 0)
                throw new NodeParseException($"YAML 展开后的值数量不能超过 {MaximumNormalizedValues}。");
            if (value is string text) ConsumeText(text);
        }

        public void ConsumeText(string text)
        {
            remainingTextBytes -= Encoding.UTF8.GetByteCount(text);
            if (remainingTextBytes < 0)
                throw new NodeParseException($"YAML 展开后的文本不能超过 {MaximumExpandedTextBytes} 个 UTF-8 字节。");
        }
    }

    private static Dictionary<string, object?> NormalizeDictionary(
        IDictionary source, HashSet<object> ancestors, NormalizationBudget budget, int depth)
    {
        // Nested containers are counted by Normalize; roots enter here directly.
        if (depth == 0) budget.ConsumeValue(null);
        EnterContainer(source, ancestors, depth);
        try
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in source)
            {
                if (entry.Key?.ToString() is { } key)
                {
                    budget.ConsumeText(key);
                    normalized[key] = Normalize(entry.Value, ancestors, budget, depth + 1);
                }
            }
            return normalized;
        }
        finally
        {
            ancestors.Remove(source);
        }
    }

    public object? GetValue(string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    internal string? GetPresentField(string name, string? alias = null) =>
        values.ContainsKey(name) ? name
        : alias is not null && values.ContainsKey(alias) ? alias
        : null;

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

    public string GetRequiredRawString(string key) =>
        GetRawString(key) is { Length: > 0 } value
            ? value
            : throw new NodeParseException($"缺失必填字段或为空: {key}");

    public int? GetInt(string key) =>
        int.TryParse(GetRawString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public int GetRequiredInt(string key)
    {
        var value = GetInt(key);
        if (value is > 0 and <= 65535)
        {
            return value.Value;
        }

        throw new NodeParseException($"缺失必填字段或端口格式无效: {key}");
    }

    public bool GetBool(string key) => GetNullableBool(key) ?? false;

    public bool? GetNullableBool(string key)
    {
        string? rawValue = GetRawString(key);
        if (rawValue == null)
        {
            return null;
        }

        return bool.TryParse(rawValue, out var value)
            ? value
            : throw new NodeParseException($"字段 '{key}' 必须是 true 或 false");
    }

    public ClashObject? GetObject(string key) => GetValue(key) as ClashObject;

    public IEnumerable<KeyValuePair<string, object?>> Properties => values;

    private static object? Normalize(object? value, HashSet<object> ancestors, NormalizationBudget budget, int depth)
    {
        budget.ConsumeValue(value);
        if (value is IDictionary dictionary)
        {
            return new ClashObject(NormalizeDictionary(dictionary, ancestors, budget, depth));
        }

        if (value is IEnumerable sequence and not string)
        {
            EnterContainer(sequence, ancestors, depth);
            try
            {
                var normalized = new List<object?>();
                foreach (object? item in sequence)
                    normalized.Add(Normalize(item, ancestors, budget, depth + 1));
                return normalized;
            }
            finally
            {
                ancestors.Remove(sequence);
            }
        }

        return value;
    }

    private static void EnterContainer(object container, HashSet<object> ancestors, int depth)
    {
        if (depth >= MaximumDepth)
            throw new NodeParseException($"YAML 嵌套深度不能达到 {MaximumDepth} 层。");
        if (!ancestors.Add(container))
            throw new NodeParseException("YAML 包含循环引用，无法安全转换。");
    }
}

public sealed class ClashProxyNode : ClashObject
{
    public ClashProxyNode(IDictionary source) : base(source) { }

    internal ClashProxyNode(IDictionary source, NormalizationBudget budget) : base(source, budget) { }

    public string? Type => GetString("type");
    public string Name => GetString("name") ?? "<未命名>";
}
