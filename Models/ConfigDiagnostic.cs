namespace BoxForge.Models;

public sealed record ConfigDiagnostic(
    string Code,
    string Path,
    string Message
);
