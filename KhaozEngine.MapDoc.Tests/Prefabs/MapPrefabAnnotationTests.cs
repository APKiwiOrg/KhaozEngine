using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabAnnotationTests
{
    [Fact]
    public void ExplicitCrossBuildingLink_HidesBothRoofsWithIndependentFlags()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
        var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.LinkedRoomPoint(r), MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Resolved, result.Status);
        Assert.Equal("room-a", result.SpaceId);
        var linked = result.Roofs.Where(x => x.RoofPlacementId != "shelter-roof").ToArray();
        Assert.Equal(2, linked.Length);
        Assert.All(linked, Hidden);
        Assert.True(result.Roofs.Single(x => x.RoofPlacementId == "shelter-roof").ColourVisible);
        Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, r.BuildingIntent.Roofs.Select(x => x.Id));
        var other = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 9, 1, -9), MapRoofMode.Auto);
        Assert.Equal("room-c", other.SpaceId);
        Assert.All(other.Roofs, Visible);
    }

    [Fact]
    public void AttachedGroups_OverlapDeduplicatesAndDoesNotFloodUnrelatedRooms()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.LinkedBuildings(true));
        Assert.Equal(2, MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.LinkedRoomPoint(r), MapRoofMode.Auto).Roofs.Count(x => !x.ColourVisible));
        Assert.All(MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 9, 1, -9), MapRoofMode.Auto).Roofs, Visible);
        Assert.All(MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 5, 1, -5), MapRoofMode.Auto).Roofs, Visible);
    }

    [Theory]
    [InlineData("prism")]
    [InlineData("parent-floor")]
    [InlineData("remote-anchor")]
    public void UnresolvedObserver_KeepsRoofsVisibleWithStatus(string fact)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
        var point = PrefabFixtures.LinkedRoomPoint(r);
        var complete = MapRoofPolicy.Evaluate(r, r.CompleteView(), point, MapRoofMode.Auto);
        Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, complete.Roofs.Select(x => x.RoofPlacementId).Order());
        Assert.False(complete.Roofs.Single(x => x.RoofPlacementId == "roof-a").ColourVisible);
        var view = PrefabFixtures.AcquireWithout(r, fact);
        var result = MapRoofPolicy.Evaluate(r, view, point, MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.MissingGeometry, result.Status);
        Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, result.Roofs.Select(x => x.RoofPlacementId).Order());
        Assert.All(result.Roofs, Visible);
        Assert.Contains(fact == "prism" ? "room-domain" : fact, PrefabFixtures.UnavailableIds(view));
        Assert.False(result.Witness.Complete);
    }

    [Fact]
    public void ObserverAtUpperStorey_HidesOnlyAboveLinks()
    {
        var f = PrefabFixtures.Cave("reflex-storeys");
        f.Document.BuildingIntent = new(1, new[]
        {
            new MapRoofLinkDoc("low", PrefabFixtures.World(MapPrefabChildKind.Placement, "a/roof"),
                new[] { PrefabFixtures.World(MapPrefabChildKind.Record, "prefab/a/Record/upper-room") }, [], 3),
            new MapRoofLinkDoc("high", PrefabFixtures.World(MapPrefabChildKind.Placement, "upper-roof"),
                new[] { PrefabFixtures.World(MapPrefabChildKind.Record, "prefab/a/Record/upper-room") }, [], 6),
        }, [], [], [])
        { Roofs = new[] { PrefabFixtures.World(MapPrefabChildKind.Placement, "upper-roof") } };
        f.Document.Placements.Add(new() { Id = "upper-roof", Kind = "roof", AssetId = "roof", X = 0, Y = 6, Z = 0, Scale = 1, NumericId = ++f.Document.NumericIdHighWaterMark });
        var r = PrefabFixtures.Effective(f);
        var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 1, 4, -1), MapRoofMode.Auto);
        Assert.Equal("prefab/a/Record/upper-room", result.SpaceId);
        Visible(result.Roofs.Single(x => x.RoofPlacementId == "a/roof"));
        Hidden(result.Roofs.Single(x => x.RoofPlacementId == "upper-roof"));
    }

    [Theory]
    [InlineData(MapRoofMode.Auto, 2)]
    [InlineData(MapRoofMode.AlwaysVisible, 0)]
    [InlineData(MapRoofMode.AlwaysHidden, 3)]
    public void AlwaysModes_PreserveShadowAndSkyIntent(MapRoofMode mode, int hidden)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
        var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.LinkedRoomPoint(r), mode);
        Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, result.Roofs.Select(x => x.RoofPlacementId));
        Assert.Equal(hidden, result.Roofs.Count(x => !x.ColourVisible));
        Assert.All(result.Roofs, roof => { Assert.True(roof.CastShadows); Assert.True(roof.SkyPickingBlocks); Assert.Equal(roof.ColourVisible, roof.CameraBoomBlocks); });
    }

    [Fact]
    public void LeafLights_TwoOwnersHaveDistinctBoundEmissions()
    {
        var f = PrefabFixtures.Create("two-leaf-lamps");
        var r = PrefabFixtures.Effective(f);
        var lights = MapEffectiveAnnotations.Lights(r).OrderBy(x => x.Id).ToArray();
        Assert.Equal(new[] { "light-a", "light-b" }, lights.Select(x => x.Id));
        Assert.Equal(new[] { "owner-a", "owner-b" }, lights.Select(x => x.OwnerPlacementId));
        Assert.All(lights, x => { Assert.Equal(1, x.PayloadVersion); Assert.Equal(2.4f, x.Radius); Assert.True(x.CastShadows); });
        Assert.Equal(new MapExactPoint(new(41, 4), MapExactValue.FromSingle(6.2f), new(-25, 8)), lights[0].Position);
        Assert.Equal(new MapExactPoint(new(81, 4), MapExactValue.FromSingle(6.2f), new(-25, 8)), lights[1].Position);
        var reload = PrefabFixtures.Effective(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        Assert.Equal(lights, MapEffectiveAnnotations.Lights(reload));
    }

    [Fact]
    public void ExplicitAndImplicitLights_CoexistWithoutDuplicateKeys()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.TwoLeafLamps(true));
        Assert.Equal(new[] { "light-a", "light-b", "light-extra" }, MapEffectiveAnnotations.Lights(r).Select(x => x.Id));
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.TwoLeafLamps(conflict: true));
    }

    [Fact]
    public void LocalLightAndMarker_PreserveVersionFieldsAndTransform()
    {
        var f = PrefabFixtures.AnnotationTransformed();
        var r = PrefabFixtures.Effective(f);
        var light = Assert.Single(MapEffectiveAnnotations.Lights(r));
        Assert.Equal(1, light.PayloadVersion);
        Assert.Equal("a/doorway", light.OwnerPlacementId);
        Assert.Equal(4.8f, light.Radius);
        var marker = Assert.Single(MapEffectiveAnnotations.Markers(r));
        Assert.Equal((1, "entry", "entrance", true, "a"), (marker.PayloadVersion, marker.Name, marker.Role, marker.Enabled, marker.InstanceId));
        Assert.Equal(new[] { "first", "second" }, marker.Tags);
        Assert.Equal(new MapResourceTransform(r.Transforms["Marker:" + marker.Id]).Point(new(new(0, 1), new(1, 1), new(0, 1))), marker.Position);
        Assert.Equal(0.371f, marker.YawRadians);
        var reload = MapEffectiveAnnotations.Markers(PrefabFixtures.Effective(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets))).Single();
        Assert.Equal(marker with { Tags = reload.Tags }, reload);
    }

    [Theory]
    [InlineData("roof")]
    [InlineData("room")]
    [InlineData("group")]
    [InlineData("light-owner")]
    [InlineData("non-room")]
    [InlineData("unlisted-roof")]
    public void UnknownRoofRoomOrGroupOrLightOwner_Refuses(string what)
    {
        var f = PrefabFixtures.Create();
        var roof = PrefabFixtures.World(MapPrefabChildKind.Placement, what == "roof" ? "missing" : what == "unlisted-roof" ? "a/item" : "a/roof");
        var rooms = what is "room" or "non-room" ? new[] { PrefabFixtures.World(MapPrefabChildKind.Record, what == "room" ? "missing" : "exterior") } : Array.Empty<MapPrefabReference>();
        f.Document.BuildingIntent = new(1, new[] { new MapRoofLinkDoc("test", roof, rooms,
            what == "group" ? new[] { PrefabFixtures.World(MapPrefabChildKind.RoofGroup, "missing") } : [], 3) }, [],
            what == "light-owner" ? new[] { new MapLocalLightDoc(1, "test-light", PrefabFixtures.World(MapPrefabChildKind.Placement, "missing"), Vector3.Zero, Vector3.One, 1, 2, false) } : [], []);
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(f));
    }

    [Theory]
    [InlineData("negative-colour")]
    [InlineData("owner-kind")]
    [InlineData("radius")]
    [InlineData("intensity")]
    public void LightPayload_StrictFieldsRefuseInvalidValues(string invalid)
    {
        var light = new MapLocalLightDoc(1, "emission", null, Vector3.Zero, Vector3.One, 1, 2, false);
        light = invalid switch
        {
            "negative-colour" => light with { Colour = new(-1, 1, 1) },
            "owner-kind" => light with { Owner = new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "$owner") },
            "radius" => light with { Radius = 0 },
            _ => light with { Intensity = float.PositiveInfinity },
        };
        Assert.Throws<MapDocumentException>(() => MapLightFile.Write(light));
    }

    [Fact]
    public void RemovedRoofChild_LeavesNoLiveInventoryEntry()
    {
        var f = PrefabFixtures.Create();
        var instance = f.Document.Prefabs!.Instances.Single();
        var removed = new[] { new MapPrefabChildKey(MapPrefabChildKind.Placement, "roof"), new MapPrefabChildKey(MapPrefabChildKind.RoofLink, "roof-link") };
        f.Document.Prefabs = new(1, 1, new[] { instance with
        {
            Bindings = new(instance.Bindings.Entries.Select(b => removed.Contains(b.Child) ? b with { Removed = true } : b).ToArray()),
            Overrides = removed.Select(k => (MapPrefabOverride)new MapPrefabRemoveOverride(k, null)).ToArray(),
        } }, []);
        var r = PrefabFixtures.Effective(f);
        Assert.Empty(MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 1, 1, -1), MapRoofMode.AlwaysHidden).Roofs);
    }

    [Fact]
    public void MaterializedLight_UsesCapturedFrameWithoutComposingOwnerAgain()
    {
        var f = PrefabFixtures.WithMaterializedOverride(PrefabFixtures.MaterializedOverrideFixture(), PrefabFixtures.MaterializedTransformOverride("light"));
        var r = PrefabFixtures.Effective(f);
        var light = Assert.Single(MapEffectiveAnnotations.Lights(r));
        Assert.Equal("captured-light", light.Id);
        Assert.Equal("deck-owner", light.OwnerPlacementId);
        Assert.Equal(new MapExactPoint(new(19, 4), new(5, 2), new(-21, 4)), light.Position);
        Assert.Equal(light, Assert.Single(MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(PrefabFixtures.ReloadPrisms(f)))));
    }

    [Fact]
    public void UnlinkedInventory_AlwaysModesWorkWithoutLinks()
    {
        var f = PrefabFixtures.Create("roomless");
        f.Document.BuildingIntent = new(1, [], [], [], []) { Roofs = new[] { PrefabFixtures.World(MapPrefabChildKind.Placement, "a/item") } };
        var r = PrefabFixtures.Effective(f);
        var p = PrefabFixtures.Point(r, 0, 0, 0);
        Visible(Assert.Single(MapRoofPolicy.Evaluate(r, r.CompleteView(), p, MapRoofMode.Auto).Roofs));
        Hidden(Assert.Single(MapRoofPolicy.Evaluate(r, r.CompleteView(), p, MapRoofMode.AlwaysHidden).Roofs));
        Visible(Assert.Single(MapRoofPolicy.Evaluate(r, r.CompleteView(), p, MapRoofMode.AlwaysVisible).Roofs));
    }

    [Fact]
    public void DuplicateRoofInventory_RefusesBeforeExpansion()
    {
        var f = PrefabFixtures.Create("roomless");
        var roof = PrefabFixtures.World(MapPrefabChildKind.Placement, "a/item");
        f.Document.BuildingIntent = new(1, [], [], [], []) { Roofs = new[] { roof, roof } };
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(f));
    }

    [Fact]
    public void ExplicitWorldOwnedLight_ComposesTheReferencedPlacementFrameOnce()
    {
        var light = Assert.Single(MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(PrefabFixtures.WorldOwnedLight())));
        Assert.Equal("world-owner", light.OwnerPlacementId);
        Assert.Equal(new MapExactPoint(new(41, 4), MapExactValue.FromSingle(6.2f), new(-25, 8)), light.Position);
        Assert.Equal(2.4f, light.Radius);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransformedRoofHeight_IsComparedInWorldMetres(bool far)
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f));
        var r = PrefabFixtures.Effective(f);
        var point = PrefabFixtures.RoomProbe(r, 1, 1, -1);
        var view = r.CompleteView();
        var observer = new MapFramePoint(view.Scope.Frame, new((float)(point.X.ToDouble() - view.Scope.Frame.Anchor.X), point.Y.ToSingle(), (float)(point.Z.ToDouble() - view.Scope.Frame.Anchor.Z)));
        var result = MapRoofPolicy.Evaluate(r, view, observer, MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Resolved, result.Status);
        Hidden(Assert.Single(result.Roofs));
        Assert.Equal(far ? -446.4f : 8.6f, r.BuildingIntent.RoofLinks.Single().AboveY);
    }

    [Fact]
    public void AmbiguousObserver_KeepsInventoryVisibleAndCarriesStatus()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("peer-overlap"));
        var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 2, 1, -2), MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Ambiguous, result.Status);
        Assert.NotEmpty(result.Roofs);
        Assert.All(result.Roofs, Visible);
    }

    [Fact]
    public void MultipleLinksToOneRoof_AnyEligibleDirectRoomLinkCanHideIt()
    {
        var f = PrefabFixtures.Create("linked-buildings");
        var intent = f.Document.BuildingIntent!;
        f.Document.BuildingIntent = new(1, new[]
        {
            new MapRoofLinkDoc("shelter-low", PrefabFixtures.World(MapPrefabChildKind.Placement, "shelter-roof"),
                new[] { PrefabFixtures.World(MapPrefabChildKind.Record, "room-c") }, [], 0),
            new MapRoofLinkDoc("shelter-high", PrefabFixtures.World(MapPrefabChildKind.Placement, "shelter-roof"),
                new[] { PrefabFixtures.World(MapPrefabChildKind.Record, "room-c") }, [], 3),
        }, intent.RoofGroups, [], [])
        { Roofs = intent.Roofs };
        var r = PrefabFixtures.Effective(f);
        var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 9, 1, -9), MapRoofMode.Auto);
        Hidden(result.Roofs.Single(x => x.RoofPlacementId == "shelter-roof"));
        Assert.All(result.Roofs.Where(x => x.RoofPlacementId != "shelter-roof"), Visible);
    }

    [Fact]
    public void MarkerName_PreservesAcceptedFixtureCasing()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
        Assert.Equal("Entry", Assert.Single(MapEffectiveAnnotations.Markers(r)).Name);
    }

    [Fact]
    public void RemovedRoofChild_StandaloneInventoryDuplicateIsInactive()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.RoofInventoryRemoval(false));
        Assert.Empty(r.BuildingIntent.Roofs);
        Assert.Empty(MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 1, 1, -1), MapRoofMode.AlwaysHidden).Roofs);
    }

    [Fact]
    public void RemovingLocalRoof_PreservesSameTextWorldInventory()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.RoofInventoryRemoval(true));
        Assert.Equal(new[] { "roof" }, r.BuildingIntent.Roofs.Select(x => x.Id));
        Hidden(Assert.Single(MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.Point(r, 1, 1, -1), MapRoofMode.AlwaysHidden).Roofs));
    }

    static void Hidden(MapRoofIntent roof)
    { Assert.False(roof.ColourVisible); Assert.True(roof.CastShadows); Assert.False(roof.CameraBoomBlocks); Assert.True(roof.SkyPickingBlocks); }
    static void Visible(MapRoofIntent roof)
    { Assert.True(roof.ColourVisible); Assert.True(roof.CastShadows); Assert.True(roof.CameraBoomBlocks); Assert.True(roof.SkyPickingBlocks); }
}
