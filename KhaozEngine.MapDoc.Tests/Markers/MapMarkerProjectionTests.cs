using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerProjectionTests
{
    [Fact]
    public void TypedViews_RetainEveryValueAndUseOnlyCallerRoles()
    {
        MarkerFixture fixture = MarkerFixtures.Create();
        fixture.Document.BuildingIntent = new(1, [], [], [], new[]
        {
            new MapLocalMarkerDoc(1, "start-1", "north gate", new Vector3(-3.5f, 2.25f, -7.125f), 0.371f, "start-role", true, new[] { "north", "gate" }),
            new MapLocalMarkerDoc(1, "start-0", "old gate", new Vector3(4, 1, 4), -0.5f, "start-role", false, new[] { "gate" }),
            new MapLocalMarkerDoc(1, "actor-tag", "pack den", new Vector3(-8, 0.5f, 2), 1.25f, "actor-role", true, new[] { "wolf", "pack", "wolf" }),
            MapMarkerHeights.Apply(new(1, "actor-kit", "kit post", new Vector3(6, 0, -2), -1.5f, "actor-role", true, new[] { "kit" }), MarkerFixtures.ExplicitHeight())
                with { ArchetypeId = "kit.actor" },
            MapMarkerHeights.Apply(new(1, "actor-off", "kit spare", new Vector3(7, 0, -2), 0, "actor-role", false, Array.Empty<string>()), MarkerFixtures.ExplicitHeight())
                with { ArchetypeId = "kit.actor" },
            new MapLocalMarkerDoc(1, "label", "old well", new Vector3(0.75f, 3, 0.25f), 0, "label-role", true, new[] { "landmark" }),
            new MapLocalMarkerDoc(1, "stray", "stray point", new Vector3(1, 1, 1), 0, "stray-role", true, Array.Empty<string>()),
            new MapLocalMarkerDoc(1, "odd", "odd point", new Vector3(2, 1, 2), 0, "odd-role", false, Array.Empty<string>()),
        });
        var registry = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture)));

        var starts = MapMarkerProjection.PlayerStarts(registry, "start-role", enabledOnly: false);
        Assert.Equal(new[] { "start-0", "start-1" }, starts.Select(s => s.Marker.Id));
        Assert.Equal(new[] { "start-1" }, MapMarkerProjection.PlayerStarts(registry, "start-role", enabledOnly: true).Select(s => s.Marker.Id));
        MapPlayerStartView north = starts[1];
        Assert.Same(registry.FindById("start-1"), north.Marker);
        Assert.Equal(MarkerFixtures.Exact(-3.5f, 2.25f, -7.125f), north.Marker.Position);
        Assert.Equal(0.371f, north.Marker.YawRadians);
        Assert.Equal(new[] { "north", "gate" }, north.Marker.Tags);
        Assert.Equal("north gate", north.Marker.Name);
        Assert.True(north.Marker.Enabled);
        Assert.False(starts[0].Marker.Enabled);
        Assert.Equal(-0.5f, starts[0].Marker.YawRadians);
        Assert.Null(north.Marker.HeightProvenance);

        var spawns = MapMarkerProjection.Spawns(registry, "actor-role", enabledOnly: false);
        Assert.Equal(new[] { ("actor-kit", (string?)"kit.actor"), ("actor-off", "kit.actor"), ("actor-tag", null) },
            spawns.Select(s => (s.Marker.Id, s.ArchetypeId)));
        Assert.Equal(new[] { "actor-kit", "actor-tag" }, MapMarkerProjection.Spawns(registry, "actor-role", enabledOnly: true).Select(s => s.Marker.Id));
        MapSpawnView kit = spawns[0];
        Assert.Equal(MarkerFixtures.Exact(6, 12.25f, -2), kit.Marker.Position);
        Assert.Equal(-1.5f, kit.Marker.YawRadians);
        Assert.Equal(new MapMarkerHeightProvenance(MapMarkerHeightMode.Explicit, null, null, null, null, null), kit.Marker.HeightProvenance);
        Assert.False(spawns[1].Marker.Enabled);
        Assert.Equal(new[] { "wolf", "pack", "wolf" }, spawns[2].Marker.Tags);

        var landmarks = MapMarkerProjection.Landmarks(registry, "label-role", enabledOnly: true);
        Assert.Equal("old well", Assert.Single(landmarks).Marker.Name);
        Assert.Equal(MarkerFixtures.Exact(0.75f, 3, 0.25f), landmarks[0].Marker.Position);

        // Roles are caller vocabulary. Any role projects, and the catalog only reports what the caller did not list.
        Assert.Equal(new[] { "stray" }, MapMarkerProjection.Landmarks(registry, "stray-role", enabledOnly: false).Select(l => l.Marker.Id));
        Assert.Empty(MapMarkerProjection.Spawns(registry, "Actor-role", enabledOnly: false));
        var findings = new MapMarkerRoleCatalog(new[] { "start-role", "actor-role", "label-role" }).Validate(registry);
        Assert.Equal(new[] { ("odd", "unknown-role"), ("stray", "unknown-role") }, findings.Select(f => (f.MarkerId, f.Code)));
        Assert.Contains("odd-role", findings[0].Detail, StringComparison.Ordinal);
        Assert.Equal(findings, new MapMarkerRoleCatalog(new[] { "label-role", "actor-role", "start-role" }).Validate(registry));
        Assert.Empty(new MapMarkerRoleCatalog(registry.All.Select(m => m.Role).ToArray()).Validate(registry));
        foreach (Type type in new[] { typeof(MapMarkerProjection), typeof(MapMarkerRoleCatalog), typeof(MapMarkerRegistry) })
            Assert.DoesNotContain(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), f => f.FieldType == typeof(string));
        Assert.ThrowsAny<ArgumentException>(() => MapMarkerProjection.PlayerStarts(registry, "", enabledOnly: false));
    }

    [Fact]
    public void Landmark_IsPointNotRegionDisc()
    {
        MarkerFixture before = MarkerFixtures.Stacked();
        MarkerFixture after = MarkerFixtures.Stacked();
        after.Document.BuildingIntent = new(1, [], [], [], after.Document.BuildingIntent!.Markers.Append(
            new MapLocalMarkerDoc(1, "well", "old well", new Vector3(0.75f, 3, 0.75f), 0, "label-role", true, new[] { "landmark" })).ToArray());
        MapEffectiveResources a = MarkerFixtures.Effective(before), b = MarkerFixtures.Effective(after);

        Assert.Equal(Regions(before.Document), Regions(after.Document));
        Assert.Equal(Patches(a), Patches(b));
        Assert.Equal(a.Surfaces.Refs.Select(s => s.Id), b.Surfaces.Refs.Select(s => s.Id));
        Assert.Equal(a.Placements.Select(p => (p.PlacementId, p.Transform)), b.Placements.Select(p => (p.PlacementId, p.Transform)));
        Assert.Equal(a.Transforms.OrderBy(t => t.Key, StringComparer.Ordinal), b.Transforms.OrderBy(t => t.Key, StringComparer.Ordinal));
        Assert.Equal(a.PaintRegions.Keys, b.PaintRegions.Keys);

        var landmark = Assert.Single(MapMarkerProjection.Landmarks(new MapMarkerRegistry(MapEffectiveAnnotations.Markers(b)), "label-role", enabledOnly: true));
        Assert.Equal(MarkerFixtures.Exact(0.75f, 3, 0.75f), landmark.Marker.Position);
        Assert.Equal(MapEffectiveAnnotations.Markers(a).Count + 1, MapEffectiveAnnotations.Markers(b).Count);
    }

    static string[] Regions(MapDocument document) => document.Regions.Select(r => r.Name + ":" + r.Shape?.GetType().Name).ToArray();

    static (MapPatchKey Key, string Digest)[] Patches(MapEffectiveResources resources) =>
        resources.Surfaces.Patches.Select(p => (p.Key, MapSurfaceSemantics.PatchDigest(p.Value))).OrderBy(p => p.Key).ToArray();
}
