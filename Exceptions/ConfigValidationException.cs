using BoxForge.Models;

namespace BoxForge.Exceptions;

public sealed class ConfigValidationException(
    IReadOnlyList<ConfigDiagnostic> diagnostics)
    : Exception(string.Join(
        Environment.NewLine,
        diagnostics.Select(item => $"{item.Code} {item.Path}: {item.Message}")))
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
}
