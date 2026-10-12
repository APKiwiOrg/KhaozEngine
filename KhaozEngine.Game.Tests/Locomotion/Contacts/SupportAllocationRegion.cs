using System;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Keeps allocation-context reconciliation out of one cold/warm pair. Every gameplay allocation
/// remains counted. This is test-only process state and requires the AllocSensitive collection.</summary>
internal sealed class SupportAllocationRegion : IDisposable
{
    bool active;
    readonly int gen0, gen1, gen2;

    SupportAllocationRegion(bool active)
    {
        this.active = active;
        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);
    }

    internal static SupportAllocationRegion Start(bool enabled = true, long totalSize = 4 * 1024 * 1024)
    {
        // Keep allocation-context reconciliation out of the cold/warm pair without excluding gameplay allocations.
        // https://github.com/dotnet/runtime/issues/134724
        if (enabled && !GC.TryStartNoGCRegion(totalSize, disallowFullBlockingGC: false))
            throw new InvalidOperationException("The allocation measurement could not enter its no-GC region.");
        return new(enabled);
    }

    public void Dispose()
    {
        if (!active) return;
        // Ending a broken or exhausted region fails the test. Never retry or accept its counter delta.
        bool unchanged = gen0 == GC.CollectionCount(0) && gen1 == GC.CollectionCount(1) && gen2 == GC.CollectionCount(2);
        active = false;
        GC.EndNoGCRegion();
        if (!unchanged)
            throw new InvalidOperationException("A collection crossed the allocation measurement region.");
    }
}
