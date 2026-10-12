using System;
using System.Threading;

namespace KhaozEngine.WorldMap;

/// <summary>Drains a chunk cache on one background thread, waking when work is queued.</summary>
public sealed class WorldMapChunkWorker : IDisposable
{
    readonly WorldMapChunkCache _cache;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    readonly Thread _thread;
    bool _stopping;
    bool _disposed;

    /// <summary>Starts a background worker and drains any work requested before construction.</summary>
    /// <param name="cache">The cache to build. The caller retains ownership of its lifetime.</param>
    public WorldMapChunkWorker(WorldMapChunkCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _thread = new Thread(Run) { IsBackground = true, Name = "World map chunk worker" };
        _cache.WorkQueued += Wake;
        _thread.Start();
        Wake();
    }

    void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            if (Volatile.Read(ref _stopping)) return;
            while (!Volatile.Read(ref _stopping) && _cache.BuildNext()) { }
        }
    }

    void Wake()
    {
        lock (_gate)
            if (!_stopping) _wake.Set();
    }

    /// <summary>Stops and wakes the worker, then joins it after any active chunk build finishes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                Volatile.Write(ref _stopping, true);
                _cache.WorkQueued -= Wake;
                _wake.Set();
            }
        }
        // A captured cache callback can still enter Wake while this thread is being joined.
        _thread.Join();
        lock (_gate)
        {
            if (_disposed) return;
            _wake.Dispose();
            _disposed = true;
        }
    }
}
