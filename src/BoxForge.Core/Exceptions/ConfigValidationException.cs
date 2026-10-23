using System.Collections.Immutable;
using BoxForge.Models;

namespace BoxForge.Exceptions;

public sealed class ConfigValidationException : Exception
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public ConfigValidationException(IReadOnlyList<ConfigDiagnostic> diagnostics)
        : this(diagnostics.ToImmutableArray()) { }

    private ConfigValidationException(ImmutableArray<ConfigDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine,
            diagnostics.Select(item => $"{item.Code} {item.Path}: {item.Message}")))
    {
        Diagnostics = diagnostics;
    }

    internal static void ThrowIfAny(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0) throw new ConfigValidationException(diagnostics);
    }
}
