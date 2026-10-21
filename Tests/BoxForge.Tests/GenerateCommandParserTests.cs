using System.Diagnostics;
using System.Text.Json;
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
    [TestCase("BOXFORGE_features__api__enabled")]
    [TestCase("BOXFORGE_Profile__typo")]
    [TestCase("BOXFORGE_typo")]
    public async Task InvalidEnvironmentConfigurationReturnsSafeFailure(string key)
    {
        const string invalidValue = "invalid-sensitive-value";
        var result = await RunCliAsync(["generate"], new() { [key] = invalidValue });
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(result.Error, Does.Contain("配置值格式无效"));
            Assert.That(result.Error, Does.Contain("BOXFORGE_"));
            Assert.That(result.Error + result.Output, Does.Not.Contain(invalidValue));
            Assert.That(result.Error, Does.Not.Contain("Unhandled exception"));
            Assert.That(result.Error.Trim().Split('\n'), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public async Task ProfileCommandsGenerateTaiwanAndReportResolvedJsonWithCliPrecedence()
    {
        string directory = Directory.CreateTempSubdirectory("boxforge-cli-profile-").FullName;
        try
        {
            string path = Path.Combine(directory, "profile.json");
            await File.WriteAllTextAsync(path, ProfilePlannerTests.TaiwanProfileJson());
            var environment = new Dictionary<string, string>
            {
                ["BOXFORGE_Profile"] = Path.Combine(directory, "missing.json"),
                ["BOXFORGE_features__api__enabled"] = "true"
            };
            var shown = await RunCliAsync(["profile", "show", "--resolved", "--profile=" + path], environment);
            Assert.That(shown.ExitCode, Is.Zero, shown.Error);
            using var resolved = JsonDocument.Parse(shown.Output);
            Assert.That(resolved.RootElement.GetProperty("route").GetProperty("regions")[4].GetProperty("id").GetString(), Is.EqualTo("tw"));
            Assert.That(resolved.RootElement.GetProperty("route").GetProperty("services")[0].GetProperty("default_region").GetString(), Is.EqualTo("tw"));
            Assert.That(resolved.RootElement.GetProperty("route").GetProperty("services")[5].GetProperty("id").GetString(), Is.EqualTo("custom"));
            Assert.That(resolved.RootElement.GetProperty("route").GetProperty("rule_sets").EnumerateArray()
                .Single(source => source.GetProperty("tags")[0].GetString() == "ai").GetProperty("url").GetString(),
                Is.EqualTo("https://rules.example.net/ai.srs"));
            Assert.That(resolved.RootElement.GetProperty("features").GetProperty("api").GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(resolved.RootElement.GetProperty("dns").GetProperty("resolvers")[0].GetProperty("server").GetString(), Is.EqualTo("203.0.113.53"));
            string input = Directory.CreateDirectory(Path.Combine(directory, "input")).FullName;
            string output = Path.Combine(directory, "output");
            await File.WriteAllTextAsync(Path.Combine(input, "tw.yaml"), ProfileConfigurationTests.Yaml("TW01", "TW02"));
            string[] arguments = ["generate", "--profile", path, "--input-dir", input, "--output-dir", output];
            var generated = await RunCliAsync(arguments, environment);
            Assert.That(generated.ExitCode, Is.Zero, generated.Output + generated.Error);
            var files = Directory.GetFiles(output, "config.json", SearchOption.AllDirectories);
            Assert.That(files, Has.Length.EqualTo(3));
            var before = files.ToDictionary(file => file, File.ReadAllBytes);
            foreach (string file in files)
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                Assert.That(document.RootElement.GetProperty("outbounds").EnumerateArray()
                    .Select(outbound => outbound.GetProperty("tag").GetString()), Does.Contain("🇹🇼 TW AUTO"));
                Assert.That(document.RootElement.GetProperty("outbounds").EnumerateArray()
                    .Single(outbound => outbound.GetProperty("tag").GetString() == "AI Taiwan")
                    .GetProperty("default").GetString(), Is.EqualTo("🇹🇼 TW"));
                Assert.That(document.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "Custom Games"), Is.True);
                var aiSource = document.RootElement.GetProperty("route").GetProperty("rule_set").EnumerateArray()
                    .Single(source => source.GetProperty("tag").ValueKind == JsonValueKind.String && source.GetProperty("tag").GetString() == "ai");
                Assert.That(aiSource.GetProperty("url").GetString(), Is.EqualTo("https://rules.example.net/ai.srs"));
                Assert.That(aiSource.GetProperty("update_interval").GetString(), Is.EqualTo("12h"));
                var bootstrap = document.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray()
                    .Single(server => server.GetProperty("tag").GetString() == "dns-direct-alidns");
                Assert.That(bootstrap.GetProperty("server").GetString(), Is.EqualTo("203.0.113.53"));
                Assert.That(bootstrap.GetProperty("tls").GetProperty("server_name").GetString(), Is.EqualTo("dns.example.net"));
                Assert.That(document.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Any(rule => rule.TryGetProperty("ip_cidr", out var cidrs) && cidrs[0].GetString() == "203.0.113.53/32"), Is.True);
            }
            await File.WriteAllTextAsync(path, """{"unknown":true}""");
            var rejected = await RunCliAsync(arguments, environment);
            Assert.That(rejected.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(rejected.Error, Does.Contain("未知字段"));
            foreach (string file in files) Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(before[file]));
            await File.WriteAllTextAsync(path, """{"dns":{"resolvers":{"replace":[{"id":"alidns","server":"dns.example.net","server_name":"dns.example.net"}]}}}""");
            rejected = await RunCliAsync(arguments, environment);
            Assert.That(rejected.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(rejected.Error, Does.Contain("resolvers.server"));
            foreach (string file in files) Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(before[file]));
            await File.WriteAllTextAsync(path, """{"route":{"rule_sets":{"append":[{"tags":["missing"],"update_interval":"12h"}]}}}""");
            rejected = await RunCliAsync(arguments, environment);
            Assert.That(rejected.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(rejected.Error, Does.Contain("缺失必填字段"));
            foreach (string file in files) Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(before[file]));
            await File.WriteAllTextAsync(path, """{"route":{"services":{"replace":[{"id":"ai","name":"AI","rule_sets":["ai"],"precedes_domestic_routes":true,"default_region":"missing"}]}}}""");
            rejected = await RunCliAsync(arguments, environment);
            Assert.That(rejected.ExitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(rejected.Error, Does.Contain("default_region"));
            foreach (string file in files) Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(before[file]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string[] arguments, Dictionary<string, string> environment)
    {
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
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (string environmentKey in startInfo.Environment.Keys.ToArray())
        {
            if (environmentKey.StartsWith("BOXFORGE_", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(environmentKey);
            }
        }


        foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        using var process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
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
        yield return new TestCaseData(new[] { "generate", "--profile" }, "参数缺少值: --profile");
        yield return new TestCaseData(new[] { "generate", "--profile=" }, "参数值不能为空: --profile");
        yield return new TestCaseData(new[] { "generate", "--profile=a", "--profile=b" }, "参数不能重复: --profile");
        yield return new TestCaseData(new[] { "profile", "show" }, "profile show 需要 --resolved。");
        yield return new TestCaseData(new[] { "profile", "show", "--resolved=true" }, "参数不接受值: --resolved");
        yield return new TestCaseData(new[] { "profile", "show", "--resolved", "--platform=all" }, "未知参数: --platform=all");
        yield return new TestCaseData(new[] { "profile", "show", "--resolved", "--resolved" }, "参数不能重复: --resolved");
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
