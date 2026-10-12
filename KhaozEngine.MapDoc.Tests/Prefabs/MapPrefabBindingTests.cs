using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabBindingTests
{
    static string Identity(PrefabFixture f) => MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options());
    static string StoredIdentity(PrefabFixture f) =>
        MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(f.Document), f.Assets, PrefabFixtures.Options());

    [Theory]
    [InlineData(MapPrefabChildKind.Placement)]
    [InlineData(MapPrefabChildKind.Surface)]
    [InlineData(MapPrefabChildKind.Record)]
    [InlineData(MapPrefabChildKind.Storey)]
    [InlineData(MapPrefabChildKind.WaterMedium)]
    [InlineData(MapPrefabChildKind.WaterBody)]
    [InlineData(MapPrefabChildKind.Light)]
    [InlineData(MapPrefabChildKind.Marker)]
    [InlineData(MapPrefabChildKind.RoofLink)]
    [InlineData(MapPrefabChildKind.RoofGroup)]
    public void EveryChildKind_RetainsReservationsAndTransfersOnce(MapPrefabChildKind kind)
    {
        var f = PrefabFixtures.Create("all-kinds");
        var asset = MapPrefabFile.Read(f.Assets.GetResource("hut").Bytes, "hut");
        var before = f.Document.Prefabs!.Instances.Single().Bindings;
        var removed = PrefabFixtures.WithChildRemoved(asset, kind);
        var dead = MapPrefabBindingPlanner.Plan(f.Document, removed, f.Assets, "a", before);
        var restored = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "a", dead.Bindings);
        Assert.Equal(before.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)),
            restored.Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)));
        Assert.Equal(f.Document.NumericIdHighWaterMark, restored.HighWaterMark);
        Assert.Contains(dead.Bindings.Entries, b => b.Child.Kind == kind && b.Removed);
        Assert.Equal(before.Entries.Count, dead.Bindings.Entries.Count);
        Assert.All(restored.Bindings.Entries, b => Assert.False(b.Removed));
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("duplicate-world-id")]
    [InlineData("standalone-collision")]
    [InlineData("dangling-transfer")]
    [InlineData("retired-live")]
    [InlineData("numeric-duplicate")]
    public void MissingOrDuplicateBindings_RefuseOnBothPaths(string variant)
    {
        var f = PrefabFixtures.Create(variant);
        long mark = f.Document.NumericIdHighWaterMark;
        Assert.Throws<MapDocumentException>(() => Identity(f));
        Assert.Throws<MapDocumentException>(() => StoredIdentity(f));
        Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.Validate(f.Document, f.Assets));
        Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
    }

    [Fact]
    public void ActiveTransfer_HasOneStandaloneOwnerOnBothPaths()
    {
        var f = PrefabFixtures.Create("active-transfer");
        Assert.Equal(Identity(f), StoredIdentity(f));
        f.Document.Placements.Single().NumericId = null;
        Assert.Throws<MapDocumentException>(() => Identity(f));
    }

    [Fact]
    public void ReorderingAndRepacking_PreserveIdentity()
    {
        var f = PrefabFixtures.Create("active-transfer");
        string expected = Identity(f);
        var instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { Bindings = new(instance.Bindings.Entries.Reverse().ToArray()) } },
            f.Document.Prefabs.Transfers.Reverse().ToArray());
        Assert.Equal(expected, Identity(f));
        Assert.Equal(expected, MapAuthoredIdentityV2.Compute(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets, PrefabFixtures.Options()));
        string first = PrefabFixtures.TempDirectory(), second = PrefabFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(f.Document, first);
            var tiled = MapDocumentFile.LoadTiled(first);
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(tiled, f.Assets, PrefabFixtures.Options()));
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(KhaozEngine.MapDoc.Storage.MapStoredSurfaceSource.Open(first), f.Assets, PrefabFixtures.Options()));
            MapDocumentFile.SaveTiled(tiled, second);
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(KhaozEngine.MapDoc.Storage.MapStoredSurfaceSource.Open(second), f.Assets, PrefabFixtures.Options()));
        }
        finally { Directory.Delete(first, true); Directory.Delete(second, true); }
        var asset = f.Assets.GetPrefab("hut");
        var shuffled = asset with
        {
            Children = asset.Children.Reverse().Select(c => c with { References = c.References.Reverse().ToArray() }).ToArray(),
        };
        Assert.Equal(MapPrefabFile.Write(asset), MapPrefabFile.Write(shuffled));
    }

    [Fact]
    public void NumericExhaustion_PublishesNothing()
    {
        var f = PrefabFixtures.Create();
        f.Document.NumericIdHighWaterMark = long.MaxValue;
        string before = MapDocumentFile.SaveText(f.Document);
        var asset = f.Assets.GetPrefab("hut");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "b", new([])));
        Assert.Contains("exhaust", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var unnumbered = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "b", new([]), allocateNumericPlacements: false);
        Assert.All(unnumbered.Bindings.Entries, b => Assert.Null(b.NumericId));
        Assert.Equal(long.MaxValue, unnumbered.HighWaterMark);
    }

    [Fact]
    public void NewBindings_UseTheEscapedTypedGrammarAboveTheMark()
    {
        var f = PrefabFixtures.Create("attached");
        var bindings = f.Document.Prefabs!.Instances.Single().Bindings.Entries.ToDictionary(b => b.Child);
        Assert.Equal("prefab/a/Light/deck%2Fglow-light%2Fglow", bindings[new(MapPrefabChildKind.Light, "deck/glow-light/glow")].WorldId);
        Assert.Equal("prefab/a/Surface/deck%2Fdeck-support%2Ftop", bindings[new(MapPrefabChildKind.Surface, "deck/deck-support/top")].WorldId);
        Assert.Null(bindings[new(MapPrefabChildKind.Surface, "deck/deck-support/top")].NumericId);
        var plan = MapPrefabBindingPlanner.Plan(f.Document, f.Assets.GetPrefab("hut"), f.Assets, "b cé", new([]));
        var item = plan.Bindings.Entries.Single(b => b.Child == new MapPrefabChildKey(MapPrefabChildKind.Placement, "item"));
        Assert.Equal("prefab/b%20c%C3%A9/Placement/item", item.WorldId);
        Assert.True(item.NumericId > f.Document.NumericIdHighWaterMark);
        Assert.Equal(f.Document.NumericIdHighWaterMark + 3, plan.HighWaterMark);
        var collision = PrefabFixtures.Create("attached");
        collision.Document.Placements.Add(new MapPlacement { Id = "prefab/b/Placement/item", Kind = "item", AssetId = "item", Y = 0, Scale = 1 });
        Assert.Throws<MapDocumentException>(() => MapPrefabBindingPlanner.Plan(collision.Document, f.Assets.GetPrefab("hut"), f.Assets, "b", new([])));
    }

    [Fact]
    public void TypedRemap_DoesNotTouchTagsAssetIdsOrNames()
    {
        var f = PrefabFixtures.Create("all-resources");
        var asset = f.Assets.GetPrefab("hut");
        var item = asset.Children.OfType<MapPrefabPlacementChild>().Single(c => c.Key.Key == "item");
        item.Value.Tags.AddRange(new[] { "floor", "room", "doorway" });
        item.Value.AssetId = "item";
        var bindings = f.Document.Prefabs!.Instances.Single().Bindings.Entries.ToDictionary(b => b.Child, b => b.WorldId);
        var parent = new MapRecordRef("exterior", new("exterior", 0, 0));
        var remapped = MapPrefabReferences.Remap(asset, bindings, parent);
        var remappedItem = remapped.Children.OfType<MapPrefabPlacementChild>().Single(c => c.Key.Key == "item");
        Assert.Equal("a/item", remappedItem.Value.Id);
        Assert.Equal(new[] { "first", "second", "floor", "room", "doorway" }, remappedItem.Value.Tags);
        Assert.Equal("item", remappedItem.Value.AssetId);
        var marker = remapped.Children.OfType<MapPrefabMarkerChild>().Single();
        Assert.Equal(("a/entry", "Entry", "entrance"), (marker.Value.Id, marker.Value.Name, marker.Value.Role));
        Assert.Equal("Lower", remapped.Children.OfType<MapPrefabStoreyChild>().Single().Name);
        var room = (MapSpaceDoc)remapped.Children.OfType<MapPrefabRecordChild>().Single(c => c.Key.Key == "room").Value;
        Assert.Equal(("a/room", parent), (room.Id, room.Parent));
        var lamp = remapped.Children.OfType<MapPrefabLightChild>().Single();
        Assert.Equal(new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "a/doorway"), lamp.Value.Owner);
        Assert.All(remapped.Children.SelectMany(c => c.References), s => Assert.Equal(MapPrefabReferenceScope.World, s.Target.Scope));
        Assert.DoesNotContain(MapPrefabReferences.References(remapped), r => r.Scope == MapPrefabReferenceScope.Local);
        Assert.Throws<MapDocumentException>(() => MapPrefabReferences.Remap(asset, bindings, null));

        var world = PrefabFixtures.Create("roof-group");
        world.Document.Placements.Add(new MapPlacement { Id = "keeper", Kind = "exterior", AssetId = "item", Y = 0, Scale = 1, Tags = new() { "exterior" } });
        var renamed = MapPrefabReferences.RemapWorld(world.Document, MapPrefabChildKind.Record, "exterior", "outside");
        Assert.Equal("outside", renamed.Prefabs!.Instances.Single().ParentSpace!.Id);
        Assert.Equal("outside", renamed.BuildingIntent!.RoofGroups.Single().Rooms.Single().Id);
        Assert.Equal(("exterior", "exterior"), (renamed.Placements.Single().Kind, renamed.Placements.Single().Tags.Single()));
        Assert.Equal("exterior", world.Document.Prefabs!.Instances.Single().ParentSpace!.Id);
    }

    [Fact]
    public void SameTextLocalAndWorldReferences_RemapOnlyTheLocalOccurrence()
    {
        var ground = PrefabFixtures.Patch("ground", 2, 2, 0);
        var paint = PrefabFixtures.Patch("paint", 2, 2, 0);
        var key = ground.Key;
        var asset = new MapPrefabAsset(1, "same-text", new MapPrefabChild[]
        {
            PrefabFixtures.SurfaceChild("ground", MapSurfaceRole.SupportFloor, ground),
            new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, "paint"),
                PrefabFixtures.Surface("paint", MapSurfaceRole.PaintOverride) with { PaintTargetSurfaceId = "ground" }, new[] { paint })
            {
                References = PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "paint"),
                    PrefabFixtures.Surf("/surface/paintTargetSurfaceId", "ground", MapPrefabReferenceScope.World)),
            },
            new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "space"), new MapSpaceDoc("space", MapSpaceKind.Cave, null, null, [], [], [], []), key)
            { References = PrefabFixtures.Slots(PrefabFixtures.Anchor("ground")) },
            new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "bound"), new MapSpaceFootprint("bound", new("space", key), new("ground", 0, 0), new[] { 0 },
                new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.Ceiling, "ground", null)), key)
            {
                References = PrefabFixtures.Slots(PrefabFixtures.Anchor("ground"), PrefabFixtures.Ref("/value/space", "space", "ground"),
                    PrefabFixtures.Surf("/value/lattice/surfaceId", "ground"), PrefabFixtures.Surf("/value/lower/surfaceId", "ground"),
                    PrefabFixtures.Surf("/value/upper/surfaceId", "ground", MapPrefabReferenceScope.World)),
            },
        });
        asset = MapPrefabFile.Read(MapPrefabFile.Write(asset), "same-text");
        var bindings = asset.Children.ToDictionary(c => c.Key, c => "p/" + c.Key.Key);
        var remapped = MapPrefabReferences.Remap(asset, bindings, null);
        var paintRef = remapped.Children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "paint").Surface;
        Assert.Equal(("p/paint", "ground"), (paintRef.Id, paintRef.PaintTargetSurfaceId));
        var bound = (MapSpaceFootprint)remapped.Children.OfType<MapPrefabRecordChild>().Single(c => c.Key.Key == "bound").Value;
        Assert.Equal(new MapRecordRef("p/space", new("p/ground", 0, 0)), bound.Space);
        Assert.Equal(new MapPatchKey("p/ground", 0, 0), bound.Lattice);
        Assert.Equal(("p/ground", "ground"), (bound.Lower.SurfaceId, bound.Upper.SurfaceId));
        Assert.Equal(new MapPatchKey("p/ground", 0, 0), remapped.Children.OfType<MapPrefabRecordChild>().Single(c => c.Key.Key == "bound").Anchor);
        var swapped = asset with
        {
            Children = asset.Children.Select(c => c.Key.Key == "bound"
                ? c with { References = c.References.Select(s => s.Path == "/value/upper/surfaceId" ? s with { Target = s.Target with { Scope = MapPrefabReferenceScope.Local } } : s).ToArray() }
                : c).ToArray(),
        };
        var local = (MapSpaceFootprint)MapPrefabReferences.Remap(swapped, bindings, null).Children.OfType<MapPrefabRecordChild>().Single(c => c.Key.Key == "bound").Value;
        Assert.Equal("p/ground", local.Upper.SurfaceId);
    }

    [Fact]
    public void SlotPointers_ResolveToTheirIdsInCanonicalBytes()
    {
        var asset = PrefabFixtures.Create("all-resources").Assets.GetPrefab("hut");
        using var json = JsonDocument.Parse(MapPrefabFile.Write(asset));
        var children = json.RootElement.GetProperty("children").EnumerateArray().ToArray();
        int checkedSlots = 0;
        foreach (var child in asset.Children)
        {
            var element = children.Single(c => c.GetProperty("key").GetProperty("kind").GetString() == child.Key.Kind.ToString() &&
                c.GetProperty("key").GetProperty("key").GetString() == child.Key.Key);
            foreach (var slot in child.References)
            {
                JsonElement node = element;
                foreach (string segment in slot.Path.Split('/').Skip(1))
                    node = node.ValueKind == JsonValueKind.Array ? node[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : node.GetProperty(segment);
                Assert.Equal(slot.Target.Id, node.GetString());
                checkedSlots++;
            }
        }
        Assert.True(checkedSlots > 30);
    }

    [Fact]
    public void ImplicitLeafLightKeys_RetainBindingsAcrossRemoveRestore()
    {
        var f = PrefabFixtures.Create("attached");
        var asset = f.Assets.GetPrefab("hut");
        var before = f.Document.Prefabs!.Instances.Single().Bindings;
        var lights = before.Entries.Where(b => b.Child.Kind == MapPrefabChildKind.Light).Select(b => b.Child.Key).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "deck/glow-light/glow", "lantern/glow-light/glow" }, lights);
        var withoutLantern = asset with { Children = asset.Children.Where(c => c.Key.Key != "lantern").ToArray() };
        var dead = MapPrefabBindingPlanner.Plan(f.Document, withoutLantern, f.Assets, "a", before);
        Assert.True(dead.Bindings.Entries.Single(b => b.Child.Key == "lantern/glow-light/glow").Removed);
        Assert.False(dead.Bindings.Entries.Single(b => b.Child.Key == "deck/glow-light/glow").Removed);
        var restored = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "a", dead.Bindings);
        Assert.Equal(before.Entries.Select(b => (b.Child, b.WorldId, b.NumericId, b.Removed)),
            restored.Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId, b.Removed)));
        var implicitLights = MapPrefabAttachedLights.Enumerate(f.Assets, asset.Children);
        Assert.Equal(2, implicitLights.Count);
        Assert.All(implicitLights, l => Assert.Equal(MapPrefabReferenceScope.Local, ((MapPrefabLightChild)l).Value.Owner!.Scope));
        Assert.Equal(new[] { "deck", "lantern" }, implicitLights.Select(l => ((MapPrefabLightChild)l).Value.Owner!.Id));
        var explicitClash = asset with
        {
            Children = asset.Children.Append(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "deck/glow-light/glow"),
                PrefabFixtures.GlowLight() with { Id = "deck/glow-light/glow", Owner = null })).ToArray(),
        };
        Assert.Throws<MapDocumentException>(() => MapPrefabBindingPlanner.Plan(f.Document, explicitClash, f.Assets, "a", before));
    }

    [Fact]
    public void EmbeddedSupportRecord_RemoveRestoreHasOneBinding()
    {
        var f = PrefabFixtures.Create("attached");
        var asset = f.Assets.GetPrefab("hut");
        var implicitSupport = MapEffectiveSupport.Enumerate(f.Assets, asset.Children);
        var record = Assert.IsType<MapPrefabRecordChild>(Assert.Single(implicitSupport, c => c.Key.Kind == MapPrefabChildKind.Record));
        Assert.Equal("deck/deck-support/top-edge", record.Key.Key);
        Assert.Equal(new MapPatchKey("deck/deck-support/top", 0, 0), record.Anchor);
        Assert.All(((MapBoundaryChain)record.Value).Vertices, v => Assert.Equal("deck/deck-support/top", v.Vertex.SurfaceId));
        Assert.Empty(Assert.IsType<MapPrefabSurfaceChild>(Assert.Single(implicitSupport, c => c.Key.Kind == MapPrefabChildKind.Surface)).Patches.Single().Records);
        var before = f.Document.Prefabs!.Instances.Single().Bindings;
        Assert.Single(before.Entries, b => b.Child.Kind == MapPrefabChildKind.Record);
        var dead = MapPrefabBindingPlanner.Plan(f.Document, asset with { Children = asset.Children.Where(c => c.Key.Key != "deck").ToArray() }, f.Assets, "a", before);
        Assert.True(Assert.Single(dead.Bindings.Entries, b => b.Child.Kind == MapPrefabChildKind.Record).Removed);
        var restored = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "a", dead.Bindings);
        var binding = Assert.Single(restored.Bindings.Entries, b => b.Child.Kind == MapPrefabChildKind.Record);
        Assert.Equal((false, "prefab/a/Record/deck%2Fdeck-support%2Ftop-edge"), (binding.Removed, binding.WorldId));
        Assert.Equal(before.Entries, restored.Bindings.Entries);
    }

    [Fact]
    public void ExplicitSurfaceEmbeddedRecords_Refuse()
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut");
        var floor = asset.Children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "floor");
        floor.Patches.Single().Records.Add(new MapSpaceDoc("embedded", MapSpaceKind.Cave, null, null, [], [], [], []));
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(asset));
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(PrefabFixtures.AssetBytes("embedded-record"), "hut"));
        var resource = MapSupportResourceFile.Read(MapSupportResourceFile.Write(PrefabFixtures.DeckSupport()), "deck-support");
        Assert.Equal("top-edge", resource.Surfaces.Patches.Values.Single().Records.Single().Id);
    }

    [Fact]
    public void AssetLocalMaterializations_RoundTripScopeIdentityAndSuppression()
    {
        var f = PrefabFixtures.Create("materialized");
        var asset = f.Assets.GetPrefab("hut");
        Assert.Equal(PrefabFixtures.AssetBytes("materialized"), MapPrefabFile.Write(asset));
        Assert.Equal(2, asset.MaterializedAttachments.Count);
        Assert.All(asset.MaterializedAttachments, row => Assert.Equal(MapPrefabReferenceScope.Local, row.Placement.Scope));
        Assert.All(asset.MaterializedAttachments.SelectMany(r => r.Children), c => Assert.Equal(MapPrefabReferenceScope.Local, c.Child.Scope));
        MapPrefabLocalMaterializations.Validate(asset, f.Assets);
        var owners = asset.Children.Where(c => c is MapPrefabPlacementChild).ToArray();
        Assert.Empty(MapEffectiveSupport.Enumerate(f.Assets, asset.Children, asset.MaterializedAttachments));
        Assert.Empty(MapPrefabAttachedLights.Enumerate(f.Assets, asset.Children, asset.MaterializedAttachments));
        var regenerated = MapEffectiveSupport.Enumerate(f.Assets, owners).Concat(MapPrefabAttachedLights.Enumerate(f.Assets, owners)).ToArray();
        Assert.Equal(3, regenerated.Length);
        foreach (var child in regenerated)
            Assert.Equal(MapPrefabIdentity.ChildDigest(child), MapPrefabIdentity.ChildDigest(asset.Children.Single(c => c.Key == child.Key)));
        var bindings = f.Document.Prefabs!.Instances.Single().Bindings.Entries;
        Assert.Equal(asset.Children.Select(c => c.Key).ToHashSet(), bindings.Select(b => b.Child).ToHashSet());
        Assert.Equal(asset.Children.Count, bindings.Count);
        var emptied = asset with
        {
            Children = asset.Children.Where(c => c.Key.Kind != MapPrefabChildKind.Light).ToArray(),
            MaterializedAttachments = asset.MaterializedAttachments.Select(r => r.Kind == MapPrefabAttachmentKind.Light ? r with { Children = [] } : r).ToArray(),
        };
        Assert.Empty(MapPrefabAttachedLights.Enumerate(f.Assets, emptied.Children, emptied.MaterializedAttachments));
        Assert.Equal(MapPrefabFile.Write(emptied), MapPrefabFile.Write(MapPrefabFile.Read(MapPrefabFile.Write(emptied), "hut")));
    }

    [Theory]
    [InlineData("world-scope")]
    [InlineData("wrong-kind")]
    [InlineData("dangling-child")]
    [InlineData("duplicate-edge")]
    [InlineData("shared-child")]
    [InlineData("frame-mismatch")]
    [InlineData("source-digest")]
    public void AttachmentRows_RefuseStructuralFaults(string fault)
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("materialized"), "hut");
        var rows = asset.MaterializedAttachments.ToList();
        var support = rows.Single(r => r.Kind == MapPrefabAttachmentKind.Support);
        var light = rows.Single(r => r.Kind == MapPrefabAttachmentKind.Light);
        switch (fault)
        {
            case "world-scope": rows[rows.IndexOf(support)] = support with { Placement = support.Placement with { Scope = MapPrefabReferenceScope.World } }; break;
            case "wrong-kind": rows[rows.IndexOf(light)] = light with { Children = support.Children }; rows.Remove(support); break;
            case "dangling-child":
                rows[rows.IndexOf(light)] = light with { Children = new[] { light.Children[0] with { Child = light.Children[0].Child with { Id = "missing" } } } }; break;
            case "duplicate-edge": rows.Add(support); break;
            case "shared-child": rows.Add(support with { ResourceId = "other-support" }); break;
            case "frame-mismatch": rows[rows.IndexOf(light)] = light with { PlacementFrame = light.PlacementFrame with { X = 9 } }; break;
            case "source-digest":
                rows[rows.IndexOf(light)] = light with { Children = new[] { light.Children[0] with { SourceDigest = new string('c', 64) } } }; break;
        }
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(asset with { MaterializedAttachments = rows }));
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("edge")]
    public void AttachmentRows_ValidateLoadedAndStoredIdentity(string fault)
    {
        var f = PrefabFixtures.Create("materialized");
        string loaded = MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options());
        Assert.Equal(loaded, MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(f.Document), f.Assets, PrefabFixtures.Options()));
        Assert.NotEqual(loaded, MapAuthoredIdentityV2.Compute(PrefabFixtures.Create("attached").Document, PrefabFixtures.Create("attached").Assets, PrefabFixtures.Options()));
        var asset = f.Assets.GetPrefab("hut");
        var row = asset.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support);
        var changed = row with
        {
            ResourceSha256 = fault == "sha" ? new string('d', 64) : row.ResourceSha256,
            ResourceId = fault == "edge" ? "glow-light" : row.ResourceId,
        };
        var faulty = asset with { MaterializedAttachments = asset.MaterializedAttachments.Select(r => r == row ? changed : r).ToArray() };
        Assert.Throws<MapDocumentException>(() => MapPrefabLocalMaterializations.Validate(faulty, f.Assets));
    }
}
