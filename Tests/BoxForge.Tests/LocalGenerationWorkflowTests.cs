using BoxForge.Engine;
using BoxForge.Exceptions;
using BoxForge.Models;
using BoxForge.Workflows;
using BoxForge.App;
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
    public async Task EngineFailureDiscardsStagingWithoutPublishingBatch()
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

    [TestCase(false)]
    [TestCase(true)]
    public void PublicationFailureRestoresOldOutputOrPreservesBackupAndBothErrors(bool restorationFails)
    {
        string staging = CreateDirectory("staging");
        string output = CreateDirectory("output");
        File.WriteAllText(System.IO.Path.Combine(staging, "new.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(output, "old.txt"), "old");
        var workflow = CreateWorkflow(new RecordingEngine(_ => throw new AssertionException("No conversion expected")));
        var publishError = new IOException("publish failed");
        var restoreError = new UnauthorizedAccessException("restore failed");
        int moves = 0;
        string? backup = null;
        void Move(string source, string destination)
        {
            moves++;
            if (moves == 1) backup = destination;
            if (moves == 2) throw publishError;
            if (moves == 3 && restorationFails) throw restoreError;
            Directory.Move(source, destination);
        }

        if (restorationFails)
        {
            var error = Assert.Throws<AggregateException>(() => workflow.ReplaceOutputDirectory(staging, output, Move));
            Assert.Multiple(() =>
            {
                Assert.That(error!.InnerExceptions, Is.EqualTo(new Exception[] { publishError, restoreError }));
                Assert.That(error.Message, Does.Contain(backup!));
                Assert.That(File.ReadAllText(System.IO.Path.Combine(backup!, "old.txt")), Is.EqualTo("old"));
                Assert.That(Directory.Exists(output), Is.False);
            });
        }
        else
        {
            Assert.That(Assert.Throws<IOException>(() => workflow.ReplaceOutputDirectory(staging, output, Move)),
                Is.SameAs(publishError));
            Assert.That(File.ReadAllText(System.IO.Path.Combine(output, "old.txt")), Is.EqualTo("old"));
            Assert.That(Directory.Exists(backup), Is.False);
        }
        Assert.That(File.ReadAllText(System.IO.Path.Combine(staging, "new.txt")), Is.EqualTo("new"));
    }

    [Test]
    public void RealDirectoryMoveFailureRestoresExistingOutput()
    {
        string output = CreateDirectory("output");
        File.WriteAllText(System.IO.Path.Combine(output, "old.txt"), "old");
        var workflow = CreateWorkflow(new RecordingEngine(_ => throw new AssertionException("No conversion expected")));
        Assert.Throws<DirectoryNotFoundException>(() => workflow.ReplaceOutputDirectory(
            System.IO.Path.Combine(temporaryDirectory, "missing-staging"), output));
        Assert.That(File.ReadAllText(System.IO.Path.Combine(output, "old.txt")), Is.EqualTo("old"));
        Assert.That(Directory.GetDirectories(temporaryDirectory), Is.EqualTo(new[] { output }));
    }

    [Test]
    public async Task UnexpectedWorkflowFailureDoesNotInventBatchStatistics()
    {
        var logger = new RecordingLogger<GenerateCommandRunner>();
        var runner = new GenerateCommandRunner(CreateWorkflow(
            new RecordingEngine(_ => throw new AssertionException("No conversion expected"))), logger);
        int exitCode = await runner.RunAsync(["generate", "--input-dir", "invalid\0path"]);
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(GenerateCommandRunner.FailureExitCode));
            Assert.That(logger.Entries.Any(entry => entry.Message.Contains("本地批量生成失败", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.All(entry => !entry.Message.Contains("共 ", StringComparison.Ordinal)), Is.True);
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
