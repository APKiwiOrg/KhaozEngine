using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Benchmarks.Journal;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class MutationJournalBenchmarkTests
{
    [Fact]
    public void Parse_accepts_explicit_options_and_rejects_hard_limit_violations()
    {
        string database = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "journal-benchmark.db"));
        string output = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "journal-benchmark.json"));
        JournalBenchmarkConfig config = JournalBenchmarkConfig.Parse(new[]
        {
            "--journal", "--operations", "10000", "--players", "1000", "--seed", "835",
            "--workers", "3", "--payload-bytes", "192", "--database", database, "--output", output,
        });

        Assert.Equal(JournalBenchmarkMode.Benchmark, config.Mode);
        Assert.Equal(10_000, config.Operations);
        Assert.Equal(1_000, config.Players);
        Assert.Equal(835, config.Seed);
        Assert.Equal(3, config.Workers);
        Assert.Equal(192, config.PayloadBytes);
        Assert.Equal(database, config.DatabasePath);
        Assert.Equal(output, config.OutputPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--operations", "10000001" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--payload-bytes", "4097" }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--database", "relative.db" }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--output", "relative.json" }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--output", Path.Combine(Path.GetTempPath(), "baseline.txt") }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--database", output, "--output", output }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--unknown", "1" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal-soak", "--duration-seconds", "2", "--progress-seconds", "3" }));
    }

    [Fact]
    public async Task Output_writer_atomically_creates_and_safely_overwrites_stable_json()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"journal-output-{Guid.NewGuid():N}");
        string output = Path.Combine(directory, "baseline.json");
        try
        {
            var first = new JournalBenchmarkResult { Seed = 835, OperationsCompleted = 7 };
            var second = first with { OperationsCompleted = 8 };

            await JournalBenchmarkOutput.WriteAsync(first, output);
            Assert.Equal(first.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));

            await JournalBenchmarkOutput.WriteAsync(second, output);
            Assert.Equal(second.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Sql_server_credentials_are_environment_only_and_require_a_dedicated_catalog_before_io()
    {
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--sql-server", "Server=db;Database=production" }));
        Assert.Throws<ArgumentException>(() => JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--sql-server-env", "BAD-NAME" }));

        string variable = $"KE_JOURNAL_TEST_{Guid.NewGuid():N}".ToUpperInvariant();
        JournalBenchmarkConfig config = JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--operations", "7", "--players", "2", "--sql-server-env", variable });
        Assert.Equal(variable, config.SqlServerEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            InvalidOperationException missing = await Assert.ThrowsAsync<InvalidOperationException>(
                () => JournalBenchmarkRunner.RunAsync(config));
            Assert.DoesNotContain("Password", missing.Message, StringComparison.OrdinalIgnoreCase);

            Environment.SetEnvironmentVariable(variable, "   ");
            await Assert.ThrowsAsync<InvalidOperationException>(() => JournalBenchmarkRunner.RunAsync(config));

            Environment.SetEnvironmentVariable(
                variable,
                "Server=203.0.113.1;Initial Catalog=production;User ID=prod;Password=top-secret;Connect Timeout=1");
            ArgumentException production = await Assert.ThrowsAsync<ArgumentException>(
                () => JournalBenchmarkRunner.RunAsync(config));
            Assert.Contains("-journal-benchmark-", production.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret", production.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Seeded_workload_is_repeatable_mixed_and_bounded()
    {
        JournalBenchmarkConfig config = JournalBenchmarkConfig.Parse(
            new[] { "--journal", "--operations", "70", "--players", "100", "--seed", "835", "--payload-bytes", "192" });

        IReadOnlyList<JournalWorkloadStep> first = JournalBenchmarkRunner.GenerateWorkload(config);
        IReadOnlyList<JournalWorkloadStep> second = JournalBenchmarkRunner.GenerateWorkload(config);
        IReadOnlyList<JournalWorkloadStep> different = JournalBenchmarkRunner.GenerateWorkload(config with { Seed = 836 });

        Assert.Equal(first, second);
        Assert.False(first.Select(static step => step.Fingerprint)
            .SequenceEqual(different.Select(static step => step.Fingerprint), StringComparer.Ordinal));
        Assert.Equal(70, first.Count);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.InventoryChange);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.BankTransfer);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.Trade);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.ProjectionRead);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.OperationReplay);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.Snapshot);
        Assert.Contains(first, static step => step.Kind == JournalWorkloadKind.Compaction);
        Assert.All(first.Where(static step => step.IsMutation), step =>
        {
            Assert.InRange(step.Payload.Length, 1, config.PayloadBytes);
            Assert.NotEqual(Guid.Empty, step.OperationId);
            Assert.InRange(step.PrimaryPlayer, 0, config.Players - 1);
        });
    }

    [Fact]
    public void Result_json_has_stable_order_and_invariant_numbers()
    {
        var result = new JournalBenchmarkResult
        {
            Mode = "benchmark",
            Provider = "sqlite",
            Machine = "test-machine",
            Framework = ".NET test",
            ProcessorCount = 4,
            Workload = "mmo-mixed-v1",
            Seed = 835,
            Players = 1000,
            Workers = 3,
            PayloadBytes = 192,
            OperationsRequested = 10,
            OperationsCompleted = 10,
            MutationSubmissions = 7,
            Applied = 6,
            Replayed = 1,
            ThroughputPerSecond = 1234.5,
            P50Milliseconds = 0.25,
            P95Milliseconds = 1.5,
            P99Milliseconds = 2.75,
            ReplayRate = 0.1,
            RetryRate = 0.02,
            BusyRate = 0.03,
            BackpressureRate = 0.04,
            DatabaseBytes = 4096,
            EventTailLength = 12,
            ProjectionBytes = 256,
            CompactionLagVersions = 3,
            SerializationCount = 7,
            JournalWriteCount = 7,
            PeakReplayCandidates = 1,
            PrunedEventCount = 0,
            AllocationBytesPerOperation = 512.25,
            ChecksumFailures = 0,
            DuplicateEffectFailures = 0,
            SequenceFailures = 0,
            PartialCommitFailures = 0,
        };

        string first = result.ToJson();
        string second = result.ToJson();

        Assert.Equal(first, second);
        Assert.Equal(
            "{\"mode\":\"benchmark\",\"provider\":\"sqlite\",\"machine\":\"test-machine\",\"framework\":\".NET test\",\"processorCount\":4,\"workload\":\"mmo-mixed-v1\",\"seed\":835,\"players\":1000,\"workers\":3,\"payloadBytes\":192,\"operationsRequested\":10,\"operationsCompleted\":10,\"mutationSubmissions\":7,\"applied\":6,\"replayed\":1,\"throughputPerSecond\":1234.5,\"p50Milliseconds\":0.25,\"p95Milliseconds\":1.5,\"p99Milliseconds\":2.75,\"replayRate\":0.1,\"retryRate\":0.02,\"busyRate\":0.03,\"backpressureRate\":0.04,\"databaseBytes\":4096,\"eventTailLength\":12,\"projectionBytes\":256,\"compactionLagVersions\":3,\"serializationCount\":7,\"journalWriteCount\":7,\"peakReplayCandidates\":1,\"prunedEventCount\":0,\"allocationBytesPerOperation\":512.25,\"checksumFailures\":0,\"duplicateEffectFailures\":0,\"sequenceFailures\":0,\"partialCommitFailures\":0}",
            first);
        using JsonDocument _ = JsonDocument.Parse(first);
    }

    [Fact]
    public async Task Idle_and_one_percent_active_tick_work_scale_with_mutations()
    {
        JournalScalingProbe idle = await JournalBenchmarkRunner.RunScalingProbeAsync(10_000, 0, 835);
        JournalScalingProbe active = await JournalBenchmarkRunner.RunScalingProbeAsync(10_000, 100, 835);

        Assert.Equal(10_000, idle.ConnectedPlayers);
        Assert.Equal(10_000, idle.RosterCount);
        Assert.Equal(0, idle.InventoryReadCount);
        Assert.Equal(0, idle.SerializationCount);
        Assert.Equal(0, idle.JournalWriteCount);
        Assert.Equal(0, idle.SubmittedMutations);
        Assert.Equal(0, idle.CompletedMutations);

        Assert.Equal(10_000, active.ConnectedPlayers);
        Assert.Equal(10_000, active.RosterCount);
        Assert.Equal(100, active.InventoryReadCount);
        Assert.Equal(100, active.SerializationCount);
        Assert.Equal(100, active.JournalWriteCount);
        Assert.Equal(100, active.SubmittedMutations);
        Assert.Equal(100, active.CompletedMutations);
    }

    [Fact]
    public async Task Small_sqlite_workload_exercises_all_shapes_without_integrity_failures()
    {
        string database = TemporaryDatabasePath();
        try
        {
            JournalBenchmarkConfig config = JournalBenchmarkConfig.Parse(new[]
            {
                "--journal", "--operations", "70", "--players", "16", "--seed", "835",
                "--payload-bytes", "192", "--database", database,
            });

            JournalBenchmarkResult result = await JournalBenchmarkRunner.RunAsync(config);

            Assert.Equal(70, result.OperationsCompleted);
            Assert.True(result.Applied > 0);
            Assert.True(result.Replayed > 0);
            Assert.True(result.EventTailLength > 0);
            Assert.True(result.ProjectionBytes > 0);
            Assert.InRange(result.PeakReplayCandidates, 1, 1);
            Assert.Equal(0, result.PrunedEventCount);
            Assert.Equal(0, result.ChecksumFailures);
            Assert.Equal(0, result.DuplicateEffectFailures);
            Assert.Equal(0, result.SequenceFailures);
            Assert.Equal(0, result.PartialCommitFailures);
        }
        finally
        {
            DeleteDatabase(database);
        }
    }

    [Fact]
    public async Task Repeated_seeded_runs_in_one_retained_store_are_isolated()
    {
        string database = TemporaryDatabasePath();
        try
        {
            JournalBenchmarkConfig config = JournalBenchmarkConfig.Parse(new[]
            {
                "--journal", "--operations", "70", "--players", "16", "--seed", "835",
                "--database", database,
            });

            JournalBenchmarkResult first = await JournalBenchmarkRunner.RunAsync(config);
            JournalBenchmarkResult second = await JournalBenchmarkRunner.RunAsync(config);

            Assert.Equal(first.Applied, second.Applied);
            Assert.Equal(first.Replayed, second.Replayed);
            Assert.True(second.Applied > 0);
            Assert.Equal(0, second.DuplicateEffectFailures);
            Assert.Equal(0, second.PartialCommitFailures);
        }
        finally
        {
            DeleteDatabase(database);
        }
    }

    [Fact]
    public void Integrity_failures_produce_a_failing_process_exit_code()
    {
        var clean = new JournalBenchmarkResult();
        JournalBenchmarkResult corrupt = clean with { SequenceFailures = 1 };

        Assert.False(clean.HasIntegrityFailures);
        Assert.Equal(0, clean.ProcessExitCode);
        Assert.True(corrupt.HasIntegrityFailures);
        Assert.NotEqual(0, corrupt.ProcessExitCode);
    }

    [Fact]
    public async Task Soak_duration_starts_after_setup_and_emits_complete_progress_lines()
    {
        string database = TemporaryDatabasePath();
        try
        {
            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            JournalBenchmarkConfig config = SoakConfig(database, TimeSpan.FromTicks(1), TimeSpan.FromTicks(1));

            JournalBenchmarkResult result = await JournalSoakRunner.RunAsync(config, output);

            Assert.InRange(result.OperationsCompleted, 1, JournalBenchmarkConfig.MaximumOperations - 1);
            string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);
            Assert.All(lines, line =>
            {
                using JsonDocument json = JsonDocument.Parse(line);
                Assert.Equal("journal-soak-progress", json.RootElement.GetProperty("type").GetString());
                Assert.True(json.RootElement.GetProperty("operationsCompleted").GetInt32() > 0);
            });
        }
        finally
        {
            DeleteDatabase(database);
        }
    }

    [Fact]
    public async Task Soak_pre_cancelled_caller_does_not_start_workload()
    {
        string database = TemporaryDatabasePath();
        try
        {
            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            JournalBenchmarkResult result = await JournalSoakRunner.RunAsync(
                SoakConfig(database, TimeSpan.FromTicks(1), TimeSpan.FromTicks(1)),
                output,
                cancellation.Token);

            Assert.Equal(0, result.OperationsCompleted);
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            DeleteDatabase(database);
        }
    }

    [Fact]
    public async Task Soak_caller_cancellation_after_progress_finishes_current_operation()
    {
        string database = TemporaryDatabasePath();
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelOnFirstLineWriter(cancellation);

            JournalBenchmarkResult result = await JournalSoakRunner.RunAsync(
                SoakConfig(database, TimeSpan.FromMinutes(1), TimeSpan.FromTicks(1)),
                output,
                cancellation.Token);

            Assert.Equal(1, result.OperationsCompleted);
            Assert.Equal(1, result.Applied);
            string line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            using JsonDocument json = JsonDocument.Parse(line);
            Assert.Equal(1, json.RootElement.GetProperty("operationsCompleted").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("applied").GetInt64());
        }
        finally
        {
            DeleteDatabase(database);
        }
    }

    [Fact]
    public void Public_sqlite_api_exposes_no_crash_hook()
    {
        Assembly core = typeof(IMutationJournalStore).Assembly;
        Assembly sqlite = typeof(SqliteMutationJournalStore).Assembly;
        Assert.Null(core.GetExportedTypes().SingleOrDefault(type => type.Name == "JournalTestHookPhase"));
        Assert.Null(core.GetExportedTypes().SingleOrDefault(type => type.Name == "InMemoryJournalTestHook"));
        Assert.Null(sqlite.GetExportedTypes().SingleOrDefault(type => type.Name == "SqliteJournalTestHook"));
        Assert.DoesNotContain(
            typeof(SqliteMutationJournalStore).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            static constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType.Name.Contains("Hook", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("JournalTestHookPhase", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit-before-response")]
    public async Task Process_kill_recovery_has_exactly_one_result_and_version_range(string phase)
    {
        string database = TemporaryDatabasePath();
        Guid operationId = Guid.NewGuid();
        Process? child = null;
        try
        {
            child = StartCrashProbe(database, operationId, phase);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? checkpoint = await child.StandardOutput.ReadLineAsync(timeout.Token);
            string stderr = checkpoint is null ? await child.StandardError.ReadToEndAsync(timeout.Token) : string.Empty;
            Assert.True(checkpoint is not null, $"Crash probe exited before its checkpoint. {stderr}");
            Assert.Equal($"JOURNAL_CHECKPOINT {phase}", checkpoint);

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);

            await using RecoveredOperation recovered = await ReopenAndResolveAsync(database, operationId);
            Assert.Equal(JournalOperationResolutionStatus.Replayed, recovered.Resolution.Status);
            JournalCommitReceipt receipt = Assert.IsType<JournalCommitReceipt>(recovered.Resolution.Receipt);
            JournalStreamVersionRange range = Assert.Single(receipt.Streams);
            Assert.Equal("player/crash-probe", range.StreamKey);
            Assert.Equal(0, range.BeforeVersion);
            Assert.Equal(1, range.AfterVersion);
            Assert.Equal(1, range.EventCount);
            Assert.Equal(new byte[] { 79, 75 }, receipt.ResultData.ToArray());

            JournalEventPage page = await recovered.Store.ReadEventsAsync(
                new JournalEventRead("player/crash-probe", 0, 1, 10, 4096));
            Assert.Single(page.Events);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            child?.Dispose();
            DeleteDatabase(database);
        }
    }

    private static Process StartCrashProbe(string database, Guid operationId, string phase)
    {
        string benchmarkDll = typeof(JournalBenchmarkConfig).Assembly.Location;
        string runtimeConfig = Path.ChangeExtension(benchmarkDll, ".runtimeconfig.json");
        Assert.True(File.Exists(runtimeConfig), $"Benchmark runtime config is missing beside {benchmarkDll}.");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(benchmarkDll);
        start.ArgumentList.Add("--journal-crash-probe");
        start.ArgumentList.Add("--database");
        start.ArgumentList.Add(database);
        start.ArgumentList.Add("--operation-id");
        start.ArgumentList.Add(operationId.ToString("D"));
        start.ArgumentList.Add("--pause-at");
        start.ArgumentList.Add(phase);
        return Process.Start(start) ?? throw new InvalidOperationException("Crash probe process did not start.");
    }

    private static async Task<RecoveredOperation> ReopenAndResolveAsync(string database, Guid operationId)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            SqliteMutationJournalStore? store = null;
            try
            {
                store = new SqliteMutationJournalStore($"Data Source={database};Pooling=False");
                JournalOperationIdentity identity = JournalCrashProbe.CreateIdentity(operationId);
                JournalOperationResolution resolution = await store.ResolveOperationAsync(identity);
                if (resolution.Status == JournalOperationResolutionStatus.NotFound)
                {
                    JournalCommitResult retry = await store.CommitAsync(JournalCrashProbe.CreateCommit(operationId));
                    Assert.Equal(JournalCommitStatus.Applied, retry.Status);
                    resolution = await store.ResolveOperationAsync(identity);
                }
                return new RecoveredOperation(store, resolution);
            }
            catch (Exception exception) when (attempt < 19)
            {
                lastFailure = exception;
                store?.Dispose();
                await Task.Delay(50);
            }
        }
        throw new InvalidOperationException("SQLite file did not reopen after process termination.", lastFailure);
    }

    private static string TemporaryDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"khaoz-journal-benchmark-{Guid.NewGuid():N}.db");

    private static JournalBenchmarkConfig SoakConfig(string database, TimeSpan duration, TimeSpan progressInterval)
        => new()
        {
            Mode = JournalBenchmarkMode.Soak,
            Operations = 70,
            Players = 16,
            Seed = 835,
            PayloadBytes = 96,
            DatabasePath = database,
            Duration = duration,
            ProgressInterval = progressInterval,
        };

    private static void DeleteDatabase(string database)
    {
        foreach (string suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            string path = database + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class RecoveredOperation : IAsyncDisposable
    {
        internal RecoveredOperation(SqliteMutationJournalStore store, JournalOperationResolution resolution)
        {
            Store = store;
            Resolution = resolution;
        }

        internal SqliteMutationJournalStore Store { get; }
        internal JournalOperationResolution Resolution { get; }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelOnFirstLineWriter : StringWriter
    {
        private readonly CancellationTokenSource cancellation;
        private int lineCount;

        internal CancelOnFirstLineWriter(CancellationTokenSource cancellation)
            : base(System.Globalization.CultureInfo.InvariantCulture)
            => this.cancellation = cancellation;

        public override void WriteLine(string? value)
        {
            if (lineCount++ != 0) throw new InvalidOperationException("Soak continued after caller cancellation.");
            base.WriteLine(value);
            cancellation.Cancel();
        }
    }
}
