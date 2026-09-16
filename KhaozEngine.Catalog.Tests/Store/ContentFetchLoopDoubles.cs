using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.Store;

// The fetch loop's own store doubles, out of the test file because they are the HARNESS rather than the
// assertions: a store that counts what was asked of it, a store that cancels part way, and a progress sink
// that forwards to a lambda. They are shaped by what the loop does to a store, so they live beside the
// loop's tests rather than in the shared pack fixtures.

/// <summary>
/// A remote that CANCELS once it has served a set number of objects and then answers the request that
/// tripped it, so the cancellation lands on the cache WRITE, which is the moment a real cancelled fetch
/// has a temporary file open.
/// </summary>
sealed class CancelAtStore(IPackStore inner, int serveLimit, CancellationTokenSource cancel) : IPackStore
{
    readonly List<string> _completed = [];
    int _limit = serveLimit;
    int _served;

    /// <summary>Every hash served before the cancellation, which is what the cache should hold after it.</summary>
    public IReadOnlyList<string> Completed => _completed;

    /// <summary>Stops cancelling, so a second call over the same pair can finish.</summary>
    public void ServeEverything() => _limit = int.MaxValue;

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(hash, cancellationToken);

    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        _served++;
        if (_served > _limit)
        {
            cancel.Cancel();
        }
        else
        {
            _completed.Add(hash);
        }

        return inner.GetAsync(hash, cancellationToken);
    }

    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => inner.PutAsync(hash, bytes, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ListAsync(versionNumber, cancellationToken);
}

/// <summary>A sink that forwards to a lambda, so the test does not depend on a progress implementation.</summary>
sealed class Progressed(Action<ContentFetchProgress> report) : IProgress<ContentFetchProgress>
{
    public void Report(ContentFetchProgress value) => report(value);
}

/// <summary>
/// A thread-safe in-memory store that counts what was asked of it, because the loop asks in parallel and
/// the facts worth pinning are which hashes were fetched, how many times, and how many at once.
/// </summary>
internal sealed class FetchPackStore : IPackStore, IPackStorePruning
{
    readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> _gets = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> _withheldUntil = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> _corruptUntil = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<string> _fetched = new();
    TaskCompletionSource? _gate;
    int _gateAt;
    int _inFlight;
    int _peak;
    int _deletes;

    /// <summary>Every hash <see cref="GetAsync"/> answered bytes for, in completion order.</summary>
    public IReadOnlyCollection<string> Fetched => _fetched;

    /// <summary>The most fetches this store ever had in flight at once.</summary>
    public int PeakConcurrency => Volatile.Read(ref _peak);

    /// <summary>How many entries were evicted, which is the poisoned-cache path.</summary>
    public int Deletes => Volatile.Read(ref _deletes);

    /// <summary>Files bytes under a name WITHOUT verifying they digest to it.</summary>
    public void Plant(string hash, ReadOnlySpan<byte> bytes) => _objects[hash] = bytes.ToArray();

    /// <summary>Files every client-side object of one published version.</summary>
    public void PlantPack(CatalogPack pack, bool manifest = true)
    {
        foreach (KeyValuePair<string, byte[]> entry in pack.ClientObjects())
        {
            if (manifest || !string.Equals(entry.Key, pack.ClientManifestHash, StringComparison.Ordinal))
            {
                Plant(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>Holds each fetch until <paramref name="count"/> are in flight, so the bound is observable.</summary>
    public void GateAt(int count)
    {
        _gateAt = count;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Answers null for this hash, which is an object the source does not hold.</summary>
    public void Withhold(string hash) => _withheldUntil[hash] = int.MaxValue;

    /// <summary>Answers null for this hash until the given fetch of it, then serves it.</summary>
    public void WithholdUntilAttempt(string hash, int attempt) => _withheldUntil[hash] = attempt;

    /// <summary>Stops withholding a hash.</summary>
    public void Serve(string hash) => _withheldUntil.TryRemove(hash, out _);

    /// <summary>Answers bytes that do not digest to their name on the FIRST fetch of this hash only.</summary>
    public void CorruptFirstGet(string hash) => _corruptUntil[hash] = 1;

    /// <summary>Forgets what was asked, so a second fetch is counted on its own.</summary>
    public void Reset()
    {
        _fetched.Clear();
        _gets.Clear();
    }

    /// <summary>How many times this hash was asked for.</summary>
    public int GetCount(string hash) => _gets.TryGetValue(hash, out int count) ? count : 0;

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => Task.FromResult(_objects.ContainsKey(hash) && !_withheldUntil.ContainsKey(hash));

    public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        int attempt = _gets.AddOrUpdate(hash, 1, static (_, count) => count + 1);
        int flight = Interlocked.Increment(ref _inFlight);
        Peak(flight);
        try
        {
            await GateAsync(flight, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        if (_withheldUntil.TryGetValue(hash, out int until) && attempt < until)
        {
            return null;
        }

        if (!_objects.TryGetValue(hash, out byte[]? bytes))
        {
            return null;
        }

        if (_corruptUntil.TryGetValue(hash, out int corruptUntil) && attempt <= corruptUntil)
        {
            byte[] corrupt = bytes.ToArray();
            corrupt[^1] ^= 0xFF;
            bytes = corrupt;
        }

        _fetched.Enqueue(hash);
        return bytes;
    }

    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _objects[hash] = bytes.ToArray();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(
        int versionNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (string hash in _objects.Keys)
        {
            yield return hash;
        }
    }

    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deletes);
        return Task.FromResult(_objects.TryRemove(hash, out _));
    }

    async Task GateAsync(int flight, CancellationToken cancellationToken)
    {
        if (_gate is not TaskCompletionSource gate)
        {
            return;
        }

        if (flight >= _gateAt)
        {
            gate.TrySetResult();
        }

        // The timeout is a FAILURE path, not a delay: a loop that never reaches the bound falls through
        // it with a peak below four rather than hanging the suite.
        await Task.WhenAny(gate.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
            .ConfigureAwait(false);
    }

    void Peak(int flight)
    {
        int seen = Volatile.Read(ref _peak);
        while (flight > seen)
        {
            int previous = Interlocked.CompareExchange(ref _peak, flight, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}
