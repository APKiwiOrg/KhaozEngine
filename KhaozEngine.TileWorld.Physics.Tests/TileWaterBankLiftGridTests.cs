using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterBankLiftGridTests(ITestOutputHelper output)
{
    [Fact]
    public void ContinuousBankLiftsCertifyAtEdgeAndDiagonalOffsetsAndAdjacentFloats()
    {
        // #1761 r1. Only discontinuity-adjacent lateral values, five approach phases and
        // three legal lift heights. The dense 1/512 m review grid is deliberately absent.
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));
        var ground = field.CaptureTraceForTests().Ground;
        float anchor = ground.HeightAt(110.375f, -236.79904f);
        float[] phases = [-236.59904f, -236.69904f, -236.79904f, -236.89905f, -236.97406f];
        float[] lifts = [0.096971154f, 0.2f, 0.4f];
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        using var digest = new WaterCoverageDigest();
        int count = 0, knownBefore = 0, refusedBefore = 0, refusedNow = 0;
        foreach (float z in phases)
            foreach (float x in Laterals(z))
                foreach (float lift in lifts)
                {
                    float y = -0.45606077f + (ground.HeightAt(x, z) - anchor);
                    var query = new MovementMediumSweepQuery(new(new(x, y, z), 0.3f, 0.75f, GrazingWaterBank.Space, null), new(0, lift, 0));
                    var result = field.TraceWater(query, Vector3.Zero, GrazingWaterBank.Identity, spans, contacts);
                    count++;
                    if (WasRefusedByR0(query)) refusedBefore++;
                    else
                    {
                        Assert.Equal(MovementAvailability.Known, result.Availability);
                        knownBefore++;
                        digest.Add(query, result, spans.AsSpan(0, result.SpansWritten), contacts.AsSpan(0, result.ContactsWritten));
                    }
                    if (result.Availability == MovementAvailability.PrecisionExceeded) refusedNow++;
                    else Assert.Equal(MovementAvailability.Known, result.Availability);
                }
        output.WriteLine($"cases={count}, known-r0={knownBefore}, refused-r0={refusedBefore}, refused-now={refusedNow}, known-digest={digest.Digest()}");
        // Captured in a separate red run on 7eac6072c, before either r1 production change.
        Assert.Equal(510, count);
        Assert.Equal(502, knownBefore);
        Assert.Equal("D7B328FCD504F7082467888306C6BF5B3F30E703378606590805F6B6BA325295", digest.Digest());
        Assert.Equal(8, refusedBefore);
        Assert.Equal(0, refusedNow);
    }

    internal static IEnumerable<float> Laterals(float z)
    {
        var values = new SortedSet<float> { 110.275390625f, 110.375f, 111.275390625f, 111.375f };
        for (int tile = 109; tile <= 111; tile++)
            foreach (float at in new[] { (float)tile, tile + 0.3f, tile + 0.7f,
                tile + (-z - 236) + 0.3f, tile + (-z - 236) - 0.3f })
                foreach (float x in new[] { MathF.BitDecrement(at), at, MathF.BitIncrement(at) })
                    if (x >= 109.5f && x <= 111.5f) values.Add(x);
        return values;
    }

    static bool WasRefusedByR0(MovementMediumSweepQuery query)
    {
        if (query.Delta.Y is not (0.2f or 0.4f)) return false;
        Vector3 p = query.Body.Centre;
        return (p.X is 110.275390625f or 111.275390625f && p.Y == -0.36105501651763916f && p.Z == -236.69903564453125f) ||
            (p.X is 110.375f or 111.375f && p.Y == -0.4560607671737671f && p.Z == -236.79904174804688f);
    }
}
