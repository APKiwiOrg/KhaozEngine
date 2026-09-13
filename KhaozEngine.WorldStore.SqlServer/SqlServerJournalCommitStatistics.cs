using System.Threading;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>
/// Round-trip accounting for the journal write path. <paramref name="Operations"/> counts every attempted
/// initialization or commit, whatever its outcome. <paramref name="Commands"/> counts the SQL commands those
/// attempts executed on their own transaction, so <c>Commands / Operations</c> is the per-operation round-trip
/// cost above the transaction's own begin and commit. Recovery work on a separate connection after a duplicate
/// key is not counted.
/// </summary>
public sealed record SqlServerJournalCommitStatistics(
    long Operations,
    long Commands,
    int MaximumCommandsForOneOperation);

internal sealed class SqlServerJournalCommandTally
{
    internal int Count { get; private set; }

    internal void Increment() => Count++;
}

internal sealed class SqlServerJournalCommitCounters
{
    private long operations;
    private long commands;
    private int maximumCommandsForOneOperation;

    internal void Record(int commandsForOneOperation)
    {
        Interlocked.Increment(ref operations);
        Interlocked.Add(ref commands, commandsForOneOperation);
        int observed = Volatile.Read(ref maximumCommandsForOneOperation);
        while (commandsForOneOperation > observed)
        {
            int exchanged = Interlocked.CompareExchange(ref maximumCommandsForOneOperation, commandsForOneOperation, observed);
            if (exchanged == observed) return;
            observed = exchanged;
        }
    }

    internal SqlServerJournalCommitStatistics Snapshot()
        => new(
            Interlocked.Read(ref operations),
            Interlocked.Read(ref commands),
            Volatile.Read(ref maximumCommandsForOneOperation));
}
