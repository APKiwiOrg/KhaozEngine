using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace KhaozEngine.WorldStore.Journal;

public sealed class MutationJournalExecutorMetrics
{
    private static readonly double[] CommitLatencyBucketMilliseconds = { 1, 5, 10, 25, 50, 100, 250, 1_000 };
    private readonly TimeProvider timeProvider;
    private readonly long[] retries = new long[Enum.GetValues<JournalStoreFailureKind>().Length];
    private readonly long[] commitLatencyBuckets = new long[CommitLatencyBucketMilliseconds.Length + 1];
    private long accepted;
    private long streamBusy;
    private long backpressure;
    private long stopping;
    private long applied;
    private long replayed;
    private long versionConflict;
    private long operationConflict;
    private long failed;
    private long quarantined;
    private long queueOperations;
    private long queueOwnedBytes;
    private long oldestAdmittedUtcTicks;
    private long unacknowledgedCompletions;
    private long reservedStreams;
    private long replayedEvents;
    private long tailBytes;
    private long compactionLagTicks;
    private long projectionLatencyTicks;
    private long projectionReadCount;
    private long returnedProjectionSections;

    internal MutationJournalExecutorMetrics(TimeProvider timeProvider)
        => this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public long Accepted => Interlocked.Read(ref accepted);
    public long StreamBusy => Interlocked.Read(ref streamBusy);
    public long Backpressure => Interlocked.Read(ref backpressure);
    public long Stopping => Interlocked.Read(ref stopping);
    public long Applied => Interlocked.Read(ref applied);
    public long Replayed => Interlocked.Read(ref replayed);
    public long VersionConflict => Interlocked.Read(ref versionConflict);
    public long OperationConflict => Interlocked.Read(ref operationConflict);
    public long Failed => Interlocked.Read(ref failed);
    public long Quarantined => Interlocked.Read(ref quarantined);
    public long QueueOperations => Interlocked.Read(ref queueOperations);
    public long QueueOwnedBytes => Interlocked.Read(ref queueOwnedBytes);
    public long UnacknowledgedCompletions => Interlocked.Read(ref unacknowledgedCompletions);
    public long ReservedStreams => Interlocked.Read(ref reservedStreams);
    public long ReplayedEvents => Interlocked.Read(ref replayedEvents);
    public long TailBytes => Interlocked.Read(ref tailBytes);
    public TimeSpan CompactionLag => TimeSpan.FromTicks(Interlocked.Read(ref compactionLagTicks));
    public long ReturnedProjectionSections => Interlocked.Read(ref returnedProjectionSections);
    public TimeSpan ProjectionLatencyTotal => TimeSpan.FromTicks(Interlocked.Read(ref projectionLatencyTicks));
    public long ProjectionReadCount => Interlocked.Read(ref projectionReadCount);

    public TimeSpan OldestPendingAge
    {
        get
        {
            long ticks = Interlocked.Read(ref oldestAdmittedUtcTicks);
            if (ticks == 0) return TimeSpan.Zero;
            long age = timeProvider.GetUtcNow().UtcTicks - ticks;
            return age <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(age);
        }
    }

    public long GetRetryCount(JournalStoreFailureKind kind) => Interlocked.Read(ref retries[(int)kind]);

    public JournalCommitLatencyHistogram GetCommitLatencyHistogram()
    {
        var counts = new long[commitLatencyBuckets.Length];
        for (int i = 0; i < counts.Length; i++) counts[i] = Interlocked.Read(ref commitLatencyBuckets[i]);
        return new JournalCommitLatencyHistogram(CommitLatencyBucketMilliseconds, counts);
    }

    public void RecordLoad(long loadedReplayedEvents, long loadedTailBytes, TimeSpan observedCompactionLag)
    {
        if (loadedReplayedEvents < 0) throw new ArgumentOutOfRangeException(nameof(loadedReplayedEvents));
        if (loadedTailBytes < 0) throw new ArgumentOutOfRangeException(nameof(loadedTailBytes));
        if (observedCompactionLag < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(observedCompactionLag));
        Interlocked.Add(ref replayedEvents, loadedReplayedEvents);
        Interlocked.Add(ref tailBytes, loadedTailBytes);
        Interlocked.Exchange(ref compactionLagTicks, observedCompactionLag.Ticks);
    }

    public void RecordProjectionRead(TimeSpan latency, int returnedSections)
    {
        if (latency < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(latency));
        if (returnedSections < 0) throw new ArgumentOutOfRangeException(nameof(returnedSections));
        Interlocked.Add(ref projectionLatencyTicks, latency.Ticks);
        Interlocked.Increment(ref projectionReadCount);
        Interlocked.Add(ref returnedProjectionSections, returnedSections);
    }

    internal void RecordSubmission(JournalSubmissionStatus status)
    {
        switch (status)
        {
            case JournalSubmissionStatus.Accepted: Interlocked.Increment(ref accepted); break;
            case JournalSubmissionStatus.StreamBusy: Interlocked.Increment(ref streamBusy); break;
            case JournalSubmissionStatus.Backpressure: Interlocked.Increment(ref backpressure); break;
            case JournalSubmissionStatus.Stopping: Interlocked.Increment(ref stopping); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    internal void SetAdmissionGauges(long operations, long bytes, long streams, DateTimeOffset? oldest)
    {
        Interlocked.Exchange(ref queueOperations, operations);
        Interlocked.Exchange(ref queueOwnedBytes, bytes);
        Interlocked.Exchange(ref reservedStreams, streams);
        Interlocked.Exchange(ref oldestAdmittedUtcTicks, oldest?.UtcTicks ?? 0);
    }

    internal void RecordRetry(JournalStoreFailureKind kind) => Interlocked.Increment(ref retries[(int)kind]);
    internal void CompletionQueued() => Interlocked.Increment(ref unacknowledgedCompletions);
    internal void CompletionAcknowledged() => Interlocked.Decrement(ref unacknowledgedCompletions);
    internal void RecordFailed() => Interlocked.Increment(ref failed);
    internal void RecordQuarantined() => Interlocked.Increment(ref quarantined);

    internal void RecordResult(JournalCommitStatus status)
    {
        switch (status)
        {
            case JournalCommitStatus.Applied: Interlocked.Increment(ref applied); break;
            case JournalCommitStatus.Replayed: Interlocked.Increment(ref replayed); break;
            case JournalCommitStatus.VersionConflict: Interlocked.Increment(ref versionConflict); break;
            case JournalCommitStatus.OperationConflict: Interlocked.Increment(ref operationConflict); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    internal void RecordCommitLatency(TimeSpan latency)
    {
        double milliseconds = Math.Max(0, latency.TotalMilliseconds);
        int bucket = 0;
        while (bucket < CommitLatencyBucketMilliseconds.Length && milliseconds > CommitLatencyBucketMilliseconds[bucket]) bucket++;
        Interlocked.Increment(ref commitLatencyBuckets[bucket]);
    }
}

public sealed class JournalCommitLatencyHistogram
{
    private readonly ReadOnlyCollection<double> upperBoundsMilliseconds;
    private readonly ReadOnlyCollection<long> bucketCounts;

    internal JournalCommitLatencyHistogram(double[] upperBoundsMilliseconds, long[] bucketCounts)
    {
        this.upperBoundsMilliseconds = Array.AsReadOnly((double[])upperBoundsMilliseconds.Clone());
        this.bucketCounts = Array.AsReadOnly((long[])bucketCounts.Clone());
    }

    public IReadOnlyList<double> UpperBoundsMilliseconds => upperBoundsMilliseconds;
    public IReadOnlyList<long> BucketCounts => bucketCounts;
}
