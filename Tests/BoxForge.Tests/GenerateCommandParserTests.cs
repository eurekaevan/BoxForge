using System.Diagnostics;
using BoxForge.App;
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

    [TestCase("0")]
    [TestCase("1")]
    [TestCase("2")]
    [TestCase("Windows,Android")]
    [TestCase("Windows,Windows")]
    [TestCase("+1")]
    public void PlatformAcceptsNamesOnly(string value)
    {
        GenerateCommandParseResult result = GenerateCommandParser.Parse(
            ["generate", "--platform", value]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error, Is.EqualTo($"不支持的平台: {value}"));
        });
    }

    [Test]
    public void PlatformNameKeepsSurroundingWhitespaceTolerance()
    {
        GenerateCommandParseResult result = GenerateCommandParser.Parse(
            ["generate", "--platform", " linux "]);
        Assert.That(result.Options!.Platforms, Is.EqualTo(new[] { TargetPlatform.Linux }));
    }

    [TestCase("BOXFORGE_TailscaleEnabled")]
    [TestCase("BOXFORGE_Tailscale__Enabled")]
    [TestCase("BOXFORGE_SingboxApi__Enabled")]
    public async Task InvalidEnvironmentConfigurationReturnsSafeFailure(string key)
    {
        const string invalidValue = "invalid-sensitive-value";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Project references share the test host's dependency set in this output directory.
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "BoxForge.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "BoxForge.Tests.deps.json"));
        startInfo.ArgumentList.Add(typeof(GenerateCommandRunner).Assembly.Location);
        startInfo.ArgumentList.Add("generate");
        foreach (string environmentKey in startInfo.Environment.Keys.ToArray())
        {
            if (environmentKey.StartsWith("BOXFORGE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(environmentKey);
            }
        }

        startInfo.Environment[key] = invalidValue;
        using var process = Process.Start(startInfo)!;
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string error = await errorTask;
            Assert.Multiple(() =>
            {
                Assert.That(process.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
                Assert.That(error, Does.Contain("配置值格式无效"));
                Assert.That(error, Does.Contain("BOXFORGE_"));
                Assert.That(error + output, Does.Not.Contain(invalidValue));
                Assert.That(error, Does.Not.Contain("Unhandled exception"));
                Assert.That(error.Trim().Split('\n'), Has.Length.EqualTo(1));
            });
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
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
