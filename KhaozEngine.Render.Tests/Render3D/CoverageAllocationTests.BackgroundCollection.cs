using System;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageAllocationTests
{
    // Engine issue 1514. A background collection voids every thread's allocation context at the end of its mark
    // and does not charge the unused remainder back, so an unchecked thread counter rises with no allocation.
    [Fact]
    public void Background_collection_in_flight_is_not_counted_as_render_allocation()
    {
        var collection = new BackgroundCollection();
        var covered = Measure(fallback: false, coverage: true, collection);
        output.WriteLine($"coverage={covered} backgroundStarted={collection.Started} " +
            $"inFlightAtStart={collection.InFlight} completed={collection.Completed} " +
            $"consumedBeforeMeasurement={collection.Consumed}");
        Assert.True(collection.Started, "No background gen2 started. The collection ran blocking or did not start.");
        Assert.True(collection.Completed, "The background collection did not complete.");
        Assert.Equal(new Allocation(0, 0, 0, 0, 0, 0, 0), covered);
        GC.KeepAlive(collection);
    }

    /// <summary>Starts a background gen2 just before a measurement opens and waits for its completion inside the
    /// render window. Whether it is still running at the check is diagnostic only. The measuring thread takes a
    /// fresh allocation context after the collection starts. Without a checked region the collection's final mark
    /// can void that context while the thread waits. With one, AllocationRegion.Start drains any active collection
    /// first, because StartNoGCRegion runs a blocking gen2 that waits for it. The region's collection counts cannot
    /// see a void from a collection that began before the region started, so the protection relies on that drain.</summary>
    sealed class BackgroundCollection
    {
        // Live objects for the background gen2 mark.
        readonly object[] graph = new object[1 << 20];
        readonly Thread completion;
        long background, fullBlocking;

        internal BackgroundCollection()
        {
            for (int i = 0; i < graph.Length; i++) graph[i] = new object();
            completion = new Thread(WaitForCompletion) { IsBackground = true, Name = "Background collection completion" };
        }

        internal bool Started { get; private set; }
        internal bool InFlight { get; private set; }
        internal bool Completed { get; private set; }
        /// <summary>Bytes the measuring thread took from its fresh allocation context before the measurement.</summary>
        internal long Consumed { get; private set; }

        internal void Start()
        {
            // A blocking full collection waits for any background collection already running, so the request below
            // starts a new one instead of returning into one that allocation triggered earlier.
            GC.Collect();
            int gen2 = GC.CollectionCount(2);
            background = GC.GetGCMemoryInfo(GCKind.Background).Index;
            fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            long fresh = GC.GetAllocatedBytesForCurrentThread();
            // A gen2 started before Collect returned without a blocking gen2. Completion before this check does
            // not invalidate that precondition. The background index reports only whether it is still running.
            Started = GC.CollectionCount(2) != gen2 && GC.GetGCMemoryInfo(GCKind.FullBlocking).Index == fullBlocking;
            InFlight = Started && GC.GetGCMemoryInfo(GCKind.Background).Index == background;
            if (Started) completion.Start();
            Consumed = GC.GetAllocatedBytesForCurrentThread() - fresh;
        }

        /// <summary>Blocks without allocating until the background collection has completed. Returns at once when
        /// no background collection started.</summary>
        internal void Await() => Completed = Started && completion.Join(TimeSpan.FromSeconds(30));

        void WaitForCompletion()
        {
            while (GC.GetGCMemoryInfo(GCKind.Background).Index == background) Thread.Sleep(1);
            GC.KeepAlive(graph);
        }
    }
}
