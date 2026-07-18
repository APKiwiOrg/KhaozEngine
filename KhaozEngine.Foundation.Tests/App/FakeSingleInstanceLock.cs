using System;
using System.Collections.Generic;
using KhaozEngine.App;

namespace KhaozEngine.Tests.App;

/// <summary>
/// In-memory <see cref="ISingleInstanceLock"/> so <see cref="SingleInstanceGuard"/>'s orchestration (the
/// conflict decision, the foreground signal, lock lifetime) is testable without a real named OS mutex.
/// </summary>
internal sealed class FakeSingleInstanceLock : ISingleInstanceLock
{
    public bool AcquireSucceeds = true;
    public string? AcquiredKey;
    public TimeSpan AcquiredPredecessorWait;
    public int TryAcquireCalls;

    public bool ForegroundRequested;
    public string? ForegroundRequestedKey;

    public readonly Queue<bool> ForegroundRequestResults = new();
    public bool Disposed;

    public bool TryAcquire(string key, TimeSpan predecessorWait)
    {
        TryAcquireCalls++;
        AcquiredKey = key;
        AcquiredPredecessorWait = predecessorWait;
        return AcquireSucceeds;
    }

    public void RequestForeground(string key)
    {
        ForegroundRequested = true;
        ForegroundRequestedKey = key;
    }

    public bool WaitForForegroundRequest(TimeSpan timeout)
        => ForegroundRequestResults.Count > 0 && ForegroundRequestResults.Dequeue();

    public void Dispose() => Disposed = true;
}
