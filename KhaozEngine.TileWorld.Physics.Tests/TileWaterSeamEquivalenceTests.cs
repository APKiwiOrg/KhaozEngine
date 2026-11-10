using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterSeamEquivalenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bank", 1761)]
    [InlineData("crease", 1762)]
    [InlineData("islands", 1763)]
    [InlineData("two-surfaces", 1764)]
    [InlineData("tangency", 1765)]
    [InlineData("wide-bank", 1766)]
    public void SeededGeometryKeepsEveryLegacyKnownRecordAndRefusal(string scene, int seed)
    {
        var doc = scene == "islands" ? TileWaterIslands.Create() : GrazingWaterBank.Document();
        if (scene == "crease") doc.SetCornerHeightCm(110, 237, 0, -125);
        if (scene == "two-surfaces") doc = TwoSurfaces();
        if (scene == "tangency") doc = MovementWaterFieldTests.WaterWorld();
        if (scene == "wide-bank") doc = GrazingWaterBank.Document(109, 3);
        var field = MovementWaterFieldTests.Field(doc);
        var legacy = field.CaptureLegacyTraceForTests();
        var random = new Random(seed);
        var counts = new Dictionary<MovementAvailability, int>();
        int changed = 0;
        for (int i = 0; i < 128; i++)
        {
            Vector3 centre = scene switch
            {
                "islands" => new(Next(-1, 7), Next(-4, 1), Next(-1, 7)),
                "two-surfaces" => new(Next(63.5f, 64.5f), Next(-2, 2), Next(0.125f, 0.75f)),
                _ => new(Next(109.5f, 111.5f), Next(-1.5f, 0.5f), Next(-238.5f, -235.5f)),
            };
            Vector3 drive = i % 5 == 0 ? Vector3.Zero : new(Next(-0.5f, 0.5f), Next(-0.5f, 0.5f), Next(-0.5f, 0.5f));
            var query = new MovementMediumSweepQuery(new(centre, i % 3 == 0 ? 0.125f : 0.3f, 0.75f,
                MovementWaterFieldTests.Space, null), drive);
            if (scene == "tangency")
                query = new(new(new(-0.5f + i / 128f, -0.5f, -0.125f), 0.125f, 0.25f,
                    MovementWaterFieldTests.Space, null), i % 4 == 0 ? Vector3.Zero : Vector3.UnitX);
            if (scene is "bank" or "crease" && i < 16)
            {
                var lift = GrazingWaterBank.Lift(i % 2 == 0 ? 110.375f : 110.5f);
                query = new(new(lift.Body.Centre + new Vector3(0, (i / 2 - 3) * 0.00001f, 0),
                    lift.Body.Radius, lift.Body.HalfHeight, lift.Body.CurrentSpace, null), lift.Delta);
            }
            if (scene == "wide-bank" && i < 16)
            {
                float x = (i % 4) switch { 0 => 110.275390625f, 1 => 110.375f, 2 => 111.275390625f, _ => 111.375f };
                float y = i % 2 == 0 ? -0.36105501651763916f : -0.4560607671737671f;
                float z = i % 2 == 0 ? -236.69903564453125f : -236.79904174804688f;
                query = new(new(new(x, y + i / 8 * 0.00001f, z), 0.3f, 0.75f,
                    MovementWaterFieldTests.Space, null), new(0, i / 4 % 2 == 0 ? 0.2f : 0.4f, 0));
            }
            var result = WaterSeamComparison.Check(field, legacy, query,
                spanCapacity: i % 29 == 28 ? 0 : 64, contactCapacity: i % 31 == 30 ? 0 : 256);
            counts[result.Before.Availability] = counts.GetValueOrDefault(result.Before.Availability) + 1;
            if (result.Before.Availability != result.After.Availability)
            {
                changed++;
                output.WriteLine(JsonSerializer.Serialize(new { scene, seed, index = i, query, result },
                    new JsonSerializerOptions { IncludeFields = true }));
            }
            else if (result.Before.Availability == MovementAvailability.Known)
                Assert.Equal(0, result.Proofs);
        }
        output.WriteLine($"scene={scene}, seed={seed}, cases=128, changed={changed}, before={JsonSerializer.Serialize(counts)}");
        Assert.True(counts.GetValueOrDefault(MovementAvailability.Known) > 0);
        if (scene is "bank" or "wide-bank") Assert.True(changed > 0);
        else Assert.Equal(0, changed);
        float Next(float min, float max) => min + (max - min) * random.NextSingle();
    }

    [Fact]
    public void AnUnreachableRepresentedBedKeepsItsUnresolvedRefusal()
    {
        const int tileX = 33554434;
        var doc = new TileWorldDocument { TileSize = 0.125f };
        doc.GetOrCreateRegion(RegionCoord.Of(tileX, 0));
        doc.SetUnderlay(tileX, 0, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(tileX, 0, 0, -400);
        doc.SetCornerHeightCm(tileX + 1, 0, 0, 2);
        var field = MovementWaterFieldTests.Field(doc);
        var query = new MovementMediumSweepQuery(new(new(0, -1, -0.0625f), 0.03125f, 0.75f,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(), query, new(4194304, 0, 0));
        Assert.Equal(MovementAvailability.Unresolved, result.After.Availability);
    }

    [Theory]
    [InlineData("invalid-query", MovementAvailability.Invalid)]
    [InlineData("invalid-identity", MovementAvailability.Invalid)]
    [InlineData("short-spans", MovementAvailability.CapacityExceeded)]
    [InlineData("short-contacts", MovementAvailability.CapacityExceeded)]
    public void NonPrecisionRefusalsRemainAtomic(string kind, MovementAvailability expected)
    {
        var field = GrazingWaterBank.Capture().Medium;
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(),
            kind == "invalid-query" ? default : GrazingWaterBank.Lift(110.5f),
            spanCapacity: kind == "short-spans" ? 0 : 64,
            contactCapacity: kind == "short-contacts" ? 0 : 256,
            identity: kind == "invalid-identity" ? default(MovementQueryIdentity) : MovementWaterFieldTests.Identity);
        Assert.Equal(expected, result.After.Availability);
        Assert.Equal(0, result.After.SpansWritten);
        Assert.Equal(0, result.After.ContactsWritten);
    }

    [Fact]
    public void OrdinaryNonGrazingBankAddsNoExactSolveOrSeamProof()
    {
        var field = GrazingWaterBank.Capture().Medium;
        var result = WaterSeamComparison.Check(field, field.CaptureLegacyTraceForTests(), GrazingWaterBank.Lift(110.5f));
        Assert.Equal(MovementAvailability.Known, result.After.Availability);
        Assert.True(result.OldSolves > 0);
        Assert.Equal(result.OldSolves, result.NewSolves);
        Assert.Equal(0, result.Proofs);
        Assert.Equal(0, result.Certificates);
        output.WriteLine($"convex solves before={result.OldSolves}, after={result.NewSolves}, seam proofs={result.Proofs}");
    }

    static TileWorldDocument TwoSurfaces()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new(7, -1), new(8, -1), new(7, 0), new(8, 0));
        doc.TileSize = 0.125f;
        for (int z = -7; z <= 0; z++)
            for (int x = 508; x <= 516; x++)
            {
                doc.SetCornerHeightCm(x, z, 0, (short)(x < 512 ? -200 : x == 512 ? -100 : 0));
                if (x < 516 && z < 0) doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        doc.SetCornerHeightCm(508, -7, 0, 2);
        doc.SetCornerHeightCm(516, -7, 0, 102);
        return doc;
    }
}
