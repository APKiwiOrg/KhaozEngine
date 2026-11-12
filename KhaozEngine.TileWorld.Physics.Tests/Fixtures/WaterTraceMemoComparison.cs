using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterTraceMemoComparison
{
    internal sealed record Observation(MovementCoverageResult Result, MovementCoverageSpan[] Spans,
        MovementDomainContact[] Contacts, int Logical, int Hits, int Misses, int Solves, ulong Status, bool HasStatus,
        ulong KernelStatus, bool HasKernelStatus);

    internal static Observation Check(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null)
    {
        // Snapshot only. There is no owned native resource, and the actual trace must leave its
        // outgoing flags in place. Restore independently immediately before each computation.
        // Initialize the measured native reader before capturing either starting environment.
        TileWaterFloatingPointState.TryRead(out _, out _);
        var environment = WaterFloatingPointEnvironment.Capture();
        var expected = Read(field, query, origin, spanCapacity, contactCapacity, identity, bypass: true, environment);
        var actual = Read(field, query, origin, spanCapacity, contactCapacity, identity, bypass: false, environment);
        WaterCoverageComparison.Equal(expected.Result, actual.Result, expected.Spans, actual.Spans,
            expected.Contacts, actual.Contacts);
        // Two cold kernel executions can cross JIT tiers and raise different unused-arithmetic
        // flags. Compare replay with recomputation, and a miss with its own post-kernel snapshot.
        // The dedicated cleared/preset cases also compare independently restored warm kernels.
        if (expected.HasStatus && actual.HasStatus && actual.Hits != 0) Assert.Equal(expected.Status, actual.Status);
        if (actual.HasStatus && actual.HasKernelStatus) Assert.Equal(actual.KernelStatus, actual.Status);
        for (int i = 0; i < actual.Result.ContactsWritten; i++)
        {
            Assert.Same(query.Body.CurrentSpace.WorldId, actual.Contacts[i].Space.WorldId);
            Assert.Same(query.Body.CurrentSpace.LocalId, actual.Contacts[i].Space.LocalId);
        }
        return actual;
    }

    internal static Observation Read(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null, bool bypass = false, WaterFloatingPointEnvironment? environment = null)
    {
        var spans = new MovementCoverageSpan[Math.Max(64, spanCapacity) + 1];
        var contacts = new MovementDomainContact[Math.Max(256, contactCapacity) + 1];
        Array.Fill(spans, new(.9375f, 1, 7, 3, false));
        Array.Fill(contacts, new(new("sentinel", "domain"), new("sentinel", "space"),
            new(-7, 3, 3, true, "lower", "upper"), new(7, 9), Vector3.UnitZ, .9375f, "boundary", 7,
            MovementContactOverlap.Tangent, MovementContactOverlap.Tangent));
        bool priorBypass = TileMediumSampler.BypassTraceMemoForTests;
        bool priorEnabled = TileWaterQueryWork.Enabled;
        TileMediumSampler.BypassTraceMemoForTests = bypass;
        TileWaterQueryWork.Reset();
        try
        {
            environment?.Restore();
            var result = field.TraceWater(query, origin, identity ?? MovementWaterFieldTests.Identity,
                spans.AsSpan(0, spanCapacity), contacts.AsSpan(0, contactCapacity));
            bool hasStatus = WaterFloatingPointEnvironment.TryReadStatus(out ulong status);
            return new(result, spans, contacts, TileWaterQueryWork.PrismTraces, TileWaterQueryWork.MemoHits,
                TileWaterQueryWork.MemoMisses, TileWaterQueryWork.ConvexIntersections, status, hasStatus,
                TileWaterQueryWork.KernelStatusForTests, TileWaterQueryWork.KernelStatusCapturedForTests);
        }
        finally
        {
            TileMediumSampler.BypassTraceMemoForTests = priorBypass;
            TileWaterQueryWork.Enabled = priorEnabled;
        }
    }

    internal static void Memo(Observation observed, bool hit)
    {
        hit &= TileWaterFloatingPointState.TryRead(out _, out _);
        Assert.Equal(1, observed.Logical);
        Assert.Equal(hit ? 1 : 0, observed.Hits);
        Assert.Equal(hit ? 0 : 1, observed.Misses);
        if (hit) Assert.Equal(0, observed.Solves);
    }
}
