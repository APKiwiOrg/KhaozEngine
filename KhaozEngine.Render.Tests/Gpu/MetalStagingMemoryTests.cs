using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class MetalStagingMemoryTests
{
    const int MeasuredPairs = 24;

    [Fact]
    public void Warmed_native_creation_and_release_allocate_no_managed_accounting_leases()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource();
        var source = new MetalMemoryStagingSource(native, memory);
        source.Destroy(source.Create(16));
        AssertMemory(memory, 0, 0, 1);

        long allocated;
        using (AllocationRegion.Start("24 fresh Metal staging create/destroy pairs"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredPairs; i++) source.Destroy(source.Create(16));
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(25, native.Created);
        Assert.Equal(25, native.Destroyed);
        AssertMemory(memory, 0, 0, 1);
    }

    [Theory]
    [InlineData(0ul, 24)]
    [InlineData(16ul, 0)]
    public void Warmed_arena_trim_recreates_blocks_without_managed_allocation(ulong retention, int freshBlocks)
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource();
        using var arena = new MetalStagingArena(new MetalMemoryStagingSource(native, memory), 3,
            blockBytes: 16, retentionBytes: retention);
        for (int frame = 0; frame < 6; frame++) Rotate(arena, frame);
        AssertMemory(memory, 48, 3, 3);
        int created = arena.BlocksCreated, destroyed = arena.BlocksDestroyed;

        long allocated;
        using (AllocationRegion.Start("24 tiny Metal staging arena rotations"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 6; frame < 6 + MeasuredPairs; frame++) Rotate(arena, frame);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(freshBlocks, arena.BlocksCreated - created);
        Assert.Equal(freshBlocks, arena.BlocksDestroyed - destroyed);
        Assert.Equal(arena.BlocksCreated, native.Created);
        Assert.Equal(arena.BlocksDestroyed, native.Destroyed);
        AssertMemory(memory, 48, 3, 3);
        arena.Dispose(); arena.Dispose();
        Assert.Equal(native.Created, native.Destroyed);
        AssertMemory(memory, 0, 0, 3);
    }

    [Fact]
    public void Duplicate_creation_rolls_back_native_reference_and_preserves_owner_until_release()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource();
        var source = new MetalMemoryStagingSource(native, memory);
        var first = source.Create(16);
        native.NextHandle = first.Buffer;
        Assert.Throws<InvalidOperationException>(() => source.Create(32));
        Assert.Equal(1, native.Destroyed);
        AssertMemory(memory, 16, 1, 1);

        source.Destroy(first);
        memory.ReleaseStaging(first.Buffer);
        memory.ReleaseStaging(first.Buffer);
        AssertMemory(memory, 0, 0, 1);
        native.NextHandle = first.Buffer;
        var reused = source.Create(32);
        AssertMemory(memory, 32, 1, 1);
        source.Destroy(reused);
        Assert.Equal(3, native.Destroyed);
        AssertMemory(memory, 0, 0, 1);
    }

    [Fact]
    public void Capacity_overflow_rolls_back_creation_without_partial_counts_or_peak()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource();
        var source = new MetalMemoryStagingSource(native, memory);
        var first = source.Create(ulong.MaxValue);
        Assert.Throws<OverflowException>(() => source.Create(1));
        Assert.Equal(1, native.Destroyed);
        AssertMemory(memory, ulong.MaxValue, 1, 1);
        source.Destroy(first);

        native.NextHandle = (IntPtr)0x5001;
        var reused = source.Create(4);
        AssertMemory(memory, 4, 1, 1);
        source.Destroy(reused);
        AssertMemory(memory, 0, 0, 1);
    }

    [Fact]
    public void Setup_creation_overflow_releases_native_staging_without_registering_its_handle()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new FakeMetalSetupNative();
        var setup = new MetalMemorySetupNative(native, memory);
        memory.OwnStaging((IntPtr)0x5000, ulong.MaxValue);
        Assert.Throws<OverflowException>(() => setup.Stage(new byte[4]));
        Assert.Single(native.ReleasedStaging);
        AssertMemory(memory, ulong.MaxValue, 1, 1);
        memory.ReleaseStaging((IntPtr)0x5000);

        var staged = setup.Stage(new byte[4]);
        AssertMemory(memory, 4, 1, 1);
        setup.AbandonStaging(staged);
        setup.AbandonStaging(staged);
        Assert.Single(native.ReleasedStaging);
        AssertMemory(memory, 0, 0, 1);
    }

    [Fact]
    public void Invalid_creation_and_nil_ownership_leave_the_ledger_empty()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource { Invalid = true };
        var source = new MetalMemoryStagingSource(native, memory);
        Assert.False(source.Create(16).IsValid);
        Assert.Equal(0, native.Destroyed);
        Assert.Throws<ArgumentException>(() => memory.OwnStaging(IntPtr.Zero, 16));
        AssertMemory(memory, 0, 0, 0);
    }

    [Fact]
    public void Dead_arena_refuses_creation_but_disposal_releases_existing_staging_once()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new SilentStagingSource();
        var live = new DeviceLiveness();
        using var arena = new MetalStagingArena(new MetalMemoryStagingSource(native, memory), 3,
            blockBytes: 16, retentionBytes: 0, liveness: live);
        Assert.True(arena.Take(16).IsValid);
        live.MarkDead();
        Assert.False(arena.Take(16).IsValid);
        Assert.Equal(1, native.Created);
        AssertMemory(memory, 16, 1, 1);
        arena.Dispose(); arena.Dispose();
        Assert.Equal(1, native.Destroyed);
        AssertMemory(memory, 0, 0, 1);
    }

    static void Rotate(MetalStagingArena arena, int frame)
    {
        arena.BeginSlot(frame % 3, (ulong)frame);
        _ = arena.Take(16);
        arena.RecordSubmitted((ulong)frame + 1);
    }

    static void AssertMemory(MetalMemoryDiagnostics memory, ulong capacity, ulong count, ulong peakCount)
    {
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(capacity, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(count, snapshot.LiveResourceCount.Value!.UInt64);
        Assert.Equal(peakCount, memory.PeakResourceCount);
        Assert.Equal(0ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(0ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(0ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
    }

    // Fabricated struct handles only. No payload or per-call log, and the mapped address is never dereferenced.
    sealed class SilentStagingSource : IMetalStagingSource
    {
        internal nint NextHandle = 0x5000;
        internal int Created, Destroyed;
        internal bool Invalid;

        public MetalStagingBlock Create(ulong sizeBytes)
        {
            Created++;
            return Invalid ? default : new((IntPtr)NextHandle++, (IntPtr)0x1000, sizeBytes);
        }

        public void Destroy(in MetalStagingBlock block) => Destroyed++;
    }
}
