using BoxForge.Cli;
using BoxForge.Models;

namespace BoxForge.Tests;

[TestFixture]
public sealed class GenerateCommandParserTests
{
    [Test]
    public void GenerateCommandUsesDocumentedDefaults()
    {
        GenerateCommandParseResult result = GenerateCommandParser.Parse(
            ["generate"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(
                result.Options!.InputDirectory,
                Is.EqualTo(GenerateCommandOptions.DefaultInputDirectory));
            Assert.That(
                result.Options.OutputDirectory,
                Is.EqualTo(GenerateCommandOptions.DefaultOutputDirectory));
            Assert.That(
                result.Options.Platforms,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Android,
                    TargetPlatform.Linux,
                    TargetPlatform.Windows
                }));
        });
    }

    [Test]
    public void OptionsSupportInlineValuesAndCaseInsensitivePlatform()
    {
        GenerateCommandParseResult result = GenerateCommandParser.Parse(
        [
            "GENERATE",
            "--input-dir=inputs",
            "--output-dir=outputs",
            "--platform=linux"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Options!.InputDirectory, Is.EqualTo("inputs"));
            Assert.That(result.Options.OutputDirectory, Is.EqualTo("outputs"));
            Assert.That(
                result.Options.Platforms,
                Is.EqualTo(new[] { TargetPlatform.Linux }));
        });
    }

    [TestCaseSource(nameof(InvalidCommands))]
    public void InvalidArgumentsReturnActionableError(
        string[] arguments,
        string expectedError)
    {
        GenerateCommandParseResult result = GenerateCommandParser.Parse(arguments);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Options, Is.Null);
            Assert.That(result.Error, Is.EqualTo(expectedError));
        });
    }

    private static IEnumerable<TestCaseData> InvalidCommands()
    {
        yield return new TestCaseData(
            Array.Empty<string>(),
            "缺少 generate 子命令。");
        yield return new TestCaseData(
            new[] { "generate", "--platform" },
            "参数缺少值: --platform");
        yield return new TestCaseData(
            new[] { "generate", "--platform", "macOS" },
            "不支持的平台: macOS");
        yield return new TestCaseData(
            new[] { "generate", "--input-dir=a", "--input-dir=b" },
            "参数不能重复: --input-dir");
        yield return new TestCaseData(
            new[] { "generate", "--unknown=value" },
            "未知参数: --unknown=value");
    }
}
