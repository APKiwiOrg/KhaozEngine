using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostScratchTests
{
    [Fact]
    public void ScratchIncludesTheCompleteFootprintOwner()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        // Query scratch exists from the first read. One footprint query leaves its collector idle in it.
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
            ((IPhysicsSupportFootprint)world).QuerySupportFootprint(lease, SupportFootprintTests.Request(),
                new SupportFootprintMember[256], new ulong[1024]);
        QueryScratch scratch = Assert.Single(ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed());
        SupportFootprintCollector? footprint = scratch.Footprints.Idle;
        Assert.NotNull(footprint);
        long complete = SupportCostScratch.PayloadBytes(world);
        long without;
        try
        {
            // Slot is a mutable struct field, so this writes the scratch itself, not a boxed copy.
            scratch.Footprints.Idle = null;
            without = SupportCostScratch.PayloadBytes(world);
        }
        finally { scratch.Footprints.Idle = footprint; }
        Assert.True(complete > without, $"Complete {complete}, without footprint {without}.");
    }

    [Fact]
    public void ScratchInspectionCountsInlineValuesAndSharedReferencesWithinTheAllocationRegion()
    {
        var scratch = new Scratch();
        var coldSeen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var warmSeen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        MethodInfo visit = typeof(SupportCostScratch).GetMethod("Visit", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var region = SupportAllocationRegion.Start();
        long cold = (long)visit.Invoke(null, [scratch, coldSeen])!;
        long warm = (long)visit.Invoke(null, [scratch, warmSeen])!;
        region.Dispose();
        long expected = 64_000 * 12 + 64_000 * IntPtr.Size + 3;
        Assert.Equal(expected, cold);
        Assert.Equal(expected, warm);
    }

    readonly record struct ReferenceStorage(byte[] Bytes);

    sealed class Scratch
    {
        internal readonly Vector3[] Positions = new Vector3[64_000];
        internal readonly ReferenceStorage[] References;

        internal Scratch()
        {
            byte[] shared = new byte[3];
            References = new ReferenceStorage[64_000];
            Array.Fill(References, new(shared));
        }
    }
}
