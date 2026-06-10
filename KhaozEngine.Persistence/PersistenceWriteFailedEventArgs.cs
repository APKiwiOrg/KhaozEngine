using System;

namespace KhaozEngine.Persistence;

/// <summary>
/// Raised by <see cref="PersistenceQueue"/> when a write to a path has failed after all retry attempts.
/// </summary>
public sealed class PersistenceWriteFailedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public PersistenceWriteFailedEventArgs(string path, Exception exception, int attemptCount)
    {
        Path = path;
        Exception = exception;
        AttemptCount = attemptCount;
    }

    /// <summary>The target path the write was destined for.</summary>
    public string Path { get; }

    /// <summary>The exception from the final failed attempt.</summary>
    public Exception Exception { get; }

    /// <summary>How many attempts were made before giving up (equals the queue's configured max attempts).</summary>
    public int AttemptCount { get; }
}
