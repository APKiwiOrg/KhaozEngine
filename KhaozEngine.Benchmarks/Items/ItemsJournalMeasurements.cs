using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Benchmarks.Journal;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct CraftBatchMeasurement(
    int CoalescedCommits,
    int CoalescedOwnedBytes,
    int UncoalescedCommits,
    int UncoalescedOwnedBytes,
    int CraftEventBytes,
    string StoreStatus);

internal readonly record struct ThroughputMeasurement(
    double OfferedPerSecond,
    double AcceptedPerSecond,
    double CommittedPerSecond,
    double P50Milliseconds,
    double P99Milliseconds,
    double BackpressureRate,
    double BusyRate,
    double VersionConflictRate,
    long ReplayCount,
    long FailureCount,
    int CommitBytes);

/// <summary>Budgets 4 and 13: the two that go through the real journal.</summary>
internal static class ItemsJournalMeasurements
{
    private const double TileTickSeconds = 0.25;

    /// <summary>
    /// Budget 4: N crafts on one item inside one held action, coalesced into ONE commit carrying one
    /// identity, one event per craft and one projection write for the page, against the same work as
    /// N separate commits.
    /// </summary>
    internal static async Task<CraftBatchMeasurement> MeasureCraftBatchAsync(
        ItemsJournalScope scope,
        int crafts,
        int seed,
        int contentVersion,
        CancellationToken cancellationToken)
    {
        string stream = $"items/{seed}/craft/player000001";
        await ItemsCommitFactory.InitializeAsync(scope.Store, stream, seed, 10).ConfigureAwait(false);
        var executor = new MutationJournalExecutor(scope.Store, new JournalExecutorOptions(2, 64, 64L * 1024 * 1024));
        try
        {
            byte[] page = CanonicalRare.BuildPage(0, contentVersion);
            byte[] intent = ItemsCommitFactory.CraftIntent(4_210, 2, 7, 12, (ulong)CanonicalRare.InstanceId);
            byte[] result = CanonicalRare.BuildPayload();
            var events = new JournalEvent[crafts];
            int eventBytes = 0;
            for (int craft = 0; craft < crafts; craft++)
            {
                byte[] before = CanonicalRare.BuildPayload(craft);
                byte[] after = CanonicalRare.BuildPayload(craft + 1);
                byte[] payload = ItemsCommitFactory.CraftEvent(4_210, (ulong)CanonicalRare.InstanceId, contentVersion, before, after);
                eventBytes = payload.Length;
                events[craft] = new JournalEvent(ItemsCommitFactory.CraftEventType, 1, payload);
            }

            long version = await HeadVersionAsync(scope.Store, stream, cancellationToken).ConfigureAwait(false);
            JournalCommit coalesced = ItemsCommitFactory.Build(
                ItemsCommitFactory.OperationId(seed, "craft-batch", 0),
                stream,
                version,
                intent,
                events,
                new[] { new JournalProjectionWrite(stream, ContainerPageCodec.SectionName("bank", 0), ItemsCommitFactory.PageSchema, 1, page) },
                result);
            int coalescedBytes = coalesced.OwnedByteCount;
            JournalCommitStatus status = await SubmitAndWaitAsync(executor, coalesced, cancellationToken).ConfigureAwait(false);
            version += crafts;

            int uncoalescedBytes = 0;
            for (int craft = 0; craft < crafts; craft++)
            {
                JournalCommit single = ItemsCommitFactory.Build(
                    ItemsCommitFactory.OperationId(seed, "craft-single", craft),
                    stream,
                    version,
                    intent,
                    new[] { events[craft] },
                    new[] { new JournalProjectionWrite(stream, ContainerPageCodec.SectionName("bank", 0), ItemsCommitFactory.PageSchema, 1, page) },
                    result);
                uncoalescedBytes += single.OwnedByteCount;
                JournalCommitStatus singleStatus = await SubmitAndWaitAsync(executor, single, cancellationToken).ConfigureAwait(false);
                if (singleStatus != JournalCommitStatus.Applied) status = singleStatus;
                version++;
            }

            return new CraftBatchMeasurement(1, coalescedBytes, crafts, uncoalescedBytes, eventBytes, status.ToString());
        }
        finally
        {
            await executor.StopAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Budget 13: the steady state rather than the burst. Every player offers one commit per tile tick,
    /// so 1,000 players at 250 ms offer 4,000 per second BEFORE coalescing, and what the store sustains
    /// is the number to find. A refused submission is DROPPED rather than retried, which is what 16 says
    /// a consumer must do on <c>Backpressure</c>.
    /// </summary>
    internal static async Task<ThroughputMeasurement> MeasureThroughputAsync(
        ItemsJournalScope scope,
        int players,
        int seconds,
        int seed,
        byte[] page,
        int contentVersion,
        CancellationToken cancellationToken)
    {
        var streams = new string[players];
        var versions = new long[players];
        for (int player = 0; player < players; player++)
        {
            streams[player] = $"items/{seed}/load/player{player:D6}";
            await ItemsCommitFactory.InitializeAsync(scope.Store, streams[player], seed, 10).ConfigureAwait(false);
            versions[player] = await HeadVersionAsync(scope.Store, streams[player], cancellationToken).ConfigureAwait(false);
        }

        int capacity = Math.Min(players * 8, 8_192);
        var executor = new MutationJournalExecutor(
            scope.Store,
            new JournalExecutorOptions(
                Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                capacity,
                capacity * 8_192L));
        var samples = new JournalLatencySamples(seed);
        var submittedAt = new Dictionary<Guid, long>(capacity);
        byte[] intent = ItemsCommitFactory.CraftIntent(4_210, 2, 7, 12, (ulong)CanonicalRare.InstanceId);
        byte[] result = CanonicalRare.BuildPayload();
        byte[] eventPayload = ItemsCommitFactory.CraftEvent(
            4_210,
            (ulong)CanonicalRare.InstanceId,
            contentVersion,
            CanonicalRare.BuildPayload(1),
            CanonicalRare.BuildPayload(2));

        long offered = 0;
        long accepted = 0;
        long committed = 0;
        long backpressure = 0;
        long busy = 0;
        long versionConflict = 0;
        long replayed = 0;
        long failures = 0;
        int commitBytes = 0;
        double ticksToMilliseconds = 1_000.0 / Stopwatch.Frequency;
        var timer = Stopwatch.StartNew();
        long ordinal = 0;

        try
        {
            TimeSpan duration = TimeSpan.FromSeconds(seconds);
            int tick = 0;
            while (timer.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan tickEnd = TimeSpan.FromSeconds((tick + 1) * TileTickSeconds);
                for (int player = 0; player < players; player++)
                {
                    JournalCommit commit = ItemsCommitFactory.Build(
                        ItemsCommitFactory.OperationId(seed, "page-commit", ordinal++),
                        streams[player],
                        versions[player],
                        intent,
                        new[] { new JournalEvent(ItemsCommitFactory.CraftEventType, 1, eventPayload) },
                        new[]
                        {
                            new JournalProjectionWrite(
                                streams[player],
                                ContainerPageCodec.SectionName("bank", player % 10),
                                ItemsCommitFactory.PageSchema,
                                1,
                                page),
                        },
                        result);
                    commitBytes = commit.OwnedByteCount;
                    offered++;
                    JournalSubmission submission = executor.Submit(commit);
                    switch (submission.Status)
                    {
                        case JournalSubmissionStatus.Accepted:
                            accepted++;
                            versions[player]++;
                            submittedAt[submission.OperationId] = Stopwatch.GetTimestamp();
                            break;
                        case JournalSubmissionStatus.Backpressure:
                            backpressure++;
                            break;
                        case JournalSubmissionStatus.StreamBusy:
                            busy++;
                            break;
                        case JournalSubmissionStatus.VersionConflict:
                            versionConflict++;
                            if (executor.TryGetAdmittedStream(streams[player], out JournalAdmittedStream? admitted))
                                versions[player] = admitted.AdmittedHeadVersion;
                            break;
                        default:
                            break;
                    }

                    Drain(executor, submittedAt, samples, ticksToMilliseconds, ref committed, ref replayed, ref failures);
                }

                while (timer.Elapsed < tickEnd && timer.Elapsed < duration)
                {
                    if (!Drain(executor, submittedAt, samples, ticksToMilliseconds, ref committed, ref replayed, ref failures))
                        Thread.Sleep(1);
                }

                tick++;
            }
        }
        finally
        {
            await executor.StopAsync(TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            Drain(executor, submittedAt, samples, ticksToMilliseconds, ref committed, ref replayed, ref failures);
        }

        timer.Stop();
        double elapsedSeconds = Math.Max(timer.Elapsed.TotalSeconds, 0.000_001);
        return new ThroughputMeasurement(
            offered / elapsedSeconds,
            accepted / elapsedSeconds,
            committed / elapsedSeconds,
            samples.Percentile(0.50),
            samples.Percentile(0.99),
            offered == 0 ? 0 : (double)backpressure / offered,
            offered == 0 ? 0 : (double)busy / offered,
            offered == 0 ? 0 : (double)versionConflict / offered,
            replayed,
            failures,
            commitBytes);
    }

    private static bool Drain(
        MutationJournalExecutor executor,
        Dictionary<Guid, long> submittedAt,
        JournalLatencySamples samples,
        double ticksToMilliseconds,
        ref long committed,
        ref long replayed,
        ref long failures)
    {
        bool drained = false;
        while (executor.TryDequeueCompletion(out JournalCompletion? completion))
        {
            drained = true;
            Guid operationId = completion.Commit.Identity.OperationId;
            if (submittedAt.Remove(operationId, out long started))
                samples.Add((Stopwatch.GetTimestamp() - started) * ticksToMilliseconds);
            if (completion.Failure is not null) failures++;
            else if (completion.Result?.Status == JournalCommitStatus.Applied) committed++;
            else if (completion.Result?.Status == JournalCommitStatus.Replayed) replayed++;
            else failures++;
            executor.AcknowledgeCompletion(operationId, JournalCompletionAcknowledgement.Handled);
        }

        return drained;
    }

    private static async Task<JournalCommitStatus> SubmitAndWaitAsync(
        MutationJournalExecutor executor,
        JournalCommit commit,
        CancellationToken cancellationToken)
    {
        JournalSubmission submission = executor.Submit(commit);
        if (!submission.IsAccepted) throw new InvalidOperationException($"Executor refused a benchmark commit with {submission.Status}.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (executor.TryDequeueCompletion(out JournalCompletion? completion))
            {
                executor.AcknowledgeCompletion(completion.Commit.Identity.OperationId, JournalCompletionAcknowledgement.Handled);
                if (completion.Failure is not null) throw completion.Failure;
                return completion.Result?.Status ?? JournalCommitStatus.Applied;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> HeadVersionAsync(IMutationJournalStore store, string stream, CancellationToken cancellationToken)
    {
        JournalProjectionRead read = await store.ReadProjectionsAsync(new JournalProjectionQuery(stream), cancellationToken).ConfigureAwait(false);
        return read.HeadVersion;
    }
}
