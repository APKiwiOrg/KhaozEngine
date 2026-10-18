using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>The invented marker village on independently loaded client and server heads, in both storage forms, and
/// marker-only edits that move residency without touching physics or navigation.</summary>
public sealed class MapMarkerVillageAcceptanceTests
{
    static readonly MapNavTileOptions NavOptions = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void VillageMarkers_SaveReloadBothStorageFormsAndHeadsAgree()
    {
        MarkerFixture village = MarkerFixtures.Village();
        MapDocument document = village.Document;
        Assert.Empty(document.Spawns);
        Assert.Empty(document.PlayerSpawns);
        var forms = new List<Row[]>();
        var formQueries = new List<string[]>();
        foreach (bool tiled in new[] { false, true })
        {
            MapBuiltWorld client = Head(village, tiled), server = Head(village, tiled);
            Assert.NotSame(client.Document, server.Document);
            Assert.NotSame(client.Document.EffectiveResources, server.Document.EffectiveResources);
            Assert.Equal(MarkerFixtures.VillageTileSize, client.TileSize);
            Assert.Equal(client.TileSize, server.TileSize);
            Assert.Equal(6, client.Markers.Count);
            Assert.Equal(5, new MapMarkerRegistry(client.Markers).Enabled.Count);

            Row[] clientRows = Rows(client), serverRows = Rows(server);
            MapMarkerHeightProvenance?[] clientProvenance = client.Markers.Select(m => m.HeightProvenance).ToArray();
            MapMarkerHeightProvenance?[] serverProvenance = server.Markers.Select(m => m.HeightProvenance).ToArray();
            string[] clientMembershipRows = Membership(client), serverMembershipRows = Membership(server);
            Assert.Equal(clientRows, serverRows);
            Assert.Equal(clientProvenance, serverProvenance);
            Assert.Equal(clientMembershipRows, serverMembershipRows);
            Assert.Equal(Rects(client), Rects(server));
            Assert.Equal(client.AnnotationDigest, server.AnnotationDigest);
            Assert.Equal(client.BuildHash, server.BuildHash);
            Assert.Equal(client.Statics.Select(s => (s.OwnerId, s.Digest)), server.Statics.Select(s => (s.OwnerId, s.Digest)));
            foreach (MapBuiltWorld head in new[] { client, server }) AssertVillageValues(head);
            forms.Add(clientRows);
            formQueries.Add(clientMembershipRows.Concat(Rects(client)).Append(client.AnnotationDigest).Append(client.BuildHash).ToArray());
        }
        Assert.Equal(forms[0], forms[1]);
        Assert.Equal(formQueries[0], formQueries[1]);
    }

    [Fact]
    public void MarkerOnlyEdits_ChangeResidencyWithoutPhysicsOrNav()
    {
        MarkerFixture village = MarkerFixtures.Village();
        var grids = new MapWorldGrids(MarkerFixtures.VillageTileSize, 1, 4, Vector2.Zero);
        MapBuiltWorld before = Build(village);
        var beforeStatics = Statics(before);
        var beforeNavCaptureIdentities = Nav(before, grids);
        var beforeAnnotations = MapResidencyOwnership.BuildAnnotations(before, grids);

        // The actor stands above every static, and markers never widen the world extent.
        Assert.True(before.Markers.Single(m => m.Id == "actor").Position.Y.ToDouble() > before.Bounds.MaxY);

        // A move across a storage tile seam through the shared marker edit. The support-bound height re-resolves on
        // the same flat owner, so world bounds and every static stay put.
        MapLocalMarkerDoc start = village.Document.BuildingIntent!.Markers.Single(m => m.Id == "start");
        MapPrefabEditResult move = MapMarkerEdits.Prepare(village.Document, village.Assets,
            new MapSetMarker(start with { Position = start.Position with { X = -17.5f } }));
        Assert.Equal(MapNativeInvalidation.Residency, move.Effects.Invalidates);
        MapBuiltWorld moved = Build(village with { Document = move.After });
        var afterMarkerOnlyStatics = Statics(moved);
        var afterMarkerOnlyNavCaptureIdentities = Nav(moved, grids);
        Assert.Equal(beforeStatics, afterMarkerOnlyStatics);
        Assert.Equal(beforeNavCaptureIdentities, afterMarkerOnlyNavCaptureIdentities);
        Assert.Equal(before.Bounds, moved.Bounds);
        Assert.NotEqual(before.AnnotationDigest, moved.AnnotationDigest);

        var oldTile = new MapTileCoord(-1, -1);
        var newTile = new MapTileCoord(-2, -1);
        var movedAnnotations = MapResidencyOwnership.BuildAnnotations(moved, grids);
        Assert.Equal(new[] { oldTile }, Single(beforeAnnotations, "marker:start").Membership);
        Assert.Equal(new[] { newTile }, Single(movedAnnotations, "marker:start").Membership);
        Assert.Equal(newTile, Single(movedAnnotations, "marker:start").StorageOwner);
        Assert.Equal(Others(beforeAnnotations, "marker:start"), Others(movedAnnotations, "marker:start"));
        MapAffectedSet movedSet = MapResidencyOwnership.Affected(before, moved, grids, move.Effects);
        Assert.Contains("marker:start", movedSet.Owners);
        Assert.Contains(oldTile, movedSet.StorageTiles);
        Assert.Contains(newTile, movedSet.StorageTiles);

        // Removing the marker above every static retires its owner and keeps its old membership in the affected set.
        MapPrefabEditResult removal = MapMarkerEdits.Prepare(move.After, village.Assets, new MapRemoveMarker("actor"));
        Assert.Equal(MapNativeInvalidation.Residency, removal.Effects.Invalidates);
        MapBuiltWorld removed = Build(village with { Document = removal.After });
        Assert.Equal(beforeStatics, Statics(removed));
        Assert.Equal(beforeNavCaptureIdentities, Nav(removed, grids));
        Assert.Equal(before.Bounds, removed.Bounds);
        var removedAnnotations = MapResidencyOwnership.BuildAnnotations(removed, grids);
        Assert.DoesNotContain(removedAnnotations, e => e.OwnerId == "marker:actor");
        Assert.Equal(Others(movedAnnotations, "marker:actor"), Others(removedAnnotations, "marker:actor"));
        MapAffectedSet removedSet = MapResidencyOwnership.Affected(moved, removed, grids, removal.Effects);
        Assert.Contains("marker:actor", removedSet.Owners);
        Assert.Contains(Single(movedAnnotations, "marker:actor").StorageOwner, removedSet.StorageTiles);

        // The server whole load lists every root-resident marker once. A tiled window over the start tile alone still
        // names all six from root metadata, including the markers whose storage tiles it does not load.
        var markerEntries = beforeAnnotations.Where(e => e.OwnerId.StartsWith("marker:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(MarkerFixtures.VillageIds.Select(id => "marker:" + id), markerEntries.Select(e => e.OwnerId));
        var window = new MapTileRect(oldTile, oldTile);
        Assert.Equal(new[] { "marker:a/disabled-local", "marker:a/entry", "marker:actor", "marker:cave-bound", "marker:cave-explicit" },
            markerEntries.Where(e => !window.Contains(e.StorageOwner)).Select(e => e.OwnerId));
        string directory = Path.Combine(Path.GetTempPath(), "ke-marker-village-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(village.Document, directory);
            MapDocument windowed = MapDocumentFile.LoadTiled(directory, window);
            Assert.Equal(MarkerFixtures.VillageIds, MapMarkerAnchors.Read(windowed, village.Assets).Select(a => a.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("incident", "start", -452.4f, "exterior")]
    [InlineData("neighbour", "cave-bound", -39.375f, "high-floor")]
    public void SparseFloorSeats_BuildBothHeadsOnTheNamedOwner(string variant, string markerId, float worldY, string owner)
    {
        MarkerFixture fixture = MarkerFixtures.SeatSlots(variant);
        MapBuiltWorld client = Head(fixture, false), server = Head(fixture, true);
        foreach (MapBuiltWorld head in new[] { client, server })
        {
            MapEffectiveMarker marker = head.Markers.Single(m => m.Id == markerId);
            Assert.Equal(MapExactValue.FromSingle(worldY), marker.Position.Y);
            Assert.Equal(owner, marker.HeightProvenance!.Face!.Value.OwnerId);
            Assert.Equal(worldY, head.Document.Placements.Single(p => p.PlacementId == "seat-probe").Transform.Position.Y);
        }
        Assert.Equal(Rows(client), Rows(server));
        Assert.Equal(client.BuildHash, server.BuildHash);
        // The effective path agrees with both heads.
        MapEffectiveResources expanded = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        Assert.Equal(client.Markers.Select(m => (m.Id, m.Position, m.HeightProvenance)),
            MapEffectiveAnnotations.Markers(expanded).Select(m => (m.Id, m.Position, m.HeightProvenance)));
    }

    static void AssertVillageValues(MapBuiltWorld head)
    {
        var registry = new MapMarkerRegistry(head.Markers);
        MapEffectiveMarker start = registry.FindByName("start")!, actor = registry.FindByName("actor")!;
        MapEffectiveMarker label = registry.FindByName("roof-label")!, disabledLocal = registry.FindByName("disabled-local")!;
        MapEffectiveMarker caveExplicit = registry.FindByName("cave-explicit")!, caveBound = registry.FindByName("cave-bound")!;
        MapEffectiveMarker negativeMarker = start;

        // Exact points, with negative X, an elevated local label and an actor above every static, and support heights from
        // their named owners.
        Assert.True(negativeMarker.Position.X.ToDouble() < 0);
        Assert.Equal(MarkerFixtures.Exact(-6.5f, -452.4f, -9.25f), start.Position);
        Assert.Equal(MarkerFixtures.Exact(5.25f, 12.25f, -12.5f), actor.Position);
        Assert.True(actor.Position.Y.ToDouble() > head.Bounds.MaxY, "the actor lies outside the geometry's vertical extent");
        Assert.Equal(MarkerFixtures.Exact(2, -57.75f, -2), caveExplicit.Position);
        Assert.Equal(MarkerFixtures.Exact(2.5f, -39.375f, -1.5f), caveBound.Position);
        Assert.Equal(MarkerFixtures.FramePoint(MarkerFixtures.TransferredFrame, 2, 4.5f, -2), label.Position);
        Assert.True(label.Position.Y.ToDouble() > -446.4, "the label stands above the room's ceiling");
        Assert.Equal(MarkerFixtures.FramePoint(MarkerFixtures.TransferredFrame, 3, 0, -1) with { Y = MapExactValue.FromSingle(-450f) },
            disabledLocal.Position);
        Assert.True(negativeMarker.Position.X.ToDouble() < 0 && label.Position.X.ToDouble() < 0 && disabledLocal.Position.X.ToDouble() < 0);

        Assert.Null(label.HeightProvenance);
        Assert.Equal(new MapMarkerHeightProvenance(MapMarkerHeightMode.Explicit, null, null, null, null, null), actor.HeightProvenance);
        Assert.Equal(actor.HeightProvenance, caveExplicit.HeightProvenance);
        foreach (var (marker, owner, worldY) in new[] { (start, "exterior", -452.4f), (caveBound, "high-floor", -39.375f), (disabledLocal, "a/floor", -450f) })
        {
            MapMarkerHeightProvenance provenance = marker.HeightProvenance!;
            Assert.Equal(MapMarkerHeightMode.Support, provenance.Mode);
            Assert.Equal(owner, provenance.Face!.Value.OwnerId);
            Assert.Equal(worldY, provenance.WorldY);
            Assert.Equal(MapExactValue.FromSingle(worldY), marker.Position.Y);
        }
        Assert.Equal(MarkerFixtures.SurfaceBinding("exterior"), start.HeightProvenance!.Binding);
        Assert.Equal(MarkerFixtures.SpaceBinding("high-cave", -39.25f, 0.5f, 0.5f), caveBound.HeightProvenance!.Binding);
        Assert.Equal(MarkerFixtures.SurfaceBinding("a/floor"), disabledLocal.HeightProvenance!.Binding);

        // Yaw modulo a turn, names, caller roles, ordered tags, archetype and instance ownership.
        Assert.True(MarkerFixtures.SameYaw(label.YawRadians, 0.25 + (double)0.371f), label.YawRadians.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(MarkerFixtures.SameYaw(disabledLocal.YawRadians, -0.5 + (double)0.371f));
        Assert.True(MarkerFixtures.SameYaw(start.YawRadians, 0.371f));
        Assert.True(MarkerFixtures.SameYaw(caveBound.YawRadians, -1.5f));
        Assert.Equal(new[] { "a/disabled-local", "a/entry", "actor", "cave-bound", "cave-explicit", "start" }, head.Markers.Select(m => m.Id));
        Assert.Equal(new[] { "disabled-local", "roof-label", "actor", "cave-bound", "cave-explicit", "start" }, head.Markers.Select(m => m.Name));
        Assert.Equal(new[] { "actor-role", "label-role", "actor-role", "cave-role", "cave-role", "start-role" }, head.Markers.Select(m => m.Role));
        Assert.Equal(new[] { "second", "first", "first" }, disabledLocal.Tags);
        Assert.Equal(new[] { "roof", "label" }, label.Tags);
        Assert.False(disabledLocal.Enabled);
        Assert.Equal(new[] { "a", "a", null, null, null, null }, head.Markers.Select(m => m.InstanceId));
        Assert.Equal(new[] { null, null, "invented-actor", null, null, null }, head.Markers.Select(m => m.ArchetypeId));
        Assert.Equal(new[] { 2, 1, 2, 2, 2, 2 }, head.Markers.Select(m => m.PayloadVersion));
    }

    // Each head reloads its own document and its own verified copy of the resource bytes, as an independent client and
    // server would.
    static MapBuiltWorld Head(MarkerFixture fixture, bool tiled)
    {
        MarkerFixture loaded = MarkerFixtures.Reload(fixture with { Assets = PrefabFixtures.ReloadVillageAssets(fixture.Assets) }, tiled);
        return MapWorldBuilder.Build(loaded.Document, loaded.Assets, Options());
    }

    static MapBuiltWorld Build(MarkerFixture fixture) => Head(fixture, false);

    static MapWorldBuildOptions Options() => new(MarkerFixtures.Options(), "public-marker-village/1", new());

    // Complete value rows, never record equality over list references. Yaw compares by its bits across heads.
    sealed record Row(int Payload, string Id, string Name, MapExactPoint Position, uint YawBits, string Role, bool Enabled, string Tags,
        string? Instance, string? Archetype, MapMarkerHeightProvenance? Provenance);

    static Row[] Rows(MapBuiltWorld world) => world.Markers.Select(m => new Row(m.PayloadVersion, m.Id, m.Name, m.Position,
        BitConverter.SingleToUInt32Bits(m.YawRadians), m.Role, m.Enabled, string.Join("|", m.Tags), m.InstanceId, m.ArchetypeId,
        m.HeightProvenance)).ToArray();

    static string[] Membership(MapBuiltWorld world) => world.Markers.Select(m =>
    {
        MapMembershipResult space = MapMarkerSpaces.Query(world.Surfaces, m);
        return string.Join(";", m.Id, space.Status, space.SpaceId, string.Join("|", space.ParentSpaceIds));
    }).ToArray();

    // Half-open rectangles with markers on their minimum and maximum edges, enabled and exhaustive.
    static string[] Rects(MapBuiltWorld world)
    {
        var registry = new MapMarkerRegistry(world.Markers);
        var rows = new List<string>();
        foreach (var (rect, enabledOnly, expected) in new (MapMarkerRect, bool, string[])[]
        {
            (new(-6.5, -9.25, -6, -9), false, new[] { "start" }),
            (new(5.25, -12.5, 5.5, -12), false, new[] { "actor" }),
            (new(2, -2, 2.5, 0), false, new[] { "cave-explicit" }),
            (new(-16, 0, -7, 8), false, new[] { "a/disabled-local", "a/entry" }),
            (new(-16, 0, -7, 8), true, new[] { "a/entry" }),
        })
        {
            var points = registry.InRect(rect, enabledOnly, m => MapMarkerSpaces.Query(world.Surfaces, m));
            Assert.Equal(expected, points.Select(p => p.Marker.Id));
            rows.AddRange(points.Select(p => string.Join(";", rect, enabledOnly, p.Marker.Id, p.Space.Status, p.Space.SpaceId)));
        }
        return rows.ToArray();
    }

    static (string, string, MapBox3)[] Statics(MapBuiltWorld world) => world.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds)).ToArray();

    static (MapNavTileCoord, string)[] Nav(MapBuiltWorld world, MapWorldGrids grids) =>
        MapNavTiling.Partition(world, grids, NavOptions).Select(t => (t.Coord, t.CaptureIdentity)).ToArray();

    static MapResidencyEntry Single(IReadOnlyList<MapResidencyEntry> entries, string owner) => Assert.Single(entries, e => e.OwnerId == owner);

    // Value rows of every other entry, since residency entries hold lists.
    static string[] Others(IReadOnlyList<MapResidencyEntry> entries, string owner) => entries.Where(e => e.OwnerId != owner).Select(e =>
        string.Join(";", e.OwnerId, e.StorageOwner, string.Join("|", e.Membership), string.Join("|", e.NavTiles), string.Join("|", e.Cells),
            e.WorldBounds)).ToArray();
}
