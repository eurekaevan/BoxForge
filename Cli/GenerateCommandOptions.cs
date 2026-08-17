using BoxForge.Models;

namespace BoxForge.Cli;

public sealed record GenerateCommandOptions(
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<TargetPlatform> Platforms)
{
    public const string DefaultInputDirectory = "clashConfigs";
    public const string DefaultOutputDirectory = "singboxConfigs";
}

public sealed record GenerateCommandParseResult(
    GenerateCommandOptions? Options,
    string? Error)
{
    public bool IsSuccess => Options != null;

    public static GenerateCommandParseResult Success(
        GenerateCommandOptions options) =>
        new(options, null);

    public static GenerateCommandParseResult Failure(string error) =>
        new(null, error);
}
