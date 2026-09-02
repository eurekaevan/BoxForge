using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoxForge.Tests;

[TestFixture]
public sealed class LocalGenerationWorkflowTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"boxforge-workflow-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task ReadsYamlAndPublishesArtifactsReturnedByEngine()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = System.IO.Path.Combine(
            temporaryDirectory,
            "output");
        const string yaml = "deliberately invalid yaml: [";
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(inputDirectory, "sample.yaml"),
            yaml);
        var engine = new RecordingEngine(request => new ConversionBundle(
            request.Name,
            [
                new ConversionArtifact(
                    TargetPlatform.Android,
                    "android-content",
                    new string('a', 64)),
                new ConversionArtifact(
                    TargetPlatform.Linux,
                    "linux-content",
                    new string('b', 64))
            ]));
        var workflow = CreateWorkflow(engine);

        LocalGenerationSummary summary = await workflow.GenerateAsync(
            new LocalGenerationRequest(
                inputDirectory,
                outputDirectory,
                [TargetPlatform.Linux, TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.EqualTo(2));
            Assert.That(summary.Skipped, Is.Zero);
            Assert.That(summary.Failed, Is.Zero);
            Assert.That(summary.Discarded, Is.Zero);
            Assert.That(engine.Requests, Has.Count.EqualTo(1));
            Assert.That(engine.Requests[0].Name, Is.EqualTo("sample"));
            Assert.That(engine.Requests[0].ClashYaml, Is.EqualTo(yaml));
            Assert.That(
                engine.Requests[0].Platforms,
                Is.EqualTo(new[]
                {
                    TargetPlatform.Linux,
                    TargetPlatform.Android
                }));
            Assert.That(
                File.ReadAllText(GetOutputPath(
                    outputDirectory,
                    "sample",
                    TargetPlatform.Android)),
                Is.EqualTo("android-content"));
            Assert.That(
                File.ReadAllText(GetOutputPath(
                    outputDirectory,
                    "sample",
                    TargetPlatform.Linux)),
                Is.EqualTo("linux-content"));
        });
    }

    [Test]
    public async Task EngineFailurePreservesExistingOutputAndRollsBackBatch()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = CreateDirectory("output");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(inputDirectory, "a.yaml"),
            "first");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(inputDirectory, "b.yaml"),
            "second");
        string existingFile = System.IO.Path.Combine(
            outputDirectory,
            "existing.txt");
        await File.WriteAllTextAsync(existingFile, "preserve-me");
        var engine = new RecordingEngine(request =>
        {
            if (request.Name == "b")
            {
                throw new InvalidOperationException("simulated failure");
            }

            return new ConversionBundle(
                request.Name,
                [
                    new ConversionArtifact(
                        TargetPlatform.Android,
                        "new-content",
                        new string('c', 64))
                ]);
        });
        var workflow = CreateWorkflow(engine);

        LocalGenerationSummary summary = await workflow.GenerateAsync(
            new LocalGenerationRequest(
                inputDirectory,
                outputDirectory,
                [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.Zero);
            Assert.That(summary.Skipped, Is.Zero);
            Assert.That(summary.Failed, Is.EqualTo(1));
            Assert.That(summary.Discarded, Is.EqualTo(1));
            Assert.That(engine.Requests.Select(request => request.Name),
                Is.EqualTo(new[] { "a", "b" }));
            Assert.That(File.ReadAllText(existingFile), Is.EqualTo("preserve-me"));
            Assert.That(
                File.Exists(GetOutputPath(
                    outputDirectory,
                    "a",
                    TargetPlatform.Android)),
                Is.False);
        });
    }

    [Test]
    public async Task PlatformFailureUsesPlatformSpecificLogEvent()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = System.IO.Path.Combine(
            temporaryDirectory,
            "output");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(inputDirectory, "sample.yaml"),
            "input");
        var engine = new RecordingEngine(_ =>
            throw new BoxForgePlatformConversionException(
                TargetPlatform.Linux,
                new InvalidOperationException("simulated platform failure")));
        var logger = new RecordingLogger<LocalGenerationWorkflow>();
        var workflow = new LocalGenerationWorkflow(engine, logger);

        LocalGenerationSummary summary = await workflow.GenerateAsync(
            new LocalGenerationRequest(
                inputDirectory,
                outputDirectory,
                [TargetPlatform.Android, TargetPlatform.Linux]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Failed, Is.EqualTo(2));
            Assert.That(
                logger.Entries.Select(entry => entry.EventId.Id),
                Does.Contain(13));
            Assert.That(
                logger.Entries.Select(entry => entry.EventId.Id),
                Does.Not.Contain(11));
            Assert.That(
                logger.Entries.Single(entry => entry.EventId.Id == 13).Message,
                Does.Contain("[Linux]"));
        });
    }

    private string CreateDirectory(string name)
    {
        string path = System.IO.Path.Combine(temporaryDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetOutputPath(
        string outputDirectory,
        string name,
        TargetPlatform platform) =>
        System.IO.Path.Combine(
            outputDirectory,
            name,
            platform.ToString(),
            "config.json");

    private static LocalGenerationWorkflow CreateWorkflow(
        IBoxForgeEngine engine) =>
        new(
            engine,
            NullLogger<LocalGenerationWorkflow>.Instance);

    private sealed class RecordingEngine(
        Func<ConversionRequest, ConversionBundle> convert) : IBoxForgeEngine
    {
        public List<ConversionRequest> Requests { get; } = [];

        public Task<ConversionBundle> ConvertAsync(
            ConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(convert(request));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(
                eventId,
                formatter(state, exception)));
        }
    }

    private sealed record LogEntry(EventId EventId, string Message);
}
