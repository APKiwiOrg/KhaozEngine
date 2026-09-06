using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.Benchmarks.Journal;

public sealed record JournalScalingProbe(
    int ConnectedPlayers,
    int SerializationCount,
    int JournalWriteCount,
    int SubmittedMutations,
    int CompletedMutations);

public sealed record JournalBenchmarkProgress(int OperationsCompleted, long Applied, long Replayed, double ElapsedSeconds);

public static class JournalBenchmarkRunner
{
    public static IReadOnlyList<JournalWorkloadStep> GenerateWorkload(JournalBenchmarkConfig config)
        => JournalWorkload.Generate(config);

    public static async Task<JournalScalingProbe> RunScalingProbeAsync(
        int connectedPlayers,
        int activePlayers,
        int seed,
        CancellationToken cancellationToken = default)
    {
        if (connectedPlayers < 1) throw new ArgumentOutOfRangeException(nameof(connectedPlayers));
        if (activePlayers < 0 || activePlayers > connectedPlayers) throw new ArgumentOutOfRangeException(nameof(activePlayers));
        if (activePlayers == 0) return new JournalScalingProbe(connectedPlayers, 0, 0, 0, 0);

        var store = new InMemoryMutationJournalStore();
        var executor = new MutationJournalExecutor(
            store,
            new JournalExecutorOptions(1, Math.Max(activePlayers, 1), Math.Max(activePlayers * 2_048L, 4_096)));
        int serializations = 0;
        int writes = 0;
        int completed = 0;
        try
        {
            for (int player = 0; player < activePlayers; player++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stream = ScalingPlayerStream(player);
                await InitializeAsync(store, stream, seed).ConfigureAwait(false);
                byte[] payload = Encoding.ASCII.GetBytes($"item:{seed}:{player}");
                serializations++;
                var commit = new JournalCommit(
                    Identity(DeterministicGuid(seed, player, "scale"), "inventory.change", payload),
                    new[] { new JournalStreamMutation(stream, 0, new[] { new JournalEvent("inventory.changed", 1, payload) }) },
                    new[] { new JournalProjectionWrite(stream, "bag", "bag.v1", 1, payload) },
                    "mutation.result.v1",
                    1,
                    new byte[] { 1 });
                writes++;
                JournalCompletion completion = await SubmitAndWaitAsync(executor, commit, cancellationToken).ConfigureAwait(false);
                if (completion.Result?.Status != JournalCommitStatus.Applied)
                    throw completion.Failure ?? new InvalidOperationException("Scaling probe mutation did not apply.");
                executor.AcknowledgeCompletion(commit.Identity.OperationId, JournalCompletionAcknowledgement.Handled);
                completed++;
            }
        }
        finally
        {
            await executor.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }

        return new JournalScalingProbe(connectedPlayers, serializations, writes, activePlayers, completed);
    }

    public static Task<JournalBenchmarkResult> RunAsync(
        JournalBenchmarkConfig config,
        CancellationToken cancellationToken = default)
        => RunAsync(config, null, cancellationToken);

    internal static async Task<JournalBenchmarkResult> RunAsync(
        JournalBenchmarkConfig config,
        Action<JournalBenchmarkProgress>? progress,
        CancellationToken cancellationToken)
    {
        config.Validate();
        using JournalStoreScope scope = JournalStoreScope.Create(config);
        var executor = new MutationJournalExecutor(
            scope.Store,
            new JournalExecutorOptions(
                config.Workers,
                Math.Max(config.Workers * 4, 16),
                Math.Max(config.Workers * config.PayloadBytes * 32L, 1_048_576)));
        var state = new RunState(config, scope, executor);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        TimeSpan nextProgress = config.ProgressInterval;

        try
        {
            foreach (JournalWorkloadStep step in JournalWorkload.Stream(config))
            {
                if (cancellationToken.IsCancellationRequested) break;
                await ExecuteStepAsync(state, step, CancellationToken.None).ConfigureAwait(false);
                state.OperationsCompleted++;
                if (progress is not null && timer.Elapsed >= nextProgress)
                {
                    progress(new JournalBenchmarkProgress(state.OperationsCompleted, state.Applied, state.Replayed, timer.Elapsed.TotalSeconds));
                    nextProgress += config.ProgressInterval;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await executor.StopAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        }

        timer.Stop();
        await InspectStoredStateAsync(state, CancellationToken.None).ConfigureAwait(false);
        long allocated = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        return state.ToResult(timer.Elapsed, allocated);
    }

    private static async Task ExecuteStepAsync(RunState state, JournalWorkloadStep step, CancellationToken cancellationToken)
    {
        switch (step.Kind)
        {
            case JournalWorkloadKind.InventoryChange:
            case JournalWorkloadKind.BankTransfer:
            case JournalWorkloadKind.Trade:
                JournalCommit commit = await BuildCommitAsync(state, step, cancellationToken).ConfigureAwait(false);
                state.Commits.Clear();
                state.Commits[step.OperationId] = commit;
                state.PeakReplayCandidates = Math.Max(state.PeakReplayCandidates, state.Commits.Count);
                state.SerializationCount++;
                await SubmitMutationAsync(state, commit, expectReplay: false, cancellationToken).ConfigureAwait(false);
                break;
            case JournalWorkloadKind.OperationReplay:
                if (!state.Commits.TryGetValue(step.OperationId, out JournalCommit? replay))
                    throw new InvalidOperationException($"Replay source '{step.OperationId}' was not generated.");
                await SubmitMutationAsync(state, replay, expectReplay: true, cancellationToken).ConfigureAwait(false);
                break;
            case JournalWorkloadKind.ProjectionRead:
                await ReadProjectionAsync(state, state.PlayerStream(step.PrimaryPlayer), cancellationToken).ConfigureAwait(false);
                break;
            case JournalWorkloadKind.Snapshot:
                await ReadSnapshotAndTailAsync(state, state.PlayerStream(step.PrimaryPlayer), cancellationToken).ConfigureAwait(false);
                break;
            case JournalWorkloadKind.Compaction:
                await CompactAsync(state, step, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    private static async Task<JournalCommit> BuildCommitAsync(RunState state, JournalWorkloadStep step, CancellationToken cancellationToken)
    {
        string player = state.PlayerStream(step.PrimaryPlayer);
        await state.EnsureStreamAsync(player, cancellationToken).ConfigureAwait(false);
        byte[] payload = step.Payload;
        string action;
        var streams = new List<JournalStreamMutation>();
        var projections = new List<JournalProjectionWrite>();

        if (step.Kind == JournalWorkloadKind.InventoryChange)
        {
            action = "inventory.change";
            AddMutation(state, streams, projections, player, "inventory.changed", "bag", payload);
        }
        else if (step.Kind == JournalWorkloadKind.BankTransfer)
        {
            action = "bank.transfer";
            string bank = state.BankStream(step.PrimaryPlayer);
            await state.EnsureStreamAsync(bank, cancellationToken).ConfigureAwait(false);
            AddMutation(state, streams, projections, player, "inventory.removed", "bag", payload);
            AddMutation(state, streams, projections, bank, "bank.added", "bank", payload);
        }
        else
        {
            action = "trade.atomic";
            string recipient = state.PlayerStream(step.SecondaryPlayer);
            await state.EnsureStreamAsync(recipient, cancellationToken).ConfigureAwait(false);
            AddMutation(state, streams, projections, player, "trade.item.removed", "bag", payload);
            AddMutation(state, streams, projections, recipient, "trade.item.added", "bag", payload);
        }

        return new JournalCommit(
            Identity(state.OperationId(step.OperationId), action, payload),
            streams,
            projections,
            "mutation.result.v1",
            1,
            ResultPayload(step));
    }

    private static async Task SubmitMutationAsync(
        RunState state,
        JournalCommit commit,
        bool expectReplay,
        CancellationToken cancellationToken)
    {
        long[] before = commit.StreamMutations.Select(stream => state.Versions[stream.StreamKey]).ToArray();
        var timer = Stopwatch.StartNew();
        JournalSubmission submission = state.Executor.Submit(commit);
        state.MutationSubmissions++;
        state.JournalWriteCount++;
        if (submission.Status == JournalSubmissionStatus.StreamBusy) state.Busy++;
        if (submission.Status == JournalSubmissionStatus.Backpressure) state.Backpressure++;
        if (!submission.IsAccepted)
            throw new InvalidOperationException($"Benchmark executor rejected a serialized operation with {submission.Status}.");

        JournalCompletion completion = await SubmitAndWaitAsync(state.Executor, commit, cancellationToken, alreadySubmitted: true).ConfigureAwait(false);
        timer.Stop();
        state.Latencies.Add(timer.Elapsed.TotalMilliseconds);
        if (completion.Failure is not null)
            throw completion.Failure;
        JournalCommitResult result = completion.Result ?? throw new InvalidOperationException("Journal completion carried no result.");
        JournalCommitReceipt receipt = result.Receipt ?? throw new InvalidOperationException($"Journal completion status was {result.Status}.");
        if (!receipt.HasValidResultChecksum) state.ChecksumFailures++;

        if (result.Status == JournalCommitStatus.Applied)
        {
            state.Applied++;
            ValidateAppliedRanges(state, commit, receipt);
            foreach (JournalStreamVersionRange range in receipt.Streams) state.Versions[range.StreamKey] = range.AfterVersion;
        }
        else if (result.Status == JournalCommitStatus.Replayed)
        {
            state.Replayed++;
            if (!expectReplay) state.DuplicateEffectFailures++;
            for (int i = 0; i < commit.StreamMutations.Count; i++)
                if (state.Versions[commit.StreamMutations[i].StreamKey] != before[i]) state.DuplicateEffectFailures++;
        }
        else
        {
            state.PartialCommitFailures++;
        }

        if (expectReplay && result.Status != JournalCommitStatus.Replayed) state.DuplicateEffectFailures++;
        state.Executor.AcknowledgeCompletion(commit.Identity.OperationId, JournalCompletionAcknowledgement.Handled);
    }

    private static async Task<JournalCompletion> SubmitAndWaitAsync(
        MutationJournalExecutor executor,
        JournalCommit commit,
        CancellationToken cancellationToken,
        bool alreadySubmitted = false)
    {
        if (!alreadySubmitted)
        {
            JournalSubmission submission = executor.Submit(commit);
            if (!submission.IsAccepted) throw new InvalidOperationException($"Executor submission failed with {submission.Status}.");
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (executor.TryDequeueCompletion(out JournalCompletion? completion)) return completion;
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateAppliedRanges(RunState state, JournalCommit commit, JournalCommitReceipt receipt)
    {
        if (receipt.Streams.Count != commit.StreamMutations.Count)
        {
            state.PartialCommitFailures++;
            return;
        }
        foreach (JournalStreamVersionRange range in receipt.Streams)
        {
            JournalStreamMutation? mutation = commit.StreamMutations.FirstOrDefault(value => value.StreamKey == range.StreamKey);
            if (mutation is null
                || range.BeforeVersion != mutation.ExpectedVersion
                || range.AfterVersion != mutation.ExpectedVersion + mutation.Events.Count
                || range.EventCount != mutation.Events.Count)
                state.PartialCommitFailures++;
        }
    }

    private static void AddMutation(
        RunState state,
        ICollection<JournalStreamMutation> streams,
        ICollection<JournalProjectionWrite> projections,
        string stream,
        string eventType,
        string section,
        byte[] payload)
    {
        streams.Add(new JournalStreamMutation(
            stream,
            state.Versions[stream],
            new[] { new JournalEvent(eventType, 1, payload) }));
        projections.Add(new JournalProjectionWrite(stream, section, $"{section}.v1", 1, payload));
    }

    private static async Task ReadProjectionAsync(RunState state, string stream, CancellationToken cancellationToken)
    {
        await state.EnsureStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        JournalProjectionRead read = await state.Scope.Store.ReadProjectionsAsync(
            new JournalProjectionQuery(stream), cancellationToken).ConfigureAwait(false);
        timer.Stop();
        state.Executor.Metrics.RecordProjectionRead(timer.Elapsed, read.Sections.Count);
        foreach (JournalProjectionSection section in read.Sections)
            if (!section.HasValidChecksum) state.ChecksumFailures++;
    }

    private static async Task ReadSnapshotAndTailAsync(RunState state, string stream, CancellationToken cancellationToken)
    {
        await state.EnsureStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        JournalSnapshot? snapshot = await state.Scope.Store.LoadSnapshotAsync(stream, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && !snapshot.HasValidChecksum) state.ChecksumFailures++;
        long after = snapshot?.ThroughVersion ?? 0;
        await InspectTailAsync(state, stream, after, state.Versions[stream], cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompactAsync(RunState state, JournalWorkloadStep step, CancellationToken cancellationToken)
    {
        string stream = state.PlayerStream(step.PrimaryPlayer);
        await state.EnsureStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        long head = state.Versions[stream];
        long? prune = head > 1 ? head - 1 : null;
        JournalCompactionResult result = await state.Scope.Store.CompactAsync(
            new JournalCompaction(stream, head, "player.snapshot.v1", 1, step.Payload, prune),
            cancellationToken).ConfigureAwait(false);
        if (result.Status != JournalCompactionStatus.Compacted) state.PartialCommitFailures++;
    }

    private static async Task InspectStoredStateAsync(RunState state, CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, long> item in state.Versions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            JournalSnapshot? snapshot = await state.Scope.Store.LoadSnapshotAsync(item.Key, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && !snapshot.HasValidChecksum) state.ChecksumFailures++;
            long after = snapshot?.ThroughVersion ?? 0;
            state.CompactionLagVersions = Math.Max(state.CompactionLagVersions, item.Value - after);
            state.EventTailLength += await InspectTailAsync(state, item.Key, after, item.Value, cancellationToken).ConfigureAwait(false);
            JournalProjectionRead projections = await state.Scope.Store.ReadProjectionsAsync(
                new JournalProjectionQuery(item.Key), cancellationToken).ConfigureAwait(false);
            state.ProjectionBytes += projections.ReturnedBytes;
            foreach (JournalProjectionSection section in projections.Sections)
                if (!section.HasValidChecksum) state.ChecksumFailures++;
        }
    }

    private static async Task<long> InspectTailAsync(
        RunState state,
        string stream,
        long afterVersion,
        long throughVersion,
        CancellationToken cancellationToken)
    {
        long count = 0;
        long cursor = afterVersion;
        while (cursor < throughVersion)
        {
            JournalEventPage page = await state.Scope.Store.ReadEventsAsync(
                new JournalEventRead(stream, cursor, throughVersion, 2_048, 8 * 1024 * 1024),
                cancellationToken).ConfigureAwait(false);
            long expected = cursor + 1;
            foreach (JournalStoredEvent journalEvent in page.Events)
            {
                if (journalEvent.StreamVersion != expected++) state.SequenceFailures++;
                if (!journalEvent.HasValidChecksum) state.ChecksumFailures++;
            }
            count += page.Events.Count;
            if (page.Events.Count == 0)
            {
                if (!page.ReachedThroughVersion) state.SequenceFailures++;
                break;
            }
            cursor = page.Events[^1].StreamVersion;
        }
        return count;
    }

    private static async Task InitializeAsync(IMutationJournalStore store, string stream, int seed)
    {
        byte[] intent = Encoding.ASCII.GetBytes(stream);
        JournalInitializeResult initialized = await store.InitializeAsync(new JournalInitialization(
            Identity(DeterministicGuid(seed, stream, "initialize"), "benchmark.initialize", intent),
            stream,
            "player.snapshot.v1",
            1,
            new byte[] { 1 },
            new[] { new JournalProjectionWrite(stream, "bag", "bag.v1", 1, new byte[] { 1 }) },
            "initialize.result.v1",
            1,
            new byte[] { 1 })).ConfigureAwait(false);
        if (initialized.Status is not (JournalInitializeStatus.Initialized or JournalInitializeStatus.Replayed or JournalInitializeStatus.ExistingStream))
            throw new InvalidOperationException($"Stream initialization failed with {initialized.Status}.");
    }

    private static JournalOperationIdentity Identity(Guid operationId, string action, byte[] intent)
        => new(operationId, "benchmark/world", action, intent);

    private static Guid DeterministicGuid(int seed, int value, string domain)
        => DeterministicGuid(seed, value.ToString(System.Globalization.CultureInfo.InvariantCulture), domain);

    private static Guid DeterministicGuid(int seed, string value, string domain)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes($"{seed}:{domain}:{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static byte[] ResultPayload(JournalWorkloadStep step)
        => Encoding.ASCII.GetBytes($"ok:{step.Index}:{(int)step.Kind}");

    private static string ScalingPlayerStream(int player) => $"player/{player:D6}";

    private sealed class RunState
    {
        internal RunState(JournalBenchmarkConfig config, JournalStoreScope scope, MutationJournalExecutor executor)
        {
            Config = config;
            Scope = scope;
            Executor = executor;
            Latencies = new JournalLatencySamples(config.Seed);
            RunNamespace = Guid.NewGuid();
        }

        internal JournalBenchmarkConfig Config { get; }
        internal JournalStoreScope Scope { get; }
        internal MutationJournalExecutor Executor { get; }
        internal Dictionary<string, long> Versions { get; } = new(StringComparer.Ordinal);
        internal Dictionary<Guid, JournalCommit> Commits { get; } = new();
        internal JournalLatencySamples Latencies { get; }
        internal Guid RunNamespace { get; }
        internal int OperationsCompleted { get; set; }
        internal long MutationSubmissions { get; set; }
        internal long Applied { get; set; }
        internal long Replayed { get; set; }
        internal long Busy { get; set; }
        internal long Backpressure { get; set; }
        internal long SerializationCount { get; set; }
        internal long JournalWriteCount { get; set; }
        internal long EventTailLength { get; set; }
        internal long ProjectionBytes { get; set; }
        internal long CompactionLagVersions { get; set; }
        internal long ChecksumFailures { get; set; }
        internal long DuplicateEffectFailures { get; set; }
        internal long SequenceFailures { get; set; }
        internal long PartialCommitFailures { get; set; }
        internal int PeakReplayCandidates { get; set; }

        internal string PlayerStream(int player) => $"benchmark/{RunNamespace:N}/player/{player:D6}";
        internal string BankStream(int player) => $"benchmark/{RunNamespace:N}/bank/{player:D6}";

        internal Guid OperationId(Guid logicalOperationId)
        {
            byte[] source = Encoding.ASCII.GetBytes($"{RunNamespace:N}:{logicalOperationId:D}");
            byte[] digest = System.Security.Cryptography.SHA256.HashData(source);
            return new Guid(digest.AsSpan(0, 16));
        }

        internal async Task EnsureStreamAsync(string stream, CancellationToken cancellationToken)
        {
            if (Versions.ContainsKey(stream)) return;
            await InitializeAsync(Scope.Store, stream, Config.Seed).ConfigureAwait(false);
            JournalProjectionRead read = await Scope.Store.ReadProjectionsAsync(
                new JournalProjectionQuery(stream), cancellationToken).ConfigureAwait(false);
            Versions.Add(stream, read.HeadVersion);
        }

        internal JournalBenchmarkResult ToResult(TimeSpan elapsed, long allocatedBytes)
        {
            long retries = Enum.GetValues<JournalStoreFailureKind>().Sum(Executor.Metrics.GetRetryCount);
            double seconds = Math.Max(elapsed.TotalSeconds, 0.000_001);
            return new JournalBenchmarkResult
            {
                Mode = Config.Mode == JournalBenchmarkMode.Soak ? "soak" : "benchmark",
                Provider = Scope.Provider,
                Machine = Environment.MachineName,
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                Seed = Config.Seed,
                Players = Config.Players,
                OperationsRequested = Config.Operations,
                OperationsCompleted = OperationsCompleted,
                MutationSubmissions = MutationSubmissions,
                Applied = Applied,
                Replayed = Replayed,
                ThroughputPerSecond = MutationSubmissions / seconds,
                P50Milliseconds = Latencies.Percentile(0.50),
                P95Milliseconds = Latencies.Percentile(0.95),
                P99Milliseconds = Latencies.Percentile(0.99),
                ReplayRate = Ratio(Replayed, MutationSubmissions),
                RetryRate = Ratio(retries, MutationSubmissions),
                BusyRate = Ratio(Busy, MutationSubmissions),
                BackpressureRate = Ratio(Backpressure, MutationSubmissions),
                DatabaseBytes = JournalStoreScope.DatabaseBytes(Scope.DatabasePath),
                EventTailLength = EventTailLength,
                ProjectionBytes = ProjectionBytes,
                CompactionLagVersions = CompactionLagVersions,
                SerializationCount = SerializationCount,
                JournalWriteCount = JournalWriteCount,
                PeakReplayCandidates = PeakReplayCandidates,
                AllocationBytesPerOperation = OperationsCompleted == 0 ? 0 : (double)allocatedBytes / OperationsCompleted,
                ChecksumFailures = ChecksumFailures,
                DuplicateEffectFailures = DuplicateEffectFailures,
                SequenceFailures = SequenceFailures,
                PartialCommitFailures = PartialCommitFailures,
            };
        }

        private static double Ratio(long numerator, long denominator)
            => denominator == 0 ? 0 : (double)numerator / denominator;
    }
}
