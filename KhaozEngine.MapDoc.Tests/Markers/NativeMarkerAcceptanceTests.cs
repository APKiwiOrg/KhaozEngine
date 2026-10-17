using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Payload, identity, membership and adoption of the invented marker village on both storage forms.</summary>
public sealed class NativeMarkerAcceptanceTests
{
    [Fact]
    public void VillageMarkers_PayloadIdentityAndMembershipSurviveBothStorageForms()
    {
        MarkerFixture village = MarkerFixtures.Village();
        Assert.Empty(village.Document.Spawns);
        Assert.Empty(village.Document.PlayerSpawns);
        string authored = MapDocumentFile.SaveText(village.Document);
        var identities = new List<string>();
        var forms = new List<Row[]>();
        foreach (bool tiled in new[] { false, true })
        {
            // Each form loads its own document and its own verified copy of the resource bytes.
            MarkerFixture loaded = MarkerFixtures.Reload(village with { Assets = PrefabFixtures.ReloadVillageAssets(village.Assets) }, tiled);
            Assert.Equal(MarkerFixtures.VillageTileSize, loaded.Document.TileSize);
            Assert.Equal(authored, MapDocumentFile.SaveText(loaded.Document));
            identities.Add(MapAuthoredIdentityV2.Compute(loaded.Document, loaded.Assets, MarkerFixtures.Options()));
            MapEffectiveResources resources = MarkerFixtures.Effective(loaded);
            IReadOnlyList<MapEffectiveMarker> markers = MapEffectiveAnnotations.Markers(resources);
            var registry = new MapMarkerRegistry(markers);
            MapMembershipResult Space(MapEffectiveMarker m) => MapMarkerSpaces.Query(resources.CompleteView(), m);

            Assert.Equal(MarkerFixtures.VillageIds, markers.Select(m => m.Id));
            Assert.Equal(6, registry.All.Count);
            Assert.Equal(5, registry.Enabled.Count);
            Assert.Equal(6, markers.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(new[] { 1 }, markers.Where(m => m.Id == MarkerFixtures.RoofLabelId).Select(m => m.PayloadVersion));
            Assert.All(markers.Where(m => m.Id != MarkerFixtures.RoofLabelId), m => Assert.Equal(2, m.PayloadVersion));
            Assert.Null(registry.FindById(MarkerFixtures.RoofLabelId)!.HeightProvenance);
            Assert.Equal(new[] { "second", "first", "first" }, registry.FindByName("disabled-local")!.Tags);
            Assert.Equal(new[] { MarkerFixtures.DisabledLocalId }, registry.ByTag("first", false).Where(m => !m.Enabled).Select(m => m.Id));
            Assert.Equal(new[] { "start" }, registry.ByRole("start-role", true).Select(m => m.Id));
            Assert.Equal(new[] { "actor" }, registry.ByRole("actor-role", true).Select(m => m.Id));
            Assert.Equal(new[] { MarkerFixtures.DisabledLocalId, "actor" }, registry.ByRole("actor-role", false).Select(m => m.Id));
            Assert.Equal(new[] { "cave-bound", "start" }, resources.MarkerSupports.Select(s => s.MarkerId));
            Assert.All(resources.MarkerSupports, s => Assert.Equal(MapSupportStatus.Supported, s.Support.Status));
            // Minima are included and maxima excluded, negative coordinates included.
            Assert.Equal(new[] { "actor" }, registry.InRect(new(-1.5, -10.25, -1, -10), false, Space).Select(p => p.Marker.Id));
            Assert.Equal(new[] { "start" }, registry.InRect(new(10.5, -12.75, 11, -12.5), false, Space).Select(p => p.Marker.Id));
            Assert.Equal(new[] { "cave-explicit" }, registry.InRect(new(2, -2.5, 2.5, 0), false, Space).Select(p => p.Marker.Id));
            Assert.Equal(new[] { MarkerFixtures.DisabledLocalId, MarkerFixtures.RoofLabelId },
                registry.InRect(new(-8, 0, -4, 4), false, Space).Select(p => p.Marker.Id));
            Assert.Equal(new[] { MarkerFixtures.RoofLabelId }, registry.InRect(new(-8, 0, -4, 4), true, Space).Select(p => p.Marker.Id));
            Assert.Equal(new[]
            {
                (MarkerFixtures.DisabledLocalId, MapMembershipStatus.Resolved, "a/room", "exterior"),
                (MarkerFixtures.RoofLabelId, MapMembershipStatus.Resolved, "exterior", ""),
                ("actor", MapMembershipStatus.Resolved, "exterior", ""),
                ("cave-bound", MapMembershipStatus.Resolved, "high-cave", ""),
                ("cave-explicit", MapMembershipStatus.Resolved, "deep-room", "deep-cave"),
                ("start", MapMembershipStatus.Resolved, "exterior", ""),
            }, markers.Select(m => { var s = Space(m); return (m.Id, s.Status, s.SpaceId ?? "", string.Join("|", s.ParentSpaceIds)); }));
            forms.Add(markers.Select(Row.Of).ToArray());
        }
        Assert.Equal(forms[0], forms[1]);
        Assert.Equal(identities[0], identities[1]);
    }

    [Fact]
    public void VillageAdoption_ClearsBothListsIntoUniqueMarkers()
    {
        MarkerFixture legacy = MarkerFixtures.VillageWithLegacyLists();
        string before = MapDocumentFile.SaveText(legacy.Document);
        MapDocument adopted = MapMarkerAdoption.ConvertSpawns(legacy.Document, MarkerFixtures.VillageChoices());

        Assert.Equal(before, MapDocumentFile.SaveText(legacy.Document));
        Assert.Empty(adopted.Spawns);
        Assert.Empty(adopted.PlayerSpawns);
        var markers = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(legacy with { Document = adopted })));
        Assert.Equal(8, markers.All.Count);
        Assert.Equal(6, markers.Enabled.Count);
        MapEffectiveMarker actor = markers.FindById("adopted-actor")!;
        Assert.Equal(("adopted-actor", "actor-role", false, "invented-actor"), (actor.Name, actor.Role, actor.Enabled, actor.ArchetypeId));
        Assert.Equal(MarkerFixtures.Exact(10.5f, -451.5f, -10.5f), actor.Position);
        Assert.Equal(new[] { "second", "first" }, actor.Tags);
        Assert.Equal(0f, actor.YawRadians);
        MapEffectiveMarker start = markers.FindById("adopted-start")!;
        Assert.Equal(MarkerFixtures.Exact(11.5f, -452.4f, -8.5f), start.Position);
        Assert.Equal("exterior", start.HeightProvenance!.Face!.Value.OwnerId);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.371f), BitConverter.SingleToUInt32Bits(start.YawRadians));

        // A destination name already held by a village marker refuses, and the source document keeps both lists.
        var choices = MarkerFixtures.VillageChoices().ToDictionary(p => p.Key, p => p.Value);
        var key = new MapLegacySpawnKey(MapLegacySpawnKind.PlayerSpawn, "legacy-start");
        choices[key] = choices[key] with { Name = "start" };
        var refused = Assert.Throws<MapDocumentException>(() => MapMarkerAdoption.ConvertSpawns(legacy.Document, choices));
        Assert.Contains("'start'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(legacy.Document));
    }

    /// <summary>Every value the village compares across forms and heads, as a value sequence rather than list references.</summary>
    internal sealed record Row(int Payload, string Id, string Name, MapExactPoint Position, float Yaw, string Role, bool Enabled, string Tags,
        string? Instance, string? Archetype, MapMarkerHeightProvenance? Provenance)
    {
        internal static Row Of(MapEffectiveMarker m) => new(m.PayloadVersion, m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled,
            string.Join("|", m.Tags), m.InstanceId, m.ArchetypeId, m.HeightProvenance);
    }
}
