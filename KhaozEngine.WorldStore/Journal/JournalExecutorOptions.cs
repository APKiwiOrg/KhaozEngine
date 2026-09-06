using System;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalExecutorOptions
{
    public JournalExecutorOptions(
        int workerCount,
        int operationCapacity,
        long ownedByteCapacity,
        int maximumTransientRetries = 8,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        if (workerCount <= 0) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (operationCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(operationCapacity));
        if (ownedByteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(ownedByteCapacity));
        if (maximumTransientRetries < 0) throw new ArgumentOutOfRangeException(nameof(maximumTransientRetries));

        TimeSpan initial = initialRetryDelay ?? TimeSpan.FromMilliseconds(25);
        TimeSpan maximum = maximumRetryDelay ?? TimeSpan.FromSeconds(2);
        if (initial < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        if (maximum < initial) throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));

        WorkerCount = workerCount;
        OperationCapacity = operationCapacity;
        OwnedByteCapacity = ownedByteCapacity;
        MaximumTransientRetries = maximumTransientRetries;
        InitialRetryDelay = initial;
        MaximumRetryDelay = maximum;
    }

    public int WorkerCount { get; }
    public int OperationCapacity { get; }
    public long OwnedByteCapacity { get; }
    public int MaximumTransientRetries { get; }
    public TimeSpan InitialRetryDelay { get; }
    public TimeSpan MaximumRetryDelay { get; }
}
