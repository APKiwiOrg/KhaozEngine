using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves background draining, wake-up, exception isolation and worker shutdown.</summary>
public sealed class ChunkWorkerTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    static readonly WorldMapStyle Style = new() { ChunkMetres = 8f };

    /// <summary>One background worker builds all requested chunks while the caller polls the cache.</summary>
    [Fact]
    public void Worker_BuildsRequestedChunks()
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(0f, 0f, 32f, 8f)), Style);
        var worker = new WorldMapChunkWorker(cache);
        try
        {
            WorldMapChunkCoord[] chunks = [new(0, 0), new(1, 0), new(2, 0), new(3, 0)];
            foreach (WorldMapChunkCoord chunk in chunks) cache.Request(chunk, chunk.X);

            Assert.True(SpinWait.SpinUntil(() => chunks.All(cache.IsCurrent), Timeout));
        }
        finally
        {
            DisposeWorkerBounded(worker);
        }
    }

    /// <summary>Work queued before worker construction is drained without another request.</summary>
    [Fact]
    public void Worker_BuildsPreviouslyRequestedChunks()
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(0f, 0f, 8f, 8f)), Style);
        cache.Request(new(0, 0), 1f);
        var worker = new WorldMapChunkWorker(cache);

        try
        {
            Assert.True(SpinWait.SpinUntil(() => cache.IsCurrent(new(0, 0)), Timeout));
        }
        finally
        {
            DisposeWorkerBounded(worker);
        }
    }

    /// <summary>A source failure does not terminate the worker or prevent later chunks from publishing.</summary>
    [Fact]
    public void Worker_SurvivesThrowingSource()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 16f, 8f));
        source.BeforeSample = area =>
        {
            if (area.X < 0f) throw new InvalidOperationException("First chunk failure.");
        };
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 0f);
        cache.Request(new(1, 0), 1f);
        var worker = new WorldMapChunkWorker(cache);

        try
        {
            Assert.True(SpinWait.SpinUntil(() => cache.IsFailed(new(0, 0)) && cache.IsCurrent(new(1, 0)), Timeout));
        }
        finally
        {
            DisposeWorkerBounded(worker);
        }
    }

    /// <summary>The worker wakes again after becoming idle when a built chunk is invalidated.</summary>
    [Fact]
    public void Worker_RebuildsInvalidatedChunk()
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(0f, 0f, 8f, 8f)), Style);
        var worker = new WorldMapChunkWorker(cache);
        try
        {
            cache.Request(new(0, 0), 1f);
            Assert.True(SpinWait.SpinUntil(() => cache.IsCurrent(new(0, 0)), Timeout));
            Assert.True(cache.TryGet(new(0, 0), out var original));

            cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));

            Assert.True(SpinWait.SpinUntil(() => cache.IsCurrent(new(0, 0)) &&
                cache.TryGet(new(0, 0), out var built) && built.Generation == original.Generation + 1, Timeout));
        }
        finally
        {
            DisposeWorkerBounded(worker);
        }
    }

    /// <summary>The worker exits promptly and no request after disposal can start another build.</summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("xUnit", "xUnit1031", Justification = "The shutdown proof requires a synchronous bounded wait.")]
    public void Dispose_StopsTheThread()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 16f, 8f));
        using var cache = new WorldMapChunkCache(source, Style);
        var worker = new WorldMapChunkWorker(cache);
        Task? disposeTask = null;
        try
        {
            cache.Request(new(0, 0), 1f);
            Assert.True(SpinWait.SpinUntil(() => cache.IsCurrent(new(0, 0)), Timeout));
            var elapsed = Stopwatch.StartNew();

            disposeTask = Task.Run(worker.Dispose);
            Assert.True(disposeTask.Wait(TimeSpan.FromSeconds(5)));
            disposeTask.GetAwaiter().GetResult();

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
            var secondDisposeTask = Task.Run(worker.Dispose);
            Assert.True(secondDisposeTask.Wait(TimeSpan.FromSeconds(5)));
            secondDisposeTask.GetAwaiter().GetResult();
            cache.Request(new(1, 0), 1f);
            Assert.True(cache.BuildNext());
            Assert.True(cache.IsCurrent(new(1, 0)));
            Assert.Equal(2, source.SampleCalls);
        }
        finally
        {
            if (disposeTask is null)
            {
                DisposeWorkerBounded(worker);
            }
        }
    }

    /// <summary>A captured wake callback can finish while disposal joins the active worker.</summary>
    [Fact]
    public async Task Dispose_DuringCapturedWake_DoesNotDeadlock()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 8f, 8f));
        using var cache = new WorldMapChunkCache(source, Style);
        using var sampling = new ManualResetEventSlim();
        using var releaseSample = new ManualResetEventSlim();
        using var callback = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        Thread? buildThread = null;
        source.BeforeSample = _ =>
        {
            Volatile.Write(ref buildThread, Thread.CurrentThread);
            sampling.Set();
            if (!releaseSample.Wait(Timeout)) throw new TimeoutException("Sample was not released.");
        };
        cache.WorkQueued += () =>
        {
            if (!ReferenceEquals(Thread.CurrentThread, Volatile.Read(ref buildThread))) return;
            callback.Set();
            if (!releaseCallback.Wait(Timeout)) throw new TimeoutException("Callback was not released.");
        };
        var worker = new WorldMapChunkWorker(cache);
        Thread? disposeThread = null;
        Task? disposal = null;
        try
        {
            cache.Request(new(0, 0), 1f);
            Assert.True(sampling.Wait(Timeout));
            cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));
            releaseSample.Set();
            Assert.True(callback.Wait(Timeout));
            disposal = Task.Run(() =>
            {
                Volatile.Write(ref disposeThread, Thread.CurrentThread);
                worker.Dispose();
            });
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref disposeThread) is { } thread &&
                (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                Timeout));
            releaseCallback.Set();

            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseSample.Set();
            releaseCallback.Set();
            // Do not make a regression failure hang the test process on a second disposal.
            if (disposal is null) DisposeWorkerBounded(worker);
            else if (disposal.IsFaulted) _ = disposal.Exception;
        }
    }

#pragma warning disable xUnit1031 // Cleanup must bound worker disposal even when the test has already failed.
    static void DisposeWorkerBounded(WorldMapChunkWorker worker)
    {
        Task disposeTask = Task.Run(worker.Dispose);
        Assert.True(disposeTask.Wait(TimeSpan.FromSeconds(5)));
        disposeTask.GetAwaiter().GetResult();
    }
#pragma warning restore xUnit1031
}
