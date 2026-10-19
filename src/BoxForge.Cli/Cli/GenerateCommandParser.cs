using BoxForge.Models;

namespace BoxForge.Cli;

public static class GenerateCommandParser
{
    public const string Usage =
        "用法: dotnet run -- generate [--input-dir <目录>] " +
        "[--output-dir <目录>] [--platform Android|Linux|Windows|all] [--profile <文件>]\n" +
        "      dotnet run -- profile show --resolved [--profile <文件>]";

    private static readonly IReadOnlyList<TargetPlatform> AllPlatforms =
    [
        TargetPlatform.Android,
        TargetPlatform.Linux,
        TargetPlatform.Windows
    ];

    public static GenerateCommandParseResult Parse(
        IReadOnlyList<string> arguments)
    {
        bool showProfile = arguments.Count >= 2
            && string.Equals(arguments[0], "profile", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[1], "show", StringComparison.OrdinalIgnoreCase);
        if (!showProfile && !IsGenerateCommand(arguments))
        {
            return GenerateCommandParseResult.Failure(
                "缺少 generate 子命令。");
        }

        string inputDirectory = GenerateCommandOptions.DefaultInputDirectory;
        string outputDirectory = GenerateCommandOptions.DefaultOutputDirectory;
        IReadOnlyList<TargetPlatform> platforms = AllPlatforms;
        string? profilePath = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (int index = showProfile ? 2 : 1; index < arguments.Count; index++)
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

            if (showProfile ? option is not ("--profile" or "--resolved")
                : option is not ("--input-dir" or "--output-dir" or "--platform" or "--profile"))
            {
                return GenerateCommandParseResult.Failure(
                    $"未知参数: {argument}");
            }

            if (!seenOptions.Add(option))
            {
                return GenerateCommandParseResult.Failure(
                    $"参数不能重复: {option}");
            }

            if (option == "--resolved")
            {
                if (inlineValue != null)
                    return GenerateCommandParseResult.Failure("参数不接受值: --resolved");
                continue;
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
                case "--profile":
                    profilePath = value;
                    break;
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

        if (showProfile && !seenOptions.Contains("--resolved"))
            return GenerateCommandParseResult.Failure("profile show 需要 --resolved。");

        return GenerateCommandParseResult.Success(new GenerateCommandOptions(
            inputDirectory,
            outputDirectory,
            platforms)
        { ProfilePath = profilePath, ShowResolved = showProfile });
    }

    private static bool IsGenerateCommand(IReadOnlyList<string> arguments) =>
        arguments.Count > 0
        && string.Equals(
            arguments[0],
            "generate",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePlatforms(
        string value,
        out IReadOnlyList<TargetPlatform> platforms)
    {
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            platforms = AllPlatforms;
            return true;
        }

        string platformName = value.Trim();
        foreach (TargetPlatform platform in AllPlatforms)
        {
            if (string.Equals(platformName, platform.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                platforms = [platform];
                return true;
            }
        }

        platforms = [];
        return false;
    }
}
