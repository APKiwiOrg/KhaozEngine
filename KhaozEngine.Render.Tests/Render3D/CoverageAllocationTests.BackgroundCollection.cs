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
        output.WriteLine($"coverage={covered} inFlightAtStart={collection.InFlight} completed={collection.Completed} " +
            $"consumedBeforeMeasurement={collection.Consumed}");
        Assert.True(collection.InFlight,
            "No background gen2 was still in flight when the measurement opened. It finished early or ran blocking.");
        Assert.True(collection.Completed, "The background collection did not complete.");
        Assert.Equal(new Allocation(0, 0, 0, 0, 0, 0, 0), covered);
        GC.KeepAlive(collection);
    }

    /// <summary>Starts a background collection just before a measurement opens and waits for it inside the
    /// render window. The measuring thread takes a fresh allocation context after the collection starts. Without a
    /// checked region the collection's final mark voids that context while the thread waits. With one,
    /// AllocationRegion.Start drains the collection first, because StartNoGCRegion runs a blocking gen2 that waits
    /// for it, so Await returns at once. The region's collection counts cannot see a void from a collection that began
    /// before the region started, so the protection relies on that drain.</summary>
    sealed class BackgroundCollection
    {
        // Live objects that keep the background mark running past the windows that precede the render window.
        readonly object[] graph = new object[1 << 20];
        readonly Thread completion;
        long background, fullBlocking;

        internal BackgroundCollection()
        {
            for (int i = 0; i < graph.Length; i++) graph[i] = new object();
            completion = new Thread(WaitForCompletion) { IsBackground = true, Name = "Background collection completion" };
        }

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
            // Decided before any other work. Only a background gen2 that is still running advances the gen2 count
            // while neither the background nor the full blocking index has moved.
            InFlight = GC.CollectionCount(2) != gen2 && GC.GetGCMemoryInfo(GCKind.Background).Index == background
                && GC.GetGCMemoryInfo(GCKind.FullBlocking).Index == fullBlocking;
            if (InFlight) completion.Start();
            Consumed = GC.GetAllocatedBytesForCurrentThread() - fresh;
        }

        /// <summary>Blocks without allocating until the background collection has completed. Returns at once when
        /// no background collection was in flight.</summary>
        internal void Await() => Completed = InFlight && completion.Join(TimeSpan.FromSeconds(30));

        void WaitForCompletion()
        {
            while (GC.GetGCMemoryInfo(GCKind.Background).Index == background) Thread.Sleep(1);
            GC.KeepAlive(graph);
        }
    }
}
