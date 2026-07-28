namespace SubConvert.Models;

public sealed record ConfigWriteRequest(
    string Path,
    string Content,
    string? ChangeDescription = null
);
