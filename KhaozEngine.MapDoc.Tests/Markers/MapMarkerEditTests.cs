using System;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Pure marker preparation: narrow effects from the shared diff, ownership and reservation refusals, payload
/// and name validation, the resolver prerequisite and the closed marker decoder.</summary>
public sealed class MapMarkerEditTests
{
    const MapNativeInvalidation PhysicsNavUnbounded = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded;

    [Fact]
    public void MetadataOnlyMarkerSet_HasIdAndRenameButNoBounds()
    {
        MarkerFixture basic = MarkerFixtures.Create();
        MapLocalMarkerDoc m = MarkerFixtures.BasicMarker();
        AssertMetadata(basic, m with { Name = "arrival-new" }, new MapMarkerNameChange("m", "arrival", "arrival-new"));
        AssertMetadata(basic, m with { Role = "other-role" }, null);
        AssertMetadata(basic, m with { Tags = new[] { "second", "first" } }, null);
        // Archetype is payload 2 only, so the control starts from the same point already in payload-2 Explicit form.
        MapLocalMarkerDoc explicitPoint = MapMarkerHeights.Apply(m, new(12.25f, null));
        MarkerFixture payloadTwo = MarkerFixtures.WithWorldMarkers(basic, explicitPoint);
        AssertMetadata(payloadTwo, explicitPoint with { ArchetypeId = "keeper" }, null);

        // Position, yaw and enabled changes are spatial and report both resolved point boxes with Residency only.
        AssertSpatial(basic, m with { Position = m.Position with { X = 0.75f } });
        AssertSpatial(basic, m with { YawRadians = -1.25f });
        AssertSpatial(basic, m with { Enabled = false });
        // A binding change to a support at the same height is still spatial.
        MarkerFixture stacked = MarkerFixtures.Stacked();
        MapLocalMarkerDoc surface = stacked.Document.BuildingIntent!.Markers.Single(x => x.Id == "stacked-surface");
        MapEffectiveMarker beforeBound = Marker(MarkerFixtures.Effective(stacked), "stacked-surface");
        var rebound = AssertSpatial(stacked, MapMarkerHeights.Apply(surface, MarkerFixtures.SpaceHeight()));
        Assert.Equal(beforeBound.Position.Y, Marker(MarkerFixtures.Effective(new(rebound.After, stacked.Assets)), "stacked-surface").Position.Y);

        // A create and a delete each carry exactly one box and one rename row.
        MapLocalMarkerDoc fresh = new(1, "fresh", "fresh-name", new Vector3(2, 3.5f, -4), 0, "entry-role", true, new[] { "new" });
        var created = MapMarkerEdits.Prepare(basic.Document, basic.Assets, new MapSetMarker(fresh));
        Assert.Null(created.Effects.OldBounds);
        Assert.Equal(MapEffectiveAnnotations.Bounds(Marker(MarkerFixtures.Effective(new(created.After, basic.Assets)), "fresh")), created.Effects.NewBounds);
        Assert.Equal(new MapMarkerNameChange("fresh", null, "fresh-name"), Assert.Single(created.Effects.MarkerNameChanges));
        Assert.Equal(new[] { "fresh" }, created.Effects.MarkerIds);
        Assert.Equal(MapNativeInvalidation.Residency, created.Effects.Invalidates);
        var deleted = MapMarkerEdits.Prepare(basic.Document, basic.Assets, new MapRemoveMarker("m"));
        Assert.Equal(MapEffectiveAnnotations.Bounds(Marker(MarkerFixtures.Effective(basic), "m")), deleted.Effects.OldBounds);
        Assert.Null(deleted.Effects.NewBounds);
        Assert.Equal(new MapMarkerNameChange("m", "arrival", null), Assert.Single(deleted.Effects.MarkerNameChanges));
        Assert.Equal(MapNativeInvalidation.Residency, deleted.Effects.Invalidates);
        Assert.Empty(deleted.After.BuildingIntent!.Markers);

        // A same-value set has empty effects, and preparation never touches the input document.
        string original = MapDocumentFile.SaveText(basic.Document);
        var same = MapMarkerEdits.Prepare(basic.Document, basic.Assets, new MapSetMarker(m));
        Assert.Empty(same.Effects.MarkerIds);
        Assert.Equal(MapNativeInvalidation.None, same.Effects.Invalidates);
        Assert.Equal(original, MapDocumentFile.SaveText(same.After));
        Assert.Equal(original, MapDocumentFile.SaveText(basic.Document));
    }

    [Fact]
    public void MarkerSet_PublishesOnlyTheBuildingIntentAndKeepsUnrelatedIntent()
    {
        MarkerFixture basic = MarkerFixtures.Create();
        var light = new MapLocalLightDoc(1, "lamp", null, new Vector3(1, 2, 3), Vector3.One, 2, 4, false);
        basic.Document.BuildingIntent = new(1, [], [], new[] { light }, basic.Document.BuildingIntent!.Markers)
        { Storeys = new[] { new MapStoreyDoc(1, "storey", "Ground", null) } };
        var result = MapMarkerEdits.Prepare(basic.Document, basic.Assets, new MapSetMarker(MarkerFixtures.BasicMarker() with { Name = "renamed" }));
        Assert.True(result.WriteSet.BuildingIntent);
        Assert.False(result.WriteSet.Prefabs);
        Assert.False(result.WriteSet.NativeResourceTransforms);
        Assert.False(result.WriteSet.Placements);
        Assert.False(result.WriteSet.NumericReservations);
        Assert.Empty(result.WriteSet.Patches);
        Assert.Equal(MapPrefabJson.Bytes(light), MapPrefabJson.Bytes(Assert.Single(result.After.BuildingIntent!.Lights)));
        Assert.Equal("storey", Assert.Single(result.After.BuildingIntent.Storeys).Id);
        Assert.Equal("renamed", Assert.Single(result.After.BuildingIntent.Markers).Name);
    }

    [Fact]
    public void PrefabBoundMarkerSet_RefusesWithOverrideGuidance()
    {
        MarkerFixture fixture = MarkerFixtures.Create("prefab-basic");
        string before = MapDocumentFile.SaveText(fixture.Document);
        MapLocalMarkerDoc child = new(1, "a/entry", "arrival-changed", new Vector3(1, 2, -1), 0.371f, "entry-role", true, new[] { "first" });

        var set = Assert.Throws<MapDocumentException>(() => MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(child)));
        Assert.Contains("prefab_override", set.Message, StringComparison.Ordinal);
        var remove = Assert.Throws<MapDocumentException>(() => MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapRemoveMarker("a/entry")));
        Assert.Contains("prefab_override", remove.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));

        // Positive control: a standalone marker beside the live instance is accepted.
        var accepted = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(child with { Id = "w", Name = "w-name" }));
        Assert.Equal(new[] { "a/entry", "w" }, MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(new(accepted.After, fixture.Assets))).Select(x => x.Id));
    }

    [Fact]
    public void InvalidHeightDuplicateIdAndNameAndForeignOwnership_RefuseWithPositiveControls()
    {
        MarkerFixture basic = MarkerFixtures.Create();
        MapLocalMarkerDoc m = MarkerFixtures.BasicMarker();
        MapLocalMarkerDoc other = new(1, "other", "other-name", new Vector3(3, 1, 3), 0, "entry-role", true, Array.Empty<string>());

        // Invalid height: a Support height whose owner does not exist, and a Support carrier that is not +0.
        Refuses(basic, new MapSetMarker(MapMarkerHeights.Apply(m, MarkerFixtures.SurfaceHeight())), "Marker 'm'");
        Refuses(basic, new MapSetMarker(MapMarkerHeights.Apply(m, MarkerFixtures.SurfaceHeight()) with { Position = m.Position }), "+0");
        Refuses(basic, new MapSetMarker(m with { Position = m.Position with { Y = float.NaN } }), "finite");
        Accepts(basic, new MapSetMarker(MapMarkerHeights.Apply(m, new(13.125f, null))));

        // Duplicate name with a positive control.
        MarkerFixture two = MarkerFixtures.WithWorldMarkers(basic, m, other);
        Refuses(two, new MapSetMarker(other with { Name = "arrival" }), "duplicate marker name 'arrival'");
        Accepts(two, new MapSetMarker(other with { Name = "arrival-2" }));

        // Duplicate ID against a dormant reservation of a removed instance, with a positive control.
        MarkerFixture prefab = MarkerFixtures.Create("prefab-basic");
        MapDocument removed = MapPrefabEdits.Prepare(prefab.Document, prefab.Assets, new MapRemovePrefab("a")).After;
        var dormant = new MarkerFixture(removed, prefab.Assets);
        Refuses(dormant, new MapSetMarker(other with { Id = "a/entry" }), "reservation");
        Accepts(dormant, new MapSetMarker(other));

        // Foreign ownership: a live prefab child is owned by its instance, and a missing standalone ID has nothing to remove.
        Refuses(prefab, new MapSetMarker(other with { Id = "a/entry" }), "prefab_override");
        Refuses(basic, new MapRemoveMarker("absent"), "No standalone marker 'absent'");
        Accepts(prefab, new MapSetMarker(other));
        Refuses(basic, new MapSetMarker(other with { Id = " " }), "ID");
    }

    [Fact]
    public void ResolverOneNative_PreparationNamesResolverAdoption()
    {
        var (document, assets, _) = FormatFourFixtures.Build();
        Assert.Equal(new MapResolverIdentityDoc(1, 1), document.ResolverIdentity);
        string before = MapDocumentFile.SaveText(document);
        foreach (MapMarkerEdit edit in new MapMarkerEdit[] { new MapSetMarker(MarkerFixtures.BasicMarker()), new MapRemoveMarker("m") })
        {
            var refused = Assert.Throws<MapDocumentException>(() => MapMarkerEdits.Prepare(document, assets, edit));
            Assert.Contains("resolver adoption", refused.Message, StringComparison.Ordinal);
            Assert.Contains("MapResolverAdoption.ConvertToAuthoredSupport", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, MapDocumentFile.SaveText(document));
    }

    [Fact]
    public void ReadMarker_IsClosedAndSharesTheBindingCodec()
    {
        MapLocalMarkerDoc bound = MapMarkerHeights.Apply(MarkerFixtures.BasicMarker(), MarkerFixtures.SpaceHeight()) with { ArchetypeId = "keeper" };
        string json = Encoding.UTF8.GetString(MapPrefabJson.Bytes(bound));
        MapLocalMarkerDoc read = MapPrefabJson.ReadMarker(json);
        Assert.Equal(json, Encoding.UTF8.GetString(MapPrefabJson.Bytes(read)));
        Assert.Equal(bound.SupportBinding, read.SupportBinding);
        Assert.Equal(bound.Tags, read.Tags);
        string payloadOne = Encoding.UTF8.GetString(MapPrefabJson.Bytes(MarkerFixtures.BasicMarker()));
        Assert.Equal(payloadOne, Encoding.UTF8.GetString(MapPrefabJson.Bytes(MapPrefabJson.ReadMarker(payloadOne))));

        // Foreign members, an explicit null binding member, a binding member of the other kind and an invalid payload refuse.
        Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarker(json.Replace("\"archetypeId\"", "\"unknown\"", StringComparison.Ordinal)));
        Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarker(json.Replace("\"kind\":\"Space\"", "\"kind\":\"Space\",\"surfaceId\":null", StringComparison.Ordinal)));
        Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarker(json.Replace("\"kind\":\"Space\"", "\"kind\":\"Space\",\"surfaceId\":\"floor\"", StringComparison.Ordinal)));
        Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarker(payloadOne.Replace("\"payloadVersion\":1", "\"payloadVersion\":2", StringComparison.Ordinal)));
        Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarker("null"));
    }

    static void AssertMetadata(MarkerFixture fixture, MapLocalMarkerDoc changed, MapMarkerNameChange? rename)
    {
        var result = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(changed));
        Assert.Equal(MapNativeInvalidation.None, result.Effects.Invalidates);
        Assert.Null(result.Effects.OldBounds);
        Assert.Null(result.Effects.NewBounds);
        Assert.Equal(new[] { changed.Id }, result.Effects.MarkerIds);
        Assert.Equal(rename is null ? Array.Empty<MapMarkerNameChange>() : new[] { rename }, result.Effects.MarkerNameChanges);
        Assert.Empty(result.Effects.Patches);
        Assert.Empty(result.Effects.ChildIds);
    }

    static MapPrefabEditResult AssertSpatial(MarkerFixture fixture, MapLocalMarkerDoc changed)
    {
        var result = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(changed));
        MapEffectiveMarker before = Marker(MarkerFixtures.Effective(fixture), changed.Id);
        MapEffectiveMarker after = Marker(MarkerFixtures.Effective(new(result.After, fixture.Assets)), changed.Id);
        Assert.Equal(MapNativeInvalidation.Residency, result.Effects.Invalidates);
        Assert.Equal(MapNativeInvalidation.None, result.Effects.Invalidates & PhysicsNavUnbounded);
        Assert.Equal(MapEffectiveAnnotations.Bounds(before), result.Effects.OldBounds);
        Assert.Equal(MapEffectiveAnnotations.Bounds(after), result.Effects.NewBounds);
        Assert.Equal(new[] { changed.Id }, result.Effects.MarkerIds);
        Assert.Empty(result.Effects.MarkerNameChanges);
        return result;
    }

    static void Refuses(MarkerFixture fixture, MapMarkerEdit edit, string message)
    {
        string before = MapDocumentFile.SaveText(fixture.Document);
        var refused = Assert.Throws<MapDocumentException>(() => MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, edit));
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
    }

    static void Accepts(MarkerFixture fixture, MapSetMarker edit)
    {
        var result = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapEffectiveMarker marker = Marker(MarkerFixtures.Effective(new(result.After, fixture.Assets)), edit.Marker.Id);
        Assert.Equal(edit.Marker.Name, marker.Name);
    }

    static MapEffectiveMarker Marker(MapEffectiveResources resources, string id) => MapEffectiveAnnotations.Markers(resources).Single(x => x.Id == id);
}
