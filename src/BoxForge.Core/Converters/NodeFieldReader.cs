using System.Globalization;
using BoxForge.Exceptions;
using BoxForge.Helpers;
using BoxForge.Models.Clash;

namespace BoxForge.Converters;

internal sealed class NodeFieldReader(ClashObject source, SourceFieldSchema? schema = null, string prefix = "")
{
    internal IEnumerable<KeyValuePair<string, object?>> Properties => source.Properties;

    internal string? PresentField(string name) =>
        source.GetPresentField(name) ?? schema?.FindAlias(source, name);

    internal object? Value(string field) => source.GetValue(PresentField(field) ?? field);

    internal string? RawString(string field)
    {
        object? value = Value(field);
        if (value is ClashObject or System.Collections.IEnumerable and not string)
            throw Error(field, $"字段 '{PresentField(field) ?? field}' 必须是标量值");
        return value?.ToString();
    }

    internal string? String(string field) => RawString(field) is { } value && !string.IsNullOrWhiteSpace(value)
        ? value.Trim() : null;

    internal string RequiredString(string field) => String(field)
        ?? throw Error(field, $"缺失必填字段或为空: {PresentField(field) ?? field}");

    internal string RequiredRawString(string field) => RawString(field) is { Length: > 0 } value
        ? value : throw Error(field, $"缺失必填字段或为空: {PresentField(field) ?? field}");

    internal int? OptionalInt(string field, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        string? raw = RawString(field);
        if (raw is null) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value >= minimum && value <= maximum) return value;
        throw Error(field, $"字段 '{PresentField(field) ?? field}' 必须是 {minimum}..{maximum} 的整数");
    }

    internal int RequiredPort(string field)
    {
        if (int.TryParse(RawString(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value is > 0 and <= 65535) return value;
        throw Error(field, $"缺失必填字段或端口格式无效: {PresentField(field) ?? field}");
    }

    internal bool? OptionalBool(string field)
    {
        string? raw = RawString(field);
        if (raw is null) return null;
        return bool.TryParse(raw, out bool value) ? value
            : throw Error(field, $"字段 '{PresentField(field) ?? field}' 必须是 true 或 false");
    }

    internal bool Bool(string field) => OptionalBool(field) ?? false;

    internal string? OptionalDuration(string field) => RawString(field) is { } value
        ? GoDurationHelper.ParsePositiveSecondsOrDuration(value, FieldPath(field)) : null;

    internal T? OptionalEnum<T>(string field, params (string Text, T Value)[] choices) where T : struct, Enum
    {
        string? raw = RawString(field);
        if (raw is null) return null;
        foreach (var choice in choices)
            if (raw == choice.Text) return choice.Value;
        throw Error(field, $"字段 '{PresentField(field) ?? field}' 的值不受支持");
    }

    internal NodeFieldReader? OptionalObject(string field) => Value(field) switch
    {
        null => null,
        ClashObject nested => new(nested, schema?.NestedSchema(field), FieldPath(field)),
        _ => throw Error(field, $"字段 '{PresentField(field) ?? field}' 必须是对象")
    };

    internal NodeParseException Error(string field, string message) => ErrorAtActualField(PresentField(field) ?? field, message);

    internal NodeParseException ErrorAtActualField(string actualField, string message) => NodeParseException.ForField(
        prefix.Length == 0 ? actualField : $"{prefix}.{actualField}", message);

    internal string FieldPath(string field)
    {
        string actual = PresentField(field) ?? field;
        return prefix.Length == 0 ? actual : $"{prefix}.{actual}";
    }
}
