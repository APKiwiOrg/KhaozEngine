using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageClearanceTests
{
    internal static MapPreparedFoliageClearance Prepare(FoliageFixture fixture)
    {
        var captured = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets);
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        var context = captured.Context!;
        var result = MapFoliageClearance.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new());
        Assert.True(result.Status == MapFoliageStatus.Complete, result.Detail);
        return result;
    }

    internal static MapFoliageSurfacePoint Sample(FoliageFixture fixture, float x, float z)
    {
        MapBoundDocumentValidation.Validate(fixture.Document, fixture.Assets);
        var context = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets).Context!;
        if (context is null) { _ = KhaozEngine.MapDoc.Prefabs.MapPrefabResolver.Expand(fixture.Document, fixture.Assets); Assert.Fail("capture refused"); }
        var layer = context!.Foliage!.Layers[0];
        var point = MapFoliageSupport.Prepare(context, layer, new(0, 0), new()).Sample(x, z);
        return MapFoliageClearance.Prepare(context, layer, new(0, 0), new()).Evaluate(point, x, z);
    }

    [Fact]
    public void NewBoundPlacement_UsesLayerRulesWithoutAnOwnerMask()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        Assert.Empty(fixture.Document.Foliage!.Layers[0].OwnerSupports);
        Assert.Equal(MapFoliageReason.Solid, Sample(fixture, 0.1f, 0.1f).Reason);
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, 2.5f, 0.5f).Reason);
        fixture.Document.Placements[0].Tags = ["other"];
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, 0.1f, 0.1f).Reason);
        fixture.Document.Placements[0].Tags = ["clear"];
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, solid: false));
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, 0.1f, 0.1f).Reason);
    }

    [Fact]
    public void DoorDistance_IsStrictAndFourNeighbourFadeUsesCentreEligibility()
    {
        var fixture = FoliageFixtures.Clearance("door");
        Assert.Equal(MapFoliageReason.Door, Sample(fixture, 0.75f - 0.0001f, 0).Reason);
        Assert.NotEqual(MapFoliageReason.Door, Sample(fixture, 0.75f, 0).Reason);
        Assert.NotEqual(MapFoliageReason.Door, Sample(fixture, 0.75f + 0.0001f, 0).Reason);
        fixture.Document.Placements.Clear();
        var patch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        // East cell is ineligible only at its centre through authored overlay paint. The current cell remains allowed.
        patch.Cells[0 * patch.Width + 1] = patch.Cells[1] with { Overlay = 6, Cut = MapOverlayCut.Full };
        float originalDensity = Sample(fixture, 0.5f, -0.5f).Density;
        float halfRimDensity = Sample(fixture, 0.875f, -0.5f).Density;
        Assert.Equal(1f, originalDensity);
        Assert.Equal(0.5f, halfRimDensity / originalDensity);
        Assert.Equal(MapFoliageReason.MaterialNotAllowed, Sample(fixture, 1f, -0.5f).Reason);
    }

    [Fact]
    public void RoofOrdering_IsOrdinalRatherThanGeometric()
    {
        var fixture = FoliageFixtures.Clearance("roof");
        Assert.Equal(MapFoliageReason.RoofAbove, Sample(fixture, -4.1f, 0.1f).Reason);
        Assert.NotEqual(MapFoliageReason.RoofAbove, Sample(fixture, 4.1f, 0.1f).Reason);
        Assert.Equal(20f, Sample(fixture, -4.1f, 0.1f).Height);
    }

    [Fact]
    public void OneRule_MixedSeatedOrdinalsApplyPerOwner()
    {
        var fixture = FoliageFixtures.Clearance("roof");
        Assert.Single(fixture.Document.Foliage!.Layers[0].ClearanceRules);
        Assert.Equal(MapFoliageReason.RoofAbove, Sample(fixture, -4.1f, 0.1f).Reason);
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, 4.1f, 0.1f).Reason);
        var before = Prepare(fixture);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: l.CompatibilityOrdinals.Select(r => r.SurfaceId == "unrelated" ? r with { Ordinal = 7 } : r).ToArray()));
        Assert.Equal(before.DependencyParts, Prepare(fixture).DependencyParts);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: l.CompatibilityOrdinals.Select(r => r.SurfaceId == "roof-seat" ? r with { Ordinal = 1 } : r).ToArray()));
        Assert.NotEqual(before.DependencyParts, Prepare(fixture).DependencyParts);
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, -4.1f, 0.1f).Reason);
    }

    [Fact]
    public void HigherRoof_SolidAndDoorNeverBorrowClearingTarget()
    {
        foreach (var kind in new[] { MapFoliageClearanceKind.Solid, MapFoliageClearanceKind.Door })
        {
            var fixture = FoliageFixtures.Clearance("mixed");
            foreach (var placement in fixture.Document.Placements) { placement.Y = 100; placement.SupportBinding = null; }
            FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
                rules: l.ClearanceRules.Select(r => r with { Kind = kind }).ToArray(),
                owners: [new(FoliageFixtures.Owner("higher"), FoliageFixtures.Surface("roof-seat")),
                    new(FoliageFixtures.Owner("peer"), FoliageFixtures.Surface("peer-seat"))],
                masks: [new("roof", FoliageFixtures.Owner("higher"), FoliageFixtures.Surface("floor-1"),
                    MapFoliageClearanceKind.RoofAbove, new(-0.5f, -0.5f, 0.5f, 0.5f))]));
            var isolated = FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, masks: []));
            Assert.Equal(MapFoliageReason.Accepted, Sample(isolated, -4.1f, 0.1f).Reason);
            Assert.Equal(kind == MapFoliageClearanceKind.Solid ? MapFoliageReason.Solid : MapFoliageReason.Door,
                Sample(isolated, 4.1f, 0.1f).Reason);
        }
        var layer = FoliageFixtures.Clearance("roof").Document.Foliage!.Layers[0];
        Assert.True(MapFoliageApplicability.Applies(layer, "peer-seat", "floor-1", MapFoliageClearanceKind.Solid));
        Assert.True(MapFoliageApplicability.Applies(layer, "peer-seat", "floor-1", MapFoliageClearanceKind.Door));
        Assert.True(MapFoliageApplicability.Applies(layer, "roof-seat", "floor-1", MapFoliageClearanceKind.RoofAbove));
        Assert.False(MapFoliageApplicability.Applies(layer, "roof-seat", "floor-1", MapFoliageClearanceKind.Solid));
        Assert.False(MapFoliageApplicability.Applies(layer, "roof-seat", "floor-1", MapFoliageClearanceKind.Door));
        Assert.False(MapFoliageApplicability.Applies(layer, "peer-seat", "floor-1", MapFoliageClearanceKind.RoofAbove));
    }

    [Fact]
    public void UnmappedSeatedSupport_UsesCanonicalFallbackAndNeverRoof()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, ordinals: []));
        var layer = fixture.Document.Foliage!.Layers[0];
        foreach (var kind in new[] { MapFoliageClearanceKind.Solid, MapFoliageClearanceKind.Door })
        {
            Assert.True(MapFoliageApplicability.Applies(layer, "floor-1", "floor-1", kind));
            Assert.False(MapFoliageApplicability.Applies(layer, "roof-seat", "floor-1", kind));
        }
        Assert.False(MapFoliageApplicability.Applies(layer, "floor-1", "floor-1", MapFoliageClearanceKind.RoofAbove));
        Assert.Equal(MapFoliageReason.Solid, Sample(fixture, 0.1f, 0.1f).Reason);
        var before = Prepare(fixture);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, ordinals: [new("floor-1", 1)]));
        Assert.NotEqual(before.DependencyParts, Prepare(fixture).DependencyParts);
        Assert.False(MapFoliageApplicability.Applies(fixture.Document.Foliage!.Layers[0], "floor-1", "roof-seat", MapFoliageClearanceKind.Solid));
    }
    [Theory]
    [InlineData("solid")]
    [InlineData("roof")]
    [InlineData("door")]
    [InlineData("edge-fade")]
    public void ReleasedClearance_PreservesEveryPinnedSampleBit(string variant)
    {
        var expected = new System.Collections.Generic.Dictionary<string, uint[][]>
        {
            ["solid"] = [[0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u], [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u], [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u]],
            ["roof"] = [[0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u], [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u], [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u]],
            ["door"] = [[0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u], [0x3D23D79Cu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F3u, 0x00000000u], [0x3F19999Au, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27F0u, 0x3F5ADADBu]],
            ["edge-fade"] = [[0x3E2E1486u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F1u, 0x3E3C93CEu], [0x3DA3D6CEu, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3DB4D7FAu], [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F65E5E6u], [0x3E9EB852u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x3F637D16u]],
        };
        (float X, float Z)[] points = variant == "door" ? [(30.5f, -30.5f), (31.7f, -30.5f), (32.5f, -30.5f)] :
            variant == "edge-fade" ? [(30.9f, -30.5f), (30.5f, -30.95f), (30.5f, -30.5f), (30.1f, -30.1f)] :
            [(30.5f, -30.5f), (30.2f, -30.8f), (33.5f, -30.5f)];
        var fixture = FoliageFixtures.ClearanceReleased(variant);
        for (int i = 0; i < points.Length; i++)
        {
            var p = Sample(fixture, points[i].X, points[i].Z);
            Assert.Equal(MapFoliageStatus.Complete, p.Status);
            Assert.Equal(expected[variant][i], new[] { p.Height, p.Normal.X, p.Normal.Y, p.Normal.Z, p.Density }
                .Select(BitConverter.SingleToUInt32Bits).ToArray());
        }
    }

    [Fact]
    public void NestedRooms_RejectIndoorsThroughSharedMembership()
    {
        var fixture = FoliageFixtures.NestedClearance();
        Assert.Equal(MapFoliageReason.Indoor, Sample(fixture, 0.5f, -0.5f).Reason);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, indoors: false));
        var point = Sample(fixture, 0.5f, -0.5f);
        Assert.Equal(MapFoliageReason.Solid, point.Reason);
        Assert.Equal("floor-1", point.SupportOwnerId);
        Assert.Equal("nested", point.OccupiedSpaceId);
    }

    [Fact]
    public void CapacityRefusal_PublishesNoUsableClearance()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        var context = FoliageFixtures.Context(fixture);
        var prepared = MapFoliageClearance.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new(MaxRuleCandidates: 0));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, prepared.Status);
        Assert.Empty(prepared.DependencyParts);
        Assert.Equal(1, prepared.Work.RuleCandidates);
        var point = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new()).Sample(0.1f, 0.1f);
        var refused = prepared.Evaluate(point, 0.1f, 0.1f);
        Assert.Equal((MapFoliageStatus.CapacityExceeded, MapFoliageReason.Refused, 0f), (refused.Status, refused.Reason, refused.Density));
        Assert.Null(refused.Face);
        Assert.Null(refused.SupportOwnerId);
    }

    [Fact]
    public void MaskBudget_OnlyChargesThePreparedLayer()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        var layer = FoliageFixtures.ClearanceLayer(fixture.Document.Foliage!.Layers[0], rules: []);
        var other = FoliageFixtures.Layer("cover-b", layer.Support, -16, -16, 1, 32, 32,
            Enumerable.Repeat((byte)255, 32 * 32).ToArray(), [10]);
        other = FoliageFixtures.ClearanceLayer(other,
            masks: [new("other-mask", FoliageFixtures.Owner("prop"), layer.Support,
                MapFoliageClearanceKind.Solid, new(-0.5f, -1.5f, 0.5f, 1.5f))]);
        fixture.Document.Foliage = new(1, [layer, other]);
        var context = FoliageFixtures.Context(fixture);
        layer = context.Foliage!.Layers[0];
        other = context.Foliage.Layers[1];
        var prepared = MapFoliageClearance.Prepare(context, layer, new(0, 0), new(MaxMaskCandidates: 0));
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);
        Assert.Equal(0, prepared.Work.MaskCandidates);
        var point = MapFoliageSupport.Prepare(context, layer, new(0, 0), new()).Sample(0.1f, 0.1f);
        Assert.Equal(MapFoliageReason.Accepted, prepared.Evaluate(point, 0.1f, 0.1f).Reason);
        var masked = MapFoliageClearance.Prepare(context, other, new(0, 0), new(MaxMaskCandidates: 1));
        Assert.Equal(MapFoliageStatus.Complete, masked.Status);
        Assert.Equal(1, masked.Work.MaskCandidates);
        var otherPoint = MapFoliageSupport.Prepare(context, other, new(0, 0), new()).Sample(0.1f, 0.1f);
        Assert.Equal(MapFoliageReason.Solid, masked.Evaluate(otherPoint, 0.1f, 0.1f).Reason);
        Assert.Equal(MapFoliageStatus.CapacityExceeded,
            MapFoliageClearance.Prepare(context, other, new(0, 0), new(MaxMaskCandidates: 0)).Status);
    }

    [Theory]
    [InlineData(-1, 0, 0.125f, -0.5f)]
    [InlineData(1, 0, 0.875f, -0.5f)]
    [InlineData(0, -1, 0.5f, -0.125f)]
    [InlineData(0, 1, 0.5f, -0.875f)]
    public void EachCardinalRim_UsesWorldMetres(int dx, int dz, float x, float z)
    {
        var fixture = FoliageFixtures.Clearance("solid");
        fixture.Document.Placements.Clear();
        var key = MapPatchKey.ForCell("floor-1", dx, dz);
        var patch = fixture.Document.Surfaces.Patches[key];
        int localX = (int)(dx - key.SlotX * 64) - patch.CellMinX;
        int localZ = (int)(dz - key.SlotZ * 64) - patch.CellMinZ;
        int index = localZ * patch.Width + localX;
        patch.Cells[index] = patch.Cells[index] with { Underlay = 0 };
        Assert.Equal(0.5f, Sample(fixture, x, z).Density);
    }

    [Fact]
    public void DoorClearance_ScalesExactlyOnce()
    {
        var fixture = FoliageFixtures.Clearance("door");
        fixture.Document.Placements[0].Scale = 1.2f;
        float radius = 0.75f * 1.2f;
        Assert.Equal(MapFoliageReason.Door, Sample(fixture, radius - 0.0001f, 0).Reason);
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, radius, 0).Reason);
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, radius + 0.0001f, 0).Reason);
    }

    [Theory]
    [InlineData(MapFoliageClearanceKind.Solid, MapFoliageReason.Accepted, MapFoliageReason.Solid)]
    [InlineData(MapFoliageClearanceKind.Door, MapFoliageReason.Accepted, MapFoliageReason.Door)]
    [InlineData(MapFoliageClearanceKind.RoofAbove, MapFoliageReason.RoofAbove, MapFoliageReason.Accepted)]
    public void ExceptionMasks_KeepEachOwnersSeatSeparateFromTheirSharedTarget(MapFoliageClearanceKind kind,
        MapFoliageReason higher, MapFoliageReason peer)
    {
        var fixture = FoliageFixtures.Clearance("mixed");
        foreach (var p in fixture.Document.Placements) { p.Y = 100; p.SupportBinding = null; }
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, rules: [],
            owners: [new(FoliageFixtures.Owner("higher"), FoliageFixtures.Surface("roof-seat")),
                new(FoliageFixtures.Owner("peer"), FoliageFixtures.Surface("peer-seat"))],
            masks: [new("higher-mask", FoliageFixtures.Owner("higher"), FoliageFixtures.Surface("floor-1"), kind, new(-0.5f, -1.5f, 0.5f, 1.5f)),
                new("peer-mask", FoliageFixtures.Owner("peer"), FoliageFixtures.Surface("floor-1"), kind, new(-0.5f, -1.5f, 0.5f, 1.5f))]));
        Assert.Equal(higher, Sample(fixture, -4.1f, 0.1f).Reason);
        Assert.Equal(peer, Sample(fixture, 4.1f, 0.1f).Reason);
    }

    [Fact]
    public void SpaceBoundLayer_UsesItsSelectedSurfaceOrdinal()
    {
        var fixture = FoliageFixtures.Clearance("roof");
        var patch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceDoc("level", KhaozEngine.MapDoc.Spaces.MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceFootprint("level-domain", new("level", patch.Key), patch.Key,
            Enumerable.Range(0, patch.Depth).SelectMany(z => Enumerable.Range(0, patch.Width).Select(x => z * 64 + x)).ToArray(),
            new(KhaozEngine.MapDoc.Spaces.MapBoundKind.SupportFloor, "floor-1", null), new(KhaozEngine.MapDoc.Spaces.MapBoundKind.OpenTop, null, null)));
        fixture.Document.Placements[0].X = 4;
        fixture.Document.Placements[1].X = 8;
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            support: new(MapSupportBindingKind.Space, null, "level", 20.2f, 1, 0.5f)));
        var higher = Sample(fixture, 4.1f, -0.1f);
        var peer = Sample(fixture, 8.1f, -0.1f);
        Assert.Equal(MapFoliageReason.RoofAbove, higher.Reason);
        Assert.Equal(MapFoliageReason.Accepted, peer.Reason);
        var before = Prepare(fixture);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: l.CompatibilityOrdinals.Select(r => r.SurfaceId == "unrelated" ? r with { Ordinal = 7 } : r).ToArray()));
        Assert.Equal(higher, Sample(fixture, 4.1f, -0.1f));
        Assert.Equal(peer, Sample(fixture, 8.1f, -0.1f));
        Assert.Equal(before.DependencyParts, Prepare(fixture).DependencyParts);
    }

    [Fact]
    public void SpaceBoundLayer_HashesItsSelectedSurfaceOrdinal()
    {
        var fixture = FoliageFixtures.Clearance("roof");
        var patch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceDoc("level", KhaozEngine.MapDoc.Spaces.MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceFootprint("level-domain", new("level", patch.Key), patch.Key,
            Enumerable.Range(0, patch.Depth).SelectMany(z => Enumerable.Range(0, patch.Width).Select(x => z * 64 + x)).ToArray(),
            new(KhaozEngine.MapDoc.Spaces.MapBoundKind.SupportFloor, "floor-1", null), new(KhaozEngine.MapDoc.Spaces.MapBoundKind.OpenTop, null, null)));
        fixture.Document.Placements[0].X = 4;
        fixture.Document.Placements[1].X = 8;
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            support: new(MapSupportBindingKind.Space, null, "level", 20.2f, 1, 0.5f)));
        var before = Prepare(fixture);
        Assert.Contains("ordinal|floor-1|1", before.DependencyParts);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: l.CompatibilityOrdinals.Select(r => r.SurfaceId == "floor-1" ? r with { Ordinal = 4 } : r).ToArray()));
        Assert.NotEqual(before.DependencyParts, Prepare(fixture).DependencyParts);
    }

    [Theory]
    [InlineData(false, "floor-1", 4.1f, -0.1f)]
    [InlineData(true, "p/deck", 8.1f, -8.1f)]
    public void SpaceBoundLegacyFallback_AtTheBandEdgeKeepsItsSurfaceOrdinal(bool posed, string surface, float x, float z)
    {
        var fixture = posed ? FoliageFixtures.LegacyBandPosed() : FoliageFixtures.LegacyBand();
        var context = FoliageFixtures.Context(fixture);
        var layer = context.Foliage!.Layers[0];
        var target = MapFoliageSupport.Prepare(context, layer, new(0, 0), new()).Target(layer.Support, x, z);
        Assert.Equal((KhaozEngine.MapDoc.Support.MapSupportStatus.LegacyFallback, surface), (target.Status, target.Compatibility?.Patch.SurfaceId));
        var parts = Prepare(fixture).DependencyParts;
        var point = Sample(fixture, x, z);
        Assert.Equal((true, surface, MapFoliageReason.RoofAbove),
            (parts.Contains("ordinal|" + surface + "|1"), point.SupportOwnerId, point.Reason));
    }

    [Fact]
    public void EdgeFade_ReadsANeighbourCentreAcrossTheChunkAndPatchEdge()
    {
        var point = Sample(FoliageFixtures.EdgeChunk(0.25f), 0.1f, 0.5f);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted, "coarse"), (point.Status, point.Reason, point.SupportOwnerId));
        // The void west neighbour fades by its distance 0.1 m over the 0.25 m edge.
        Assert.Equal(0.4f, point.Density, 0.0001f);
    }

    [Fact]
    public void EdgeFade_PosedLargeCellsKeepNeighbourCentresInTheContext()
    {
        var fixture = FoliageFixtures.LegacyBandPosed();
        Assert.Equal(0.25f, fixture.Document.Foliage!.Layers[0].EdgeFade);
        fixture.Document.Placements.RemoveAt(0);
        var point = Sample(fixture, 8.1f, -8.1f);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted, "p/deck"), (point.Status, point.Reason, point.SupportOwnerId));
    }

    [Fact]
    public void EdgeFade_WidensAndChargesOnlyItsOwnContext()
    {
        MapPreparedFoliageSupport Prepared(float edge, MapFoliageLimits limits)
        {
            var context = FoliageFixtures.Context(FoliageFixtures.EdgeChunk(edge));
            return MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), limits);
        }
        var off = Prepared(0, new());
        // Measured at f55789fb0, before the edge fade reach existed.
        Assert.Equal((MapFoliageStatus.Complete, 9112L), (off.Status, off.Work.Faces));
        Assert.DoesNotContain(off.DependencyParts, p => p.StartsWith("context-reach|", StringComparison.Ordinal));
        var on = Prepared(0.25f, new());
        Assert.True(on.Work.Faces > off.Work.Faces, $"{on.Work.Faces} faces");
        Assert.Contains(on.DependencyParts, p => p.StartsWith("context-reach|", StringComparison.Ordinal));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, Prepared(0.25f, new(MaxFaces: (int)on.Work.Faces - 1)).Status);
        Assert.Equal(MapFoliageStatus.Complete, Prepared(0.25f, new(MaxFaces: (int)on.Work.Faces)).Status);
    }

    [Fact]
    public void UnrelatedCave_WithoutCompatibilityProvenanceNeverClears()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        KhaozEngine.Tests.MapDoc.Water.WaterFixtures.Surface(fixture.Document.Surfaces, "cave-lid", MapSurfaceRole.Ceiling,
            MapLatticeFrame.ImportedMetreCentimetre, 0, 0, 2, 2, (_, _) => 5100);
        foreach (var lid in fixture.Document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "cave-lid"))
            for (int i = 0; i < lid.Cells.Length; i++) lid.Cells[i] = lid.Cells[i] with { Underlay = 10 };
        int lidIndex = fixture.Document.Surfaces.Refs.FindIndex(s => s.Id == "cave-lid");
        var lidRef = fixture.Document.Surfaces.Refs[lidIndex];
        fixture.Document.Surfaces.Refs[lidIndex] = lidRef with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(lidRef,
            fixture.Document.Surfaces.Patches.Where(p => p.Key.SurfaceId == "cave-lid")
                .Select(p => new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
        };
        var patch = fixture.Document.Surfaces.Patches[new("unrelated", 0, 0)];
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceDoc("cave", KhaozEngine.MapDoc.Spaces.MapSpaceKind.Cave, null, null, [], [], [], []));
        patch.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceFootprint("cave-domain", new("cave", patch.Key), patch.Key,
            [0, 1, 64, 65], new(KhaozEngine.MapDoc.Spaces.MapBoundKind.SupportFloor, "unrelated", null),
            new(KhaozEngine.MapDoc.Spaces.MapBoundKind.Ceiling, "cave-lid", null)));
        fixture.Document.Placements[0].SupportBinding = new(MapSupportBindingKind.Space, null, "cave", 50.2f, 1, 0.5f);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: [new("floor-1", 1)], rules: [new("roof", "tree", "clear", MapFoliageClearanceKind.RoofAbove, new(-1, -1, 1, 1))]));
        Assert.Equal(MapFoliageReason.Accepted, Sample(fixture, 0.1f, -0.1f).Reason);
    }

    [Fact]
    public void ExplicitHeightRoof_RefusesWithoutASeatedCorrespondence()
    {
        var fixture = FoliageFixtures.NestedClearance();
        var prop = fixture.Document.Placements[0];
        prop.Y = 100;
        prop.SupportBinding = null;
        var owner = FoliageFixtures.Owner("prop");
        var intent = fixture.Document.BuildingIntent!;
        fixture.Document.BuildingIntent = new(1, [new("link", owner, [FoliageFixtures.Owner("nested", KhaozEngine.MapDoc.Prefabs.MapPrefabChildKind.Record)], [], 25)], [], [], [])
        { Storeys = intent.Storeys, Roofs = [owner] };
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, indoors: false));
        var context = FoliageFixtures.Context(fixture);
        var prepared = MapFoliageClearance.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new());
        Assert.Equal(MapFoliageStatus.MissingSupport, prepared.Status);
        Assert.Empty(prepared.DependencyParts);
    }

    [Fact]
    public void NoMatchedOwners_ConsultsNoCompatibilityRows()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        fixture.Document.Placements.Clear();
        var before = Prepare(fixture);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            ordinals: l.CompatibilityOrdinals.Select(r => r with { Ordinal = r.Ordinal + 1 }).ToArray()));
        Assert.Equal(before.DependencyParts, Prepare(fixture).DependencyParts);
    }

    [Fact]
    public void LegacyNoDrawOwnerAndMaskTargets_KeepLogicalCanonicalSupport()
    {
        var fixture = FoliageFixtures.ClearanceReleased("solid");
        var owner = fixture.Document.Placements.Single();
        owner.Y = 0;
        owner.SupportBinding = null;
        var patch = fixture.Document.Surfaces.Patches[new(FoliageFixtures.GroundId, 0, 0)];
        patch.Cells[30 * 64 + 30] = patch.Cells[30 * 64 + 30] with { Flags = MapCellFlags.NoDraw | MapCellFlags.Blocked };
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            owners: [new(FoliageFixtures.Owner(owner.Id), FoliageFixtures.Surface(FoliageFixtures.GroundId))]));
        Assert.Equal(MapFoliageReason.Solid, Sample(fixture, 30.5f, -30.5f).Reason);
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, rules: [],
            masks: [new("logical", FoliageFixtures.Owner(owner.Id), FoliageFixtures.Surface(FoliageFixtures.GroundId),
                MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f))]));
        Assert.Equal(MapFoliageReason.Solid, Sample(fixture, 30.5f, -30.5f).Reason);
    }

}
