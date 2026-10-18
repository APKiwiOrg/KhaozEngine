using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Native marker starts anchor windows from metadata alone, and whole native readouts use authored support.</summary>
public sealed class NativeMarkerConsumerTests
{
    [Fact]
    public void NativeStartAnchor_SelectsFromMetadataWithUnreadableBodies()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            Assert.True(MarkerConsumerFixtures.CorruptBodies(root, out int terrainBodies, out int supportPages) > 0);
            Assert.True(terrainBodies > 0, "no terrain tile body was corrupted");
            Assert.True(supportPages > 0, "no support page was corrupted");
            var options = new MapDocumentLoadOptions();
            MapDocument manifest;
            using (MapDocumentSource source = MapDocumentSource.OpenTiled(root, options)) manifest = source.Manifest;
            MapAssetClosure closure = MapAssetClosure.Load(manifest.NativeAssets,
                new MapStorageGuardedAssetSource(Path.GetFullPath(root), MapDocumentForm.Tiled));
            IReadOnlyList<MapMarkerAnchor> anchors = MapMarkerAnchors.Read(manifest, closure);
            MapExactXz boundsCentre = MarkerConsumerFixtures.BoundsCentre(manifest);
            MapMarkerAnchor? selected = MapMarkerAnchors.Select(anchors, "start-role", boundsCentre);

            Assert.Equal("start-a", selected!.Id);
            Assert.Equal(MapExactValue.FromSingle(-64.25f), selected.Position.X);
            Assert.Equal(-2, MapTileGrid.CoordOf((float)selected.Position.X.ToDouble(), (float)selected.Position.Z.ToDouble(), 64).X);
            Assert.ThrowsAny<Exception>(() => MapDocumentFile.LoadTiled(root, options));
            Assert.Null(MapMarkerAnchors.Select(anchors, "unknown-role", boundsCentre));

            // The disabled start lies nearer the centre and is skipped.
            MapMarkerAnchor disabled = anchors.Single(a => a.Id == "start-disabled");
            Assert.False(disabled.Enabled);
            Assert.Equal(new MapExactXz(MapExactValue.FromSingle(0.25f), MapExactValue.FromSingle(0.25f)), disabled.Position);

            // The standalone start mirrors start-a across the exact centre, so the tie resolves by ordinal ID in any order.
            MapMarkerAnchor tie = anchors.Single(a => a.Id == "start-tie");
            Assert.Equal(new MapExactXz(MapExactValue.FromSingle(0), MapExactValue.FromSingle(0)), boundsCentre);
            Assert.Equal((selected.Position.X.Negate(), selected.Position.Z), (tie.Position.X, tie.Position.Z));
            Assert.Equal("start-a", MapMarkerAnchors.Select(anchors.Reverse().ToArray(), "start-role", boundsCentre)!.Id);

            // Missing prefab bytes refuse rather than anchoring on what remains.
            File.Delete(Path.Combine(root, closure.GetResource("hut").Reference.Path));
            Assert.Throws<MapDocumentException>(() => MapMarkerAnchors.Read(manifest, MarkerConsumerFixtures.Closure(manifest, root)));
        });
    }

    [Fact]
    public void NativeWindowing_SelectsMarkerTileAndOpenKeepsRefusal()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapDocumentWindowing.Load(root, new MapDocumentLoadOptions(), 1, 0, MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit,
                out bool windowed, out MapTileRect? window, "start-role", m => MapMarkerAnchors.Read(m, MarkerConsumerFixtures.Closure(m, root)));
            Assert.True(windowed);
            Assert.Equal(-2, (window!.Value.Min.X + window.Value.Max.X) / 2);
            Assert.Equal(0, (window.Value.Min.Z + window.Value.Max.Z) / 2);

            // The released overload keeps its bounds-centre window, and so does an unmatched role.
            MapDocumentWindowing.Load(root, new MapDocumentLoadOptions(), 1, 0, out bool releasedWindowed, out MapTileRect? released);
            Assert.True(releasedWindowed);
            Assert.Equal(new MapTileCoord(0, 0), released!.Value.Min);
            MapDocumentWindowing.Load(root, new MapDocumentLoadOptions(), 1, 0, MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit,
                out _, out MapTileRect? unmatched, "unknown-role", m => MapMarkerAnchors.Read(m, MarkerConsumerFixtures.Closure(m, root)));
            Assert.Equal(new MapTileCoord(0, 0), unmatched!.Value.Min);

            var session = new MapEditSession { EditorWindowRadius = 0 };
            var staged = new MapMarkerConsumerOptions(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner);
            session.Open(root, null, staged);
            string stateBefore = MarkerEditorFixtures.State(session);
            session.WholeWorldTileLimit = 1;
            var windowedOpen = Assert.Throws<MapDocumentException>(() =>
                session.Open(root, null, new(MarkerConsumerFixtures.Roles(), null)));
            Assert.Contains("Windowed native editing is not supported yet", windowedOpen.Message, StringComparison.Ordinal);
            Assert.Equal(stateBefore, MarkerEditorFixtures.State(session));
            Assert.Same(staged, session.MarkerOptions);

            session.WholeWorldTileLimit = MapDocumentWindowing.DefaultWholeWorldTileLimit;
            OpenResult wholeOpen = session.Open(root, null, new(MarkerConsumerFixtures.Roles(), null));
            Assert.Equal(new[] { "start-a", "start-tie" }, wholeOpen.Summary.Markers!.EnabledPlayerStartIds);

            // The metadata anchor is the resolved marker's exact XZ.
            MapEffectiveMarker resolved = MarkerEditorFixtures.Markers(session).Single(m => m.Id == "start-a");
            MapMarkerAnchor anchor = MapMarkerAnchors.Read(PrefabEditorFixtures.Document(session), MarkerConsumerFixtures.Closure(
                PrefabEditorFixtures.Document(session), root)).Single(a => a.Id == "start-a");
            Assert.Equal(new MapExactXz(resolved.Position.X, resolved.Position.Z), anchor.Position);
        });
    }

    [Fact]
    public void NativeWholeLoadReadouts_UseNamedSupport()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            var session = new MapEditSession();
            session.Open(root, null, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner));
            MapSummary summary = session.Summary();
            Assert.Equal(new[] { "start-a", "start-disabled", "start-tie" }, summary.Markers!.PlayerStartIds);
            Assert.Equal(new[] { "start-a", "start-tie" }, summary.Markers.EnabledPlayerStartIds);
            Assert.Equal("start-role", summary.Markers.PlayerStartRole);
            Assert.Equal((3, 2), (summary.Markers.Total, summary.Markers.Enabled));
            Assert.Equal((1, 1), (summary.SpawnCount, summary.PlayerSpawnCount));
            Assert.Equal(new[] { "old" }, summary.PlayerSpawnIds);

            var query = new QueryService(session);
            Assert.Equal(900f, query.GroundHeight(0.5f, 0.5f).Height);
            PlacementsInRectResult rect = query.PlacementsInRect(-2, -2, 2, 2);
            PlacementEntry nativePlacement = Assert.Single(rect.Placements);
            MapResolvedPlacement actual = session.WithNativeAssets(c =>
                MapNativeResolution.Resolve(c.Document, c.Assets, NativeDocumentService.SessionOptionsV2)).Placements.Single(p => p.PlacementId == "bound");
            Assert.Equal(20f, nativePlacement.Y);
            Assert.Equal(actual.Transform.Position, new Vector3(nativePlacement.X, nativePlacement.Y, nativePlacement.Z));
            Assert.False(nativePlacement.ExplicitY);

            SpawnEntry nativeSpawn = Assert.Single(rect.Spawns);
            Assert.Equal(20f, nativeSpawn.GroundY);
            Assert.Equal(MapSupportStatus.Supported, nativeSpawn.Support!.Status);
            Assert.Equal(MarkerConsumerFixtures.SupportOwner, nativeSpawn.Support.Face!.Value.OwnerId);
            PlayerSpawnEntry nativeStart = Assert.Single(rect.PlayerSpawns);
            Assert.Equal((20f, MapSupportStatus.Supported), (nativeStart.GroundY, nativeStart.Support!.Status));

            // An effective prefab placement reads its resolved world transform, and a native rectangle includes its edges.
            PlacementEntry post = Assert.Single(query.PlacementsInRect(-66, 0, -63.5f, 1).Placements);
            MapResolvedPlacement postActual = session.WithNativeAssets(c =>
                MapNativeResolution.Resolve(c.Document, c.Assets, NativeDocumentService.SessionOptionsV2)).Placements.Single(p => p.PlacementId == post.Id);
            Assert.Equal("prefab/start-frame/Placement/post", post.Id);
            Assert.Equal((-63.5f, 3f, 0f), (post.X, post.Y, post.Z));
            Assert.Equal(postActual.Transform, new MapTransform(new Vector3(post.X, post.Y, post.Z), post.Yaw, post.Scale));
            Assert.True(post.ExplicitY);
            Assert.Equal(new[] { "edge" }, post.Tags);
            Assert.Empty(query.PlacementsInRect(-66, 0.0001f, -63.5f, 1).Placements);

            // Adopted-style starts read through typed views, on their own bound support.
            var registry = new MapMarkerRegistry(MarkerEditorFixtures.Markers(session));
            MapPlayerStartView start = MapMarkerProjection.PlayerStarts(registry, "start-role", true).Single(v => v.Marker.Id == "start-a");
            Assert.Equal(MapExactValue.FromSingle(20f), start.Marker.Position.Y);
            Assert.Equal(MapExactValue.FromSingle(-64.25f), start.Marker.Position.X);

            // A point the named owner does not cover stays a typed refusal, never another floor.
            MapSupportResult uncovered = session.WithNativeAssets(c => MapNativePointReadout.SurfaceAt(
                MapPrefabResolver.Expand(c.Document, c.Assets), new(MapExactValue.FromSingle(10.5f), MapExactValue.FromSingle(10.5f)),
                MarkerConsumerFixtures.SupportOwner));
            Assert.NotEqual(MapSupportStatus.Supported, uncovered.Status);

            // Without a Surface owner the spawn readout refuses, and a rectangle without old spawns still reads.
            var bare = new MapEditSession();
            bare.Open(root, null, new(MarkerConsumerFixtures.Roles(), null));
            string missingChoiceMessage = Assert.Throws<MapDocumentException>(() => new QueryService(bare).PlacementsInRect(-2, -2, 2, 2)).Message;
            Assert.Contains("spawns_adopt", missingChoiceMessage, StringComparison.Ordinal);
            Assert.Contains("marker_set", missingChoiceMessage, StringComparison.Ordinal);
            Assert.Equal(20f, Assert.Single(new QueryService(bare).PlacementsInRect(0, 0, 2, 2).Placements).Y);

            // The released open stages no role, so the summary lists no starts.
            var released = new MapEditSession();
            MapMarkerSummary unnamed = released.Open(root).Summary.Markers!;
            Assert.Equal((3, (string?)null), (unnamed.Total, unnamed.PlayerStartRole));
            Assert.Empty(unnamed.PlayerStartIds);
        });
    }

    [Fact]
    public void NativeSpawnReadout_RefusesUnsupportedSamples()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root, "uncovered-spawn");
            var session = new MapEditSession();
            session.Open(root, null, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner));
            var query = new QueryService(session);

            // Control: the covered spawns still read their supported height.
            SpawnEntry covered = Assert.Single(query.PlacementsInRect(-2, -2, 2, 2).Spawns);
            Assert.Equal((20f, MapSupportStatus.Supported), (covered.GroundY, covered.Support!.Status));

            MapSupportResult sample = session.WithNativeAssets(c => MapNativePointReadout.SurfaceAt(MapPrefabResolver.Expand(c.Document, c.Assets),
                new(MapExactValue.FromSingle(10.5f), MapExactValue.FromSingle(10.5f)), MarkerConsumerFixtures.SupportOwner));
            Assert.NotEqual(MapSupportStatus.Supported, sample.Status);
            string message = Assert.Throws<MapDocumentException>(() => query.PlacementsInRect(10, 10, 11, 11)).Message;
            Assert.Contains($"legacy Spawn 'uncovered' support status {sample.Status} on Surface 'adoption-floor'", message, StringComparison.Ordinal);
            Assert.Contains("legacySpawnSurfaceId 'adoption-floor'", message, StringComparison.Ordinal);
            Assert.Contains("spawns_adopt", message, StringComparison.Ordinal);
            Assert.Contains("marker_set", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NativeSurfaceReadout_OverflowStaysTyped()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            var session = new MapEditSession();
            session.Open(root, null, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner));
            MapSupportResult result = session.WithNativeAssets(c => MapNativePointReadout.SurfaceAt(MapPrefabResolver.Expand(c.Document, c.Assets),
                new(new MapExactValue(long.MaxValue, 1), MapExactValue.FromSingle(0.5f)), MarkerConsumerFixtures.SupportOwner));
            Assert.Equal(MapSupportStatus.NotRepresentable, result.Status);
            Assert.False(result.IsCaptureSupport);
            Assert.False(result.Witness.Complete);
        });
    }

    [Fact]
    public void NativeWindowing_MatchedRoleWinsOverAnEnabledLegacyStart()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root, "enabled-legacy-start");
            MapTileRect? Window(string role)
            {
                MapDocumentWindowing.Load(root, new MapDocumentLoadOptions(), 1, 0, MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit,
                    out _, out MapTileRect? window, role, m => MapMarkerAnchors.Read(m, MarkerConsumerFixtures.Closure(m, root)));
                return window;
            }
            Assert.Equal(new MapTileCoord(-2, 0), Window("start-role")!.Value.Min);
            // An unmatched role falls back to the released legacy player spawn search, as the released overload does.
            Assert.Equal(new MapTileCoord(-1, -1), Window("unknown-role")!.Value.Min);
            MapDocumentWindowing.Load(root, new MapDocumentLoadOptions(), 1, 0, out _, out MapTileRect? released);
            Assert.Equal(new MapTileCoord(-1, -1), released!.Value.Min);
        });
    }

    [Fact]
    public void AnalyticReadouts_KeepFieldHeightsAndInclusiveEdges()
    {
        WithRoot(root =>
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "analytic.map.json");
            var document = new MapDocument
            {
                Id = "analytic-readout",
                Bounds = new MapBounds { MinX = -64, MinZ = -64, MaxX = 64, MaxZ = 64 },
                Terrain = { Biomes = { new MapBiomeBand { BaseHeight = 4, HillAmplitude = 3 } } },
            };
            document.Placements.Add(new MapPlacement { Id = "edge", Kind = "rock", X = -20, Z = 30 });
            document.Spawns.Add(new MapSpawn { Id = "npc", ArchetypeId = "kit.actor", X = -40, Z = 10 });
            document.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = -20, Z = 10 });
            MapDocumentFile.Save(document, path);
            var session = new MapEditSession();
            Assert.Null(session.Open(path, null, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner)).Summary.Markers);

            PlacementsInRectResult rect = new QueryService(session).PlacementsInRect(-40, 10, -20, 30);
            var field = session.Field();
            Assert.Equal(field.SampleHeight(-20, 30), Assert.Single(rect.Placements).Y);
            SpawnEntry npc = Assert.Single(rect.Spawns);
            Assert.Equal((field.SampleHeight(-40, 10), (MapSupportResult?)null), (npc.GroundY, npc.Support));
            PlayerSpawnEntry start = Assert.Single(rect.PlayerSpawns);
            Assert.Equal((field.SampleHeight(-20, 10), (MapSupportResult?)null), (start.GroundY, start.Support));
        });
    }

    [Fact]
    public void MarkerListRect_BuildsTheCompleteViewOnlyForCandidates()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            var session = new MapEditSession();
            session.Open(root, null, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner));
            var query = new QueryService(session);
            Assert.Empty(query.MarkerList(null, null, null, false, new MapMarkerRect(100, 100, 110, 110), out bool emptyBuilt));
            Assert.False(emptyBuilt);
            Assert.Equal("start-tie", Assert.Single(query.MarkerList(null, null, null, false, new MapMarkerRect(64, 0, 65, 1), out bool hitBuilt)).Id);
            Assert.True(hitBuilt);
        });
    }

    static void WithRoot(Action<string> test)
    {
        string root = MarkerConsumerFixtures.TempRoot();
        try { test(root); }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
