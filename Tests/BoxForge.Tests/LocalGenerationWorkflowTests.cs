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
            Assert.That(Directory.GetDirectories(temporaryDirectory, ".output.boxforge-*"), Is.Empty);
            Assert.That(
                File.Exists(GetOutputPath(
                    outputDirectory,
                    "a",
                    TargetPlatform.Android)),
                Is.False);
        });
    }

    [Test]
    public async Task DiscoversOnlyTopLevelYamlFilesInOrdinalFileNameOrder()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = System.IO.Path.Combine(temporaryDirectory, "output");
        foreach (string name in new[] { "z.YaMl", "a.yaml", "B.YML", "ignored.txt", "ignored.yaml.bak" })
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(inputDirectory, name), name);
        }
        string nestedDirectory = System.IO.Path.Combine(inputDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(nestedDirectory, "nested.yaml"), "nested");
        var engine = new RecordingEngine(request => new ConversionBundle(request.Name,
            [new ConversionArtifact(TargetPlatform.Android, request.ClashYaml, new string('a', 64))]));

        LocalGenerationSummary summary = await CreateWorkflow(engine).GenerateAsync(
            new LocalGenerationRequest(inputDirectory, outputDirectory, [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.EqualTo(3));
            Assert.That(summary.Failed, Is.Zero);
            Assert.That(engine.Requests.Select(request => request.Name), Is.EqualTo(new[] { "B", "a", "z" }));
            Assert.That(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public async Task DuplicateStemsDiscardTheEntireBatchAndPreserveOldOutput()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = CreateDirectory("output");
        foreach (string name in new[] { "good.yaml", "sample.yaml", "sample.yml" })
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(inputDirectory, name), "input");
        }
        string existingFile = System.IO.Path.Combine(outputDirectory, "old.txt");
        await File.WriteAllTextAsync(existingFile, "old");
        var engine = new RecordingEngine(request => new ConversionBundle(request.Name,
            [new ConversionArtifact(TargetPlatform.Android, "new", new string('a', 64))]));

        LocalGenerationSummary summary = await CreateWorkflow(engine).GenerateAsync(
            new LocalGenerationRequest(inputDirectory, outputDirectory, [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.Zero);
            Assert.That(summary.Failed, Is.EqualTo(2));
            Assert.That(summary.Discarded, Is.EqualTo(1));
            Assert.That(engine.Requests.Select(request => request.Name), Is.EqualTo(new[] { "good" }));
            Assert.That(File.ReadAllText(existingFile), Is.EqualTo("old"));
            Assert.That(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { existingFile }));
            Assert.That(Directory.GetDirectories(temporaryDirectory, ".output.boxforge-*"), Is.Empty);
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RejectsOutputThatPhysicallyEqualsOrContainsInput(bool ancestor, bool throughSymbolicLink)
    {
        string parentDirectory = CreateDirectory("parent");
        string inputDirectory = System.IO.Path.Combine(parentDirectory, "input");
        Directory.CreateDirectory(inputDirectory);
        string inputFile = System.IO.Path.Combine(inputDirectory, "sample.yaml");
        await File.WriteAllTextAsync(inputFile, "preserve-input");
        string outputDirectory = ancestor ? parentDirectory : inputDirectory;
        if (throughSymbolicLink)
        {
            string link = System.IO.Path.Combine(temporaryDirectory, "output-link");
            try
            {
                Directory.CreateSymbolicLink(link, outputDirectory);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException)
            {
                Assert.Ignore($"Creating symbolic links is unavailable: {ex.Message}");
            }
            outputDirectory = link;
        }
        var engine = new RecordingEngine(_ => throw new AssertionException("Unsafe paths must be rejected before conversion"));

        LocalGenerationSummary summary = await CreateWorkflow(engine).GenerateAsync(
            new LocalGenerationRequest(inputDirectory, outputDirectory, [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.Zero);
            Assert.That(summary.Failed, Is.EqualTo(1));
            Assert.That(engine.Requests, Is.Empty);
            Assert.That(File.ReadAllText(inputFile), Is.EqualTo("preserve-input"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnchangedArtifactsKeepOutputUnlessItContainsStaleFiles(bool hasStaleFile)
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = CreateDirectory("output");
        await File.WriteAllTextAsync(System.IO.Path.Combine(inputDirectory, "sample.yaml"), "input");
        string existingFile = GetOutputPath(outputDirectory, "sample", TargetPlatform.Android);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(existingFile)!);
        await File.WriteAllTextAsync(existingFile, "same");
        string emptyDirectory = System.IO.Path.Combine(outputDirectory, "directory-marker");
        Directory.CreateDirectory(emptyDirectory);
        string staleFile = System.IO.Path.Combine(outputDirectory, "stale.txt");
        if (hasStaleFile)
        {
            await File.WriteAllTextAsync(staleFile, "stale");
        }
        var engine = new RecordingEngine(request => new ConversionBundle(request.Name,
            [new ConversionArtifact(TargetPlatform.Android, "same", new string('a', 64))]));

        LocalGenerationSummary summary = await CreateWorkflow(engine).GenerateAsync(
            new LocalGenerationRequest(inputDirectory, outputDirectory, [TargetPlatform.Android]));

        Assert.Multiple(() =>
        {
            Assert.That(summary.Succeeded, Is.Zero);
            Assert.That(summary.Skipped, Is.EqualTo(1));
            Assert.That(summary.Failed, Is.Zero);
            Assert.That(File.ReadAllText(existingFile), Is.EqualTo("same"));
            Assert.That(File.Exists(staleFile), Is.False);
            Assert.That(Directory.Exists(emptyDirectory), Is.EqualTo(!hasStaleFile));
            Assert.That(Directory.GetDirectories(temporaryDirectory, ".output.boxforge-*"), Is.Empty);
        });
    }

    [Test]
    public async Task CancellationDiscardsStagedArtifactsAndPreservesOldOutput()
    {
        string inputDirectory = CreateDirectory("input");
        string outputDirectory = CreateDirectory("output");
        await File.WriteAllTextAsync(System.IO.Path.Combine(inputDirectory, "a.yaml"), "first");
        await File.WriteAllTextAsync(System.IO.Path.Combine(inputDirectory, "b.yaml"), "second");
        string existingFile = System.IO.Path.Combine(outputDirectory, "old.txt");
        await File.WriteAllTextAsync(existingFile, "old");
        using var cancellation = new CancellationTokenSource();
        var engine = new RecordingEngine(request =>
        {
            if (request.Name == "b")
            {
                cancellation.Cancel();
            }
            return new ConversionBundle(request.Name,
                [new ConversionArtifact(TargetPlatform.Android, "new", new string('a', 64))]);
        });

        Assert.CatchAsync<OperationCanceledException>(() => CreateWorkflow(engine).GenerateAsync(
            new LocalGenerationRequest(inputDirectory, outputDirectory, [TargetPlatform.Android]), cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(engine.Requests.Select(request => request.Name), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(File.ReadAllText(existingFile), Is.EqualTo("old"));
            Assert.That(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { existingFile }));
            Assert.That(Directory.GetDirectories(temporaryDirectory, ".output.boxforge-*"), Is.Empty);
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
    public async Task PublicationFailureRestoresOldOutputOrPreservesBackupAndBothErrors(bool restorationFails)
    {
        string output = CreateDirectory("output");
        File.WriteAllText(System.IO.Path.Combine(output, "old.txt"), "old");
        var publishError = new IOException("publish failed");
        var restoreError = new UnauthorizedAccessException("restore failed");
        int moves = 0;
        string? backup = null;
        string? staging = null;
        void Move(string source, string destination)
        {
            moves++;
            if (moves == 1) backup = destination;
            if (moves == 2)
            {
                staging = source;
                throw publishError;
            }
            if (moves == 3 && restorationFails) throw restoreError;
            Directory.Move(source, destination);
        }

        using var transaction = new OutputTransaction(output, NullLogger<LocalGenerationWorkflow>.Instance, Move);
        await transaction.StageAsync("sample", new ConversionArtifact(TargetPlatform.Android, "new", "unused"), CancellationToken.None);

        if (restorationFails)
        {
            var error = Assert.Throws<AggregateException>(transaction.Commit);
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
            Assert.That(Assert.Throws<IOException>(transaction.Commit),
                Is.SameAs(publishError));
            Assert.That(File.ReadAllText(System.IO.Path.Combine(output, "old.txt")), Is.EqualTo("old"));
            Assert.That(Directory.Exists(backup), Is.False);
        }
        Assert.That(File.ReadAllText(GetOutputPath(staging!, "sample", TargetPlatform.Android)), Is.EqualTo("new"));
    }

    [Test]
    public async Task RealDirectoryMoveFailureRestoresExistingOutput()
    {
        string output = CreateDirectory("output");
        File.WriteAllText(System.IO.Path.Combine(output, "old.txt"), "old");
        using var transaction = new OutputTransaction(output, NullLogger<LocalGenerationWorkflow>.Instance);
        await transaction.StageAsync("sample", new ConversionArtifact(TargetPlatform.Android, "new", "unused"), CancellationToken.None);
        string staging = Directory.GetDirectories(temporaryDirectory, ".output.boxforge-*").Single();
        Directory.Delete(staging, recursive: true);
        Assert.Throws<DirectoryNotFoundException>(transaction.Commit);
        Assert.That(File.ReadAllText(System.IO.Path.Combine(output, "old.txt")), Is.EqualTo("old"));
        Assert.That(Directory.GetDirectories(temporaryDirectory), Is.EqualTo(new[] { output }));
    }

    [Test]
    public async Task UnexpectedWorkflowFailureDoesNotInventBatchStatistics()
    {
        var logger = new RecordingLogger<GenerateCommandRunner>();
        var runner = new GenerateCommandRunner(CreateWorkflow(
            new RecordingEngine(_ => throw new AssertionException("No conversion expected"))), logger);
        int exitCode = await runner.RunAsync(new LocalGenerationRequest(
            "invalid\0path", temporaryDirectory, [TargetPlatform.Android]));
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
