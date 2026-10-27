using BoxForge.Models.Clash;

namespace BoxForge.Converters;

internal enum SourceFieldDisposition
{
    Mapped,
    Alias,
    IgnoredByDesign,
    UnsupportedSemantic
}

internal enum SourceFieldValueKind
{
    Scalar,
    Mapping,
    Sequence,
    Any
}

internal sealed record SourceFieldDefinition(
    string Name,
    SourceFieldDisposition Disposition,
    string? CanonicalName = null,
    string? Reason = null,
    SourceFieldSchema? NestedSchema = null,
    Func<NodeFieldReader, object?, string?>? ValidateValue = null,
    SourceFieldValueKind ValueKind = SourceFieldValueKind.Scalar);

internal sealed class SourceFieldSchema
{
    private readonly Dictionary<string, SourceFieldDefinition> fields =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> aliasesByCanonical = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SourceFieldDefinition> Fields => fields.Values;

    public SourceFieldSchema Include(SourceFieldSchema other)
    {
        foreach (SourceFieldDefinition definition in other.Fields)
        {
            Add(definition);
        }

        return this;
    }

    public SourceFieldSchema Mapped(params string[] names)
    {
        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.Mapped));
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

    public SourceFieldSchema Ignored(params string[] names)
    {
        foreach (string name in names)
        {
            Add(new SourceFieldDefinition(
                name, SourceFieldDisposition.IgnoredByDesign,
                ValueKind: SourceFieldValueKind.Any));
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

    public SourceFieldSchema Nested(string name, SourceFieldSchema schema,
        Func<NodeFieldReader, object?, string?>? guard = null,
        SourceFieldDisposition disposition = SourceFieldDisposition.Mapped)
    {
        Add(new SourceFieldDefinition(
            name, disposition, NestedSchema: schema, ValidateValue: guard,
            ValueKind: SourceFieldValueKind.Mapping));
        return this;
    }

    public SourceFieldSchema Conditional(
        string name,
        SourceFieldDisposition disposition,
        Func<NodeFieldReader, object?, string?> validate,
        SourceFieldValueKind valueKind = SourceFieldValueKind.Scalar)
    {
        Add(new SourceFieldDefinition(
            name, disposition, ValidateValue: validate, ValueKind: valueKind));
        return this;
    }

    internal NodeFieldReader Bind(ClashProxyNode source, out string name)
    {
        var reader = new NodeFieldReader(source, this);
        name = reader.RequiredString("name");
        Validate(reader);
        return reader;
    }

    internal string? FindAlias(ClashObject source, string name)
    {
        if (aliasesByCanonical.TryGetValue(name, out var aliases))
            foreach (string alias in aliases)
                if (source.GetPresentField(alias) is not null) return alias;
        return null;
    }

    internal SourceFieldSchema? NestedSchema(string name) => fields.GetValueOrDefault(name)?.NestedSchema;

    internal void Validate(NodeFieldReader reader)
    {
        var present = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, object? value) in reader.Properties
                     .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            string path = reader.FieldPath(name);
            if (!fields.TryGetValue(name, out SourceFieldDefinition? definition))
            {
                throw reader.ErrorAtActualField(name,
                    $"包含当前 BoxForge 尚未分类的字段 '{path}'，无法确认转换是否保持语义");
            }

            if (definition.Disposition == SourceFieldDisposition.UnsupportedSemantic)
            {
                throw reader.Error(
                    name, $"包含尚未支持的语义字段 '{path}'；{definition.Reason}");
            }

            string canonical = definition.CanonicalName ?? name;
            if (present.TryGetValue(canonical, out string? previous))
            {
                throw reader.Error(
                    name, $"字段 '{previous}' 与 '{path}' 同时指定同一语义，请只保留一个");
            }

            present.Add(canonical, path);

            // Nested guards run first so protocol preconditions keep their diagnostic precedence.
            string? error = definition.NestedSchema is not null ? definition.ValidateValue?.Invoke(reader, value) : null;
            if (error is not null) throw reader.Error(name, $"字段 '{path}' {error}");
            ValidateValueKind(reader, definition.ValueKind, value, name);
            if (definition.NestedSchema is null)
            {
                error = definition.ValidateValue?.Invoke(reader, value);
                if (error is not null) throw reader.Error(name, $"字段 '{path}' {error}");
            }

            if (definition.NestedSchema is not null)
            {
                if (value is not ClashObject)
                {
                    throw reader.Error(name, $"字段 '{path}' 必须是对象");
                }

                definition.NestedSchema.Validate(reader.OptionalObject(name)!);
            }
        }
    }

    private static void ValidateValueKind(
        NodeFieldReader reader,
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
            throw reader.Error(path, $"字段 '{reader.PresentField(path) ?? path}' 必须是{expected}");
        }
    }

    private void Add(SourceFieldDefinition definition)
    {
        if (!fields.TryAdd(definition.Name, definition))
        {
            throw new InvalidOperationException(
                $"重复的源字段声明: {definition.Name}");
        }
        if (definition.CanonicalName is { } canonical)
        {
            if (!aliasesByCanonical.TryGetValue(canonical, out var aliases))
                aliasesByCanonical[canonical] = aliases = [];
            aliases.Add(definition.Name);
        }
    }
}
