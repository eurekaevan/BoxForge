using BoxForge.Cli;
using BoxForge.Models;

namespace BoxForge.Tests;

public sealed class GenerateCommandParserTests
{
    [Fact]
    public void Parse_UsesCiFriendlyDefaults()
    {
        var result = GenerateCommandParser.Parse(["generate"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("clashConfigs", result.Options!.InputDirectory);
        Assert.Equal("singboxConfigs", result.Options.OutputDirectory);
        Assert.Equal(
            [
                TargetPlatform.Android,
                TargetPlatform.Linux,
                TargetPlatform.Windows
            ],
            result.Options.Platforms);
    }

    [Theory]
    [InlineData("Android", TargetPlatform.Android)]
    [InlineData("linux", TargetPlatform.Linux)]
    [InlineData("WINDOWS", TargetPlatform.Windows)]
    public void Parse_AcceptsEveryPlatformCaseInsensitively(
        string value,
        TargetPlatform expected)
    {
        var result = GenerateCommandParser.Parse(
            ["generate", "--platform", value]);

        Assert.True(result.IsSuccess);
        Assert.Equal([expected], result.Options!.Platforms);
    }

    [Fact]
    public void Parse_AcceptsInlineOptionValues()
    {
        var result = GenerateCommandParser.Parse(
            [
                "generate",
                "--input-dir=input",
                "--output-dir=output",
                "--platform=all"
            ]);

        Assert.True(result.IsSuccess);
        Assert.Equal("input", result.Options!.InputDirectory);
        Assert.Equal("output", result.Options.OutputDirectory);
        Assert.Equal(3, result.Options.Platforms.Count);
    }

    [Theory]
    [InlineData("generate", "--platform", "ios")]
    [InlineData("generate", "--unknown", "value")]
    [InlineData("generate", "--input-dir")]
    [InlineData("generate", "--platform", "all", "--platform", "Linux")]
    public void Parse_RejectsInvalidArguments(params string[] arguments)
    {
        var result = GenerateCommandParser.Parse(arguments);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Error!);
    }
}
