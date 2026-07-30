namespace BoxForge.Models;

public sealed record ConfigWriteRequest(
    string Path,
    string Content,
    string? ChangeDescription = null
);
