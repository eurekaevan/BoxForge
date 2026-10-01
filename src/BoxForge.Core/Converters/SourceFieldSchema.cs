using BoxForge.Exceptions;
using BoxForge.Models.Clash;

namespace BoxForge.Converters;

public enum SourceFieldDisposition
{
    Mapped,
    Alias,
    IgnoredByDesign,
    UnsupportedSemantic
}

public enum SourceFieldValueKind
{
    Scalar,
    Mapping,
    Sequence,
    Any
}

public sealed record SourceFieldDefinition(
    string Name,
    SourceFieldDisposition Disposition,
    string? CanonicalName = null,
    string? Reason = null,
    SourceFieldSchema? NestedSchema = null,
    Func<ClashProxyNode, object?, string?>? ValidateValue = null,
    SourceFieldValueKind ValueKind = SourceFieldValueKind.Scalar);

public sealed class SourceFieldSchema
{
    private readonly Dictionary<string, SourceFieldDefinition> fields =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<SourceFieldDefinition> Fields => fields.Values;

    public SourceFieldSchema Include(SourceFieldSchema other)
    {
        foreach (SourceFieldDefinition definition in other.Fields)
        {
            Add(definition);
        }

        return this;
    }

    public SourceFieldSchema Mapped(params string[] names) =>
        Mapped(SourceFieldValueKind.Scalar, names);

    public SourceFieldSchema Mapped(
        SourceFieldValueKind valueKind,
        params string[] names)
    {
        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.Mapped, ValueKind: valueKind));
        }

        return this;
    }

    public SourceFieldSchema Alias(string canonicalName, params string[] names)
    {
        if (!fields.TryGetValue(canonicalName, out SourceFieldDefinition? canonical))
        {
            throw new InvalidOperationException($"别名缺少对应的源字段声明: {canonicalName}");
        }

        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.Alias, canonicalName,
                NestedSchema: canonical.NestedSchema,
                ValidateValue: canonical.ValidateValue, ValueKind: canonical.ValueKind));
        }

        return this;
    }

    public SourceFieldSchema Ignored(string reason, params string[] names)
    {
        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.IgnoredByDesign,
                Reason: reason, ValueKind: SourceFieldValueKind.Any));
        }

        return this;
    }

    public SourceFieldSchema Unsupported(string reason, params string[] names)
    {
        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.UnsupportedSemantic,
                Reason: reason));
        }

        return this;
    }

    public SourceFieldSchema Nested(
        string name,
        SourceFieldSchema schema,
        Func<ClashProxyNode, object?, string?>? validate = null)
    {
        Add(new SourceFieldDefinition(
            name, SourceFieldDisposition.Mapped, NestedSchema: schema,
            ValueKind: SourceFieldValueKind.Mapping, ValidateValue: validate));
        return this;
    }

    public SourceFieldSchema Conditional(
        string name,
        SourceFieldDisposition disposition,
        Func<ClashProxyNode, object?, string?> validate,
        SourceFieldValueKind valueKind = SourceFieldValueKind.Scalar)
    {
        Add(new SourceFieldDefinition(
            name, disposition, ValidateValue: validate, ValueKind: valueKind));
        return this;
    }

    public SourceFieldSchema ConditionalAlias(
        string canonicalName,
        string alias,
        Func<ClashProxyNode, object?, string?> validate)
    {
        if (!fields.TryGetValue(canonicalName, out SourceFieldDefinition? canonical))
        {
            throw new InvalidOperationException($"别名缺少对应的源字段声明: {canonicalName}");
        }

        Add(new SourceFieldDefinition(
            alias, SourceFieldDisposition.Alias, canonicalName,
            ValidateValue: validate, ValueKind: canonical.ValueKind));
        return this;
    }

    public void Validate(ClashProxyNode node) =>
        ValidateObject(node, node, null);

    internal void ValidateNested(ClashObject source, ClashProxyNode node, string prefix) =>
        ValidateObject(source, node, prefix);

    private void ValidateObject(
        ClashObject source,
        ClashProxyNode node,
        string? prefix)
    {
        var present = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, object? value) in source.Properties
                     .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            string path = prefix is null ? name : $"{prefix}.{name}";
            if (!fields.TryGetValue(name, out SourceFieldDefinition? definition))
            {
                throw new NodeParseException(
                    $"包含当前 BoxForge 尚未分类的字段 '{path}'，无法确认转换是否保持语义");
            }

            if (definition.Disposition == SourceFieldDisposition.UnsupportedSemantic)
            {
                throw new NodeParseException(
                    $"包含尚未支持的语义字段 '{path}'；{definition.Reason}");
            }

            string canonical = definition.CanonicalName ?? name;
            if (present.TryGetValue(canonical, out string? previous))
            {
                throw new NodeParseException(
                    $"字段 '{previous}' 与 '{path}' 同时指定同一语义，请只保留一个");
            }

            present.Add(canonical, path);

            ValidateValueKind(definition.ValueKind, value, path);

            string? error = definition.ValidateValue?.Invoke(node, value);
            if (error is not null)
            {
                throw new NodeParseException($"字段 '{path}' {error}");
            }

            if (definition.NestedSchema is not null)
            {
                if (value is not ClashObject nested)
                {
                    throw new NodeParseException($"字段 '{path}' 必须是对象");
                }

                definition.NestedSchema.ValidateObject(nested, node, path);
            }
        }
    }

    private static void ValidateValueKind(
        SourceFieldValueKind kind,
        object? value,
        string path)
    {
        bool isMapping = value is ClashObject;
        bool isSequence = value is System.Collections.IEnumerable and not string;
        bool valid = kind switch
        {
            SourceFieldValueKind.Scalar => !isMapping && !isSequence,
            SourceFieldValueKind.Mapping => isMapping,
            SourceFieldValueKind.Sequence => isSequence && !isMapping,
            SourceFieldValueKind.Any => true,
            _ => false
        };
        if (value is not null && !valid)
        {
            string expected = kind switch
            {
                SourceFieldValueKind.Mapping => "对象",
                SourceFieldValueKind.Sequence => "列表",
                _ => "标量值"
            };
            throw new NodeParseException($"字段 '{path}' 必须是{expected}");
        }
    }

    private void Add(SourceFieldDefinition definition)
    {
        if (!fields.TryAdd(definition.Name, definition))
        {
            throw new InvalidOperationException(
                $"重复的源字段声明: {definition.Name}");
        }
    }
}
