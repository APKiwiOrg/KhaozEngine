using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterTraceMemoComparison
{
    internal sealed record Observation(MovementCoverageResult Result, MovementCoverageSpan[] Spans,
        MovementDomainContact[] Contacts, int Logical, int Hits, int Misses, int Solves);

    internal static Observation Check(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null)
    {
        var expected = Read(field, query, origin, spanCapacity, contactCapacity, identity, bypass: true);
        var actual = Read(field, query, origin, spanCapacity, contactCapacity, identity, bypass: false);
        WaterCoverageComparison.Equal(expected.Result, actual.Result, expected.Spans, actual.Spans,
            expected.Contacts, actual.Contacts);
        return actual;
    }

    internal static Observation Read(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null, bool bypass = false)
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
            var result = field.TraceWater(query, origin, identity ?? MovementWaterFieldTests.Identity,
                spans.AsSpan(0, spanCapacity), contacts.AsSpan(0, contactCapacity));
            return new(result, spans, contacts, TileWaterQueryWork.PrismTraces, TileWaterQueryWork.MemoHits,
                TileWaterQueryWork.MemoMisses, TileWaterQueryWork.ConvexIntersections);
        }
        finally
        {
            TileMediumSampler.BypassTraceMemoForTests = priorBypass;
            TileWaterQueryWork.Enabled = priorEnabled;
        }
    }

    internal static void Memo(Observation observed, bool hit)
    {
        hit &= TileWaterFloatingPointState.TryRead(out _);
        Assert.Equal(1, observed.Logical);
        Assert.Equal(hit ? 1 : 0, observed.Hits);
        Assert.Equal(hit ? 0 : 1, observed.Misses);
        if (hit) Assert.Equal(0, observed.Solves);
    }
}
