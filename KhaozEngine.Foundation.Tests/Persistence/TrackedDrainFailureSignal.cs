using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Persistence;

namespace KhaozEngine.Tests;

// Discriminates WriteFailed notifications in the tracked drain scenario. Every notification is traced
// with its path role and exception type. Only the exact deliberately invalid child path completes the
// expected signal. Any other path is unexpected evidence: it faults the signal when it arrives first
// and is always kept for AssertNoUnexpected, so it can never pass as a successful drain.
internal sealed class TrackedDrainFailureSignal(TrackedDrainTrace trace, string expectedPath, IReadOnlyDictionary<string, string> roles)
{
    private readonly TaskCompletionSource<PersistenceWriteFailedEventArgs> expected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<PersistenceWriteFailedEventArgs> unexpected = new();

    public Task<PersistenceWriteFailedEventArgs> Expected => expected.Task;

    public string RoleOf(string path) => roles.TryGetValue(path, out string? role) ? role : "unknown";

    public void Observe(object? sender, PersistenceWriteFailedEventArgs args) => trace.Guard("write-failed", () =>
    {
        bool isExpected = string.Equals(args.Path, expectedPath, StringComparison.Ordinal);
        trace.Record("write-failed",
            $"role={RoleOf(args.Path)} expected={isExpected} attempts={args.AttemptCount} error={args.Exception.GetType().FullName}: {args.Exception.Message}");
        if (isExpected)
        {
            expected.TrySetResult(args);
            return;
        }

        unexpected.Enqueue(args);
        expected.TrySetException(new InvalidOperationException(
            $"WriteFailed arrived for the {RoleOf(args.Path)} path '{args.Path}', not the deliberately invalid child path.", args.Exception));
    });

    public void AssertNoUnexpected()
    {
        PersistenceWriteFailedEventArgs[] seen = unexpected.ToArray();
        if (seen.Length == 0)
        {
            return;
        }

        string paths = string.Join(", ", seen.Select(args => $"{RoleOf(args.Path)} '{args.Path}'"));
        throw new AggregateException($"Unexpected WriteFailed notifications: {paths}.", seen.Select(args => args.Exception));
    }
}
