using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageOwnerTests
{
    [Fact]
    public void OwnerLocalClearance_UnpackRemapAndRemovalAreAtomic()
    {
        var fixture = FoliageFixtures.Clearance("prefab");
        byte[] beforeAssetBytes = MapPrefabFile.Write(fixture.Assets.GetPrefab("clearing-prefab"));
        var before = MapFoliageClearanceTests.Sample(fixture, -12.1f, 7.1f);
        Assert.Equal(MapFoliageReason.Solid, before.Reason);
        var unpacked = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("p"));
        var transferred = fixture with { Document = unpacked.After };
        Assert.Equal(before, MapFoliageClearanceTests.Sample(transferred, -12.1f, 7.1f));
        Assert.Equal(beforeAssetBytes, MapPrefabFile.Write(fixture.Assets.GetPrefab("clearing-prefab")));
        var extracted = MapPrefabExtraction.Extract(unpacked.After, fixture.Assets, [FoliageFixtures.Owner("p/prop")],
            new(new(-12.25f, -450, 7.125f), 0.371f, 1.2f), "extracted");
        Assert.Equal(1, extracted.PayloadVersion);
        Assert.Single(extracted.Children);
        var renamed = MapPrefabReferences.RemapWorld(unpacked.After, MapPrefabChildKind.Placement, "p/prop", "renamed");
        renamed.Placements.Single().Id = "renamed";
        Assert.Equal("renamed", renamed.Foliage!.Layers[0].OwnerSupports.Single().Owner.Id);
        Assert.Equal(before, MapFoliageClearanceTests.Sample(fixture with { Document = renamed }, -12.1f, 7.1f));
        string beforeState = MapDocumentFile.SaveText(fixture.Document);
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapRemovePrefab("p")));
        Assert.Equal(beforeState, MapDocumentFile.SaveText(fixture.Document));
        var clean = FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, owners: []));
        Assert.NotNull(MapPrefabEdits.Prepare(clean.Document, clean.Assets, new MapRemovePrefab("p")));
    }

    [Fact]
    public void ClearanceBindings_ResolveCanonicalSurfaceRatherThanTextIds()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        var floorPatch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        var anchor = floorPatch.Key;
        floorPatch.Records.Add(new MapSpaceDoc("shared", MapSpaceKind.Exterior, null, null, [], [], [], []));
        floorPatch.Records.Add(new MapSpaceFootprint("domain", new("shared", anchor), anchor,
            Enumerable.Range(0, floorPatch.Depth).SelectMany(z => Enumerable.Range(0, floorPatch.Width).Select(x => (z + floorPatch.CellMinZ) * 64 + x + floorPatch.CellMinX)).ToArray(),
            new(MapBoundKind.SupportFloor, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        var space = new MapSupportBinding(MapSupportBindingKind.Space, null, "shared", 20.2f, 1, 0.5f);
        var placement = fixture.Document.Placements[0];
        var surfaceBound = MapFoliageClearanceTests.Sample(fixture, 0.1f, -0.1f);
        placement.SupportBinding = space;
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, support: space, indoors: false));
        // The Surface variant also disables indoor rejection to isolate canonical clearance.
        var spaceBound = MapFoliageClearanceTests.Sample(fixture, 0.1f, -0.1f);
        Assert.Equal(MapFoliageReason.Solid, spaceBound.Reason);
        Assert.Equal(surfaceBound, spaceBound);
        placement.Y = 100;
        placement.SupportBinding = null;
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            owners: [new(FoliageFixtures.Owner("prop"), space)]));
        Assert.Equal(spaceBound, MapFoliageClearanceTests.Sample(fixture, 0.1f, -0.1f));
        Assert.Equal("floor-1", spaceBound.SupportOwnerId);
        Assert.Equal("shared", spaceBound.OccupiedSpaceId);
        var remapped = MapFoliageReferences.Remap(fixture.Document.Foliage!,
            new System.Collections.Generic.Dictionary<MapPrefabReference, MapPrefabReference>
            { [FoliageFixtures.Owner("shared", MapPrefabChildKind.Surface)] = FoliageFixtures.Owner("changed", MapPrefabChildKind.Surface) });
        Assert.Equal("shared", remapped.Layers[0].Support.SpaceId);
    }
    [Fact]
    public void TypedMasks_SurfaceRecordAndMarkerOwnersUseTheSameCanonicalGate()
    {
        var fixture = FoliageFixtures.Clearance("typed");
        var patch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        patch.Records.Add(new MapSpaceDoc("shared", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("shared-domain", new("shared", patch.Key), patch.Key,
            [0, 1, 64, 65], new(MapBoundKind.SupportFloor, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        fixture.Document.BuildingIntent = new(1, [], [], [], [new(1, "shared", "Shared", System.Numerics.Vector3.Zero, 0, "fixture", true, [])]);
        var space = new MapSupportBinding(MapSupportBindingKind.Space, null, "shared", 20.2f, 1, 0.5f);
        foreach (var kind in new[] { MapPrefabChildKind.Surface, MapPrefabChildKind.Record, MapPrefabChildKind.Marker })
        {
            var owner = FoliageFixtures.Owner("shared", kind);
            FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, rules: [],
                owners: [new(owner, space)], masks: [new("mask", owner, space, MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))]));
            Assert.Equal(MapFoliageReason.Solid, MapFoliageClearanceTests.Sample(fixture, 0.1f, -0.1f).Reason);
            var remapped = MapFoliageReferences.Remap(fixture.Document.Foliage!,
                new System.Collections.Generic.Dictionary<MapPrefabReference, MapPrefabReference>
                { [FoliageFixtures.Owner("shared", MapPrefabChildKind.Surface)] = FoliageFixtures.Owner("renamed", MapPrefabChildKind.Surface) });
            Assert.Equal(kind == MapPrefabChildKind.Surface ? "renamed" : "shared", remapped.Layers[0].ExceptionMasks[0].Owner.Id);
            Assert.Equal("shared", remapped.Layers[0].ExceptionMasks[0].Support.SpaceId);
        }
    }

    [Fact]
    public void OverridesRemovalAndRestoration_PreserveOwnerRulesAndAtomicRefusal()
    {
        var fixture = FoliageFixtures.Clearance("prefab");
        var key = new MapPrefabChildKey(MapPrefabChildKind.Placement, "prop");
        var child = (MapPrefabPlacementChild)fixture.Assets.GetPrefab("clearing-prefab").Children.Single();
        var moved = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets,
            new MapSetPrefabOverride("p", new MapPrefabReplaceOverride(key, child with
            {
                Value = new MapPlacement
                { Id = "prop", Kind = "tree", AssetId = "tree", X = 3, Y = 100, Z = 0, Scale = 1, Tags = ["clear"] }
            })));
        Assert.Equal(MapFoliageReason.Accepted, MapFoliageClearanceTests.Sample(fixture with { Document = moved.After }, -12.1f, 7.1f).Reason);
        string state = MapDocumentFile.SaveText(fixture.Document);
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(fixture.Document, fixture.Assets,
            new MapSetPrefabOverride("p", new MapPrefabRemoveOverride(key))));
        Assert.Equal(state, MapDocumentFile.SaveText(fixture.Document));
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l, owners: []));
        var removed = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapSetPrefabOverride("p", new MapPrefabRemoveOverride(key)));
        var restored = MapPrefabEdits.Prepare(removed.After, fixture.Assets, new MapRestorePrefabChild("p", key));
        FoliageFixtures.WithClearance(fixture with { Document = restored.After }, l => FoliageFixtures.ClearanceLayer(l,
            owners: [new(FoliageFixtures.Owner("p/prop"), FoliageFixtures.Surface("floor-1"))]));
        Assert.Equal(MapFoliageReason.Solid, MapFoliageClearanceTests.Sample(fixture with { Document = restored.After }, -12.1f, 7.1f).Reason);
    }

    [Fact]
    public void RootSupportReferences_ValidateBeforeAnyCosmeticPreparation()
    {
        var fixture = FoliageFixtures.Clearance("solid");
        FoliageFixtures.WithClearance(fixture, l => FoliageFixtures.ClearanceLayer(l,
            masks: [new("orphan", FoliageFixtures.Owner("prop"), FoliageFixtures.Surface("missing"),
                MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))]));
        Assert.Contains(MapFoliageValidation.ValidateBound(fixture.Document, fixture.Assets), e => e.Contains("missing", StringComparison.Ordinal));
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(fixture.Document, fixture.Assets));
    }

    [Fact]
    public void RotatedNonSquareFootprint_UsesTheEffectivePoseOnceAcrossUnpack()
    {
        var fixture = FoliageFixtures.Clearance("prefab");
        (float X, float Z, bool Solid)[] cells = [(-12.5f, 7.5f, true), (-11.5f, 7.5f, true),
            (-12.5f, 8.5f, false), (-12.5f, 6.5f, true), (-11.5f, 8.5f, true), (-11.5f, 9.5f, false)];
        var before = cells.Select(c => MapFoliageClearanceTests.Sample(fixture, c.X, c.Z)).ToArray();
        for (int i = 0; i < cells.Length; i++) Assert.Equal(cells[i].Solid ? MapFoliageReason.Solid : MapFoliageReason.Accepted, before[i].Reason);
        var unpacked = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("p"));
        var after = cells.Select(c => MapFoliageClearanceTests.Sample(fixture with { Document = unpacked.After }, c.X, c.Z)).ToArray();
        Assert.Equal(before, after);
    }

}
