using SubConvert.Models;

namespace SubConvert.Cli;

public sealed record GenerateCommandOptions(
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<TargetPlatform> Platforms);

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

public static class GenerateCommandParser
{
    public const string Usage =
        "用法: dotnet run -- generate [--input-dir <目录>] " +
        "[--output-dir <目录>] [--platform Android|Linux|Windows|all]";

    private static readonly IReadOnlyList<TargetPlatform> AllPlatforms =
    [
        TargetPlatform.Android,
        TargetPlatform.Linux,
        TargetPlatform.Windows
    ];

    public static bool IsGenerateCommand(IReadOnlyList<string> arguments) =>
        arguments.Count > 0
        && string.Equals(
            arguments[0],
            "generate",
            StringComparison.OrdinalIgnoreCase);

    public static GenerateCommandParseResult Parse(
        IReadOnlyList<string> arguments)
    {
        if (!IsGenerateCommand(arguments))
        {
            return GenerateCommandParseResult.Failure(
                "缺少 generate 子命令。");
        }

        string inputDirectory = "clashConfigs";
        string outputDirectory = "singboxConfigs";
        IReadOnlyList<TargetPlatform> platforms = AllPlatforms;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            string option;
            string? inlineValue = null;
            int equalsIndex = argument.IndexOf('=');
            if (equalsIndex >= 0)
            {
                option = argument[..equalsIndex];
                inlineValue = argument[(equalsIndex + 1)..];
            }
            else
            {
                option = argument;
            }

            if (option is not ("--input-dir" or "--output-dir" or "--platform"))
            {
                return GenerateCommandParseResult.Failure(
                    $"未知参数: {argument}");
            }

            if (!seenOptions.Add(option))
            {
                return GenerateCommandParseResult.Failure(
                    $"参数不能重复: {option}");
            }

            string? value = inlineValue;
            if (value == null)
            {
                if (++index >= arguments.Count)
                {
                    return GenerateCommandParseResult.Failure(
                        $"参数缺少值: {option}");
                }

                value = arguments[index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return GenerateCommandParseResult.Failure(
                    $"参数值不能为空: {option}");
            }

            switch (option)
            {
                case "--input-dir":
                    inputDirectory = value;
                    break;
                case "--output-dir":
                    outputDirectory = value;
                    break;
                case "--platform":
                    if (!TryParsePlatforms(value, out platforms))
                    {
                        return GenerateCommandParseResult.Failure(
                            $"不支持的平台: {value}");
                    }

                    break;
            }
        }

        return GenerateCommandParseResult.Success(new GenerateCommandOptions(
            inputDirectory,
            outputDirectory,
            platforms));
    }

    private static bool TryParsePlatforms(
        string value,
        out IReadOnlyList<TargetPlatform> platforms)
    {
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            platforms = AllPlatforms;
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TargetPlatform platform)
            && Enum.IsDefined(platform))
        {
            platforms = [platform];
            return true;
        }

        platforms = [];
        return false;
    }
}
