using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>GUI spawn billboards, spawn picking, spawn focus and the camera start read native documents from named
/// authored support, never from the analytic terrain field. Headless, with no device.</summary>
public sealed class NativeMarkerReadoutTests
{
    static readonly Vector3 CameraOffset = new(0, 24, -32);
    readonly MouseFrames _mouse = new();

    [Fact]
    public void NativeGuiReadouts_UseNamedSupportAndNeverTerrainField()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapDocument document = MapDocumentFile.LoadTiled(root);
            MapAssetClosure closure = MarkerConsumerFixtures.Closure(document, root);
            var readouts = new NativeMarkerReadouts(document, closure, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner));
            MapSpawn spawn = Assert.Single(document.Spawns);
            var visibility = new EditorVisibility();

            // The support answer first, on the named owner and not the upper floor at 70.
            MapSupportResult support = readouts.LegacySpawnHeight(spawn.X, spawn.Z);
            Assert.Equal(MapSupportStatus.Supported, support.Status);
            Assert.Equal(MarkerConsumerFixtures.SupportOwner, support.Face!.Value.OwnerId);
            Assert.Equal(20f, readouts.LegacySpawnHeight(spawn.X, spawn.Z).WorldY);
            Assert.Null(readouts.Diagnostic);

            // Then presentation, support plus the released lift, with the analytic 900 never consulted.
            long expansions = MapPrefabResolver.ThreadExpansions;
            SpawnBillboardFrame frame = SpawnBillboards.Build(document, visibility, (x, z) => 900f, readouts);
            SpawnBillboard billboard = frame.Billboards.Single(b => b.Kind == SelectionKind.Spawn && b.Id == spawn.Id);
            Assert.Equal(20f + SpawnBillboards.Lift, billboard.Position.Y);
            Assert.Equal(21f, frame.Billboards.Single(b => b.Kind == SelectionKind.PlayerSpawn).Position.Y);
            Assert.Empty(frame.Diagnostics);
            var submitted = new List<(Vector3 Position, float Size, Color Color)>();
            ViewportWorld.SubmitSpawnBillboards(frame, (p, s, c) => submitted.Add((p, s, c)));
            Assert.Equal(frame.Billboards.Select(b => b.Position), submitted.Select(s => s.Position));
            Assert.Contains(submitted, s => s.Position.Y == 21f);
            Assert.All(submitted, s => Assert.Equal(0.6f, s.Size));
            // One snapshot serves every billboard and every later frame.
            SpawnBillboards.Build(document, visibility, (x, z) => 900f, readouts);
            Assert.Equal(expansions, MapPrefabResolver.ThreadExpansions);

            // Camera focus through the headless scene: F on the selected legacy spawn.
            (MarkerScene scene, SceneManager manager, Pointer pointer) = Push(document,
                Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner), closure));
            Assert.Equal(900f, scene.Controller.Field!.SampleHeight(spawn.X, spawn.Z));
            scene.Document.Selection.Set(SelectionKind.Spawn, spawn.Id);
            PressF(manager, pointer);
            Assert.Equal(20f + SpawnBillboards.FocusLift, scene.NavigationPivot!.Value.Y);
            Assert.Equal(20.75f, scene.NavigationPivot.Value.Y);
            Assert.Equal((spawn.X, spawn.Z), (scene.NavigationPivot.Value.X, scene.NavigationPivot.Value.Z));

            // The scene's viewport draws the configured row at 21.
            SpawnBillboardFrame drawn = scene.SceneViewport.SpawnFrame(scene.Visibility);
            Assert.Equal(21f, drawn.Billboards.Single(b => b.Kind == SelectionKind.Spawn && b.Id == spawn.Id).Position.Y);
            Assert.Empty(drawn.Diagnostics);

            // The spawn pick box sits at the drawn height 21, not at the analytic 900.
            var flat = new TerrainField(new TerrainConfig { GentleAmplitude = 0f, DetailOctaves = 0 });
            Assert.True(EditorPicking.Pick(document, flat, new Vector3(spawn.X - 5f, 21f, spawn.Z), Vector3.UnitX, 100f, _ => 1f,
                out EditorPicking.PickResult hit, null, null, null, scene.SpawnFrame()));
            Assert.Equal((SelectionKind.Spawn, spawn.Id, 21f), (hit.Kind, hit.Id, hit.Point.Y));
            EditorPicking.Pick(document, flat, new Vector3(spawn.X - 5f, 900.5f, spawn.Z), Vector3.UnitX, 100f, _ => 1f,
                out EditorPicking.PickResult high, null, null, null, scene.SpawnFrame());
            Assert.Equal(SelectionKind.None, high.Kind);

            // Without a Surface owner the row is omitted with one diagnostic, and focus leaves the pivot unchanged.
            var bare = new NativeMarkerReadouts(document, closure, new(MarkerConsumerFixtures.Roles(), null));
            MapSupportResult unavailable = bare.LegacySpawnHeight(spawn.X, spawn.Z);
            Assert.NotEqual(MapSupportStatus.Supported, unavailable.Status);
            // One unavailable answer serves every row of the snapshot, so a steady frame builds no result per row.
            Assert.Same(unavailable, bare.LegacySpawnHeight(spawn.X + 1f, spawn.Z));
            bare.Refresh();
            Assert.NotSame(unavailable, bare.LegacySpawnHeight(spawn.X, spawn.Z));
            SpawnBillboardFrame unconfigured = SpawnBillboards.Build(document, visibility, (x, z) => 900f, bare);
            Assert.DoesNotContain(unconfigured.Billboards, b => b.Id == spawn.Id);
            Assert.Contains("spawns_adopt", Assert.Single(unconfigured.Diagnostics));

            MapDocument second = MapDocumentFile.LoadTiled(root);
            (MarkerScene unconfiguredScene, SceneManager unconfiguredManager, Pointer unconfiguredPointer) = Push(second,
                Options(root, new(MarkerConsumerFixtures.Roles(), null), closure));
            unconfiguredScene.Document.Selection.Set(SelectionKind.Spawn, spawn.Id);
            Vector3? pivotBefore = unconfiguredScene.NavigationPivot;
            PressF(unconfiguredManager, unconfiguredPointer);
            Assert.Equal(pivotBefore, unconfiguredScene.NavigationPivot);
            Assert.Contains("spawns_adopt", unconfiguredScene.StatusText, StringComparison.Ordinal);
            SpawnBillboardFrame omitted = unconfiguredScene.SceneViewport.SpawnFrame(unconfiguredScene.Visibility);
            Assert.DoesNotContain(omitted.Billboards, b => b.Id == spawn.Id);
            Assert.Contains("spawns_adopt", Assert.Single(omitted.Diagnostics));

            // With no marker options at all, the native readouts are unavailable and nothing falls back to the field.
            (MarkerScene noOptions, _, _) = Push(MapDocumentFile.LoadTiled(root), Options(root, null, closure));
            Assert.Null(noOptions.MarkerReadouts);
            Assert.Contains("MarkerOptions", noOptions.MarkerDiagnostic, StringComparison.Ordinal);
            SpawnBillboardFrame none = noOptions.SceneViewport.SpawnFrame(noOptions.Visibility);
            Assert.Empty(none.Billboards);
            Assert.Contains("spawns_adopt", Assert.Single(none.Diagnostics));
        });
    }

    [Fact]
    public void AnalyticBillboards_KeepTheReleasedGroundAndLift()
    {
        var document = new MapDocument
        {
            Id = "analytic-billboards",
            Bounds = new MapBounds { MinX = -64f, MinZ = -64f, MaxX = 64f, MaxZ = 64f },
        };
        document.Spawns.Add(new MapSpawn { Id = "wolf-1", ArchetypeId = "wolf", X = 3.5f, Z = -7.25f, Enabled = true });
        document.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1", X = -11f, Z = 9.5f, Enabled = false });
        TerrainField field = MapRuntime.BuildField(document, MapDocRegistry.CreateDefault());
        var visibility = new EditorVisibility();

        SpawnBillboardFrame frame = SpawnBillboards.Build(document, visibility, field.SampleHeight, null);
        var submitted = new List<(Vector3 Position, float Size, Color Color)>();
        ViewportWorld.SubmitSpawnBillboards(frame, (p, s, c) => submitted.Add((p, s, c)));

        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new[] { SelectionKind.Spawn, SelectionKind.PlayerSpawn }, frame.Billboards.Select(b => b.Kind));
        SpawnBillboard analyticRow = frame.Billboards[0];
        float analyticGround = field.SampleHeight(3.5f, -7.25f);
        Assert.Equal(analyticGround + 1.0f, analyticRow.Position.Y);
        Assert.Equal(new Vector3(-11f, field.SampleHeight(-11f, 9.5f) + 1.0f, 9.5f), frame.Billboards[1].Position);
        Assert.Equal(frame.Billboards.Select(b => b.Position), submitted.Select(s => s.Position));
        Assert.All(submitted, s => Assert.Equal(0.6f, s.Size));
        Assert.NotEqual(submitted[0].Color, submitted[1].Color);
        Assert.Equal(analyticGround, SpawnBillboards.FocusGround(document, SelectionKind.Spawn, "wolf-1", field.SampleHeight, null, out string? why));
        Assert.Null(why);

        // The released group and element gates.
        visibility.SetElementHidden(SelectionKind.Spawn, "wolf-1", true);
        Assert.Equal("player-1", Assert.Single(SpawnBillboards.Build(document, visibility, field.SampleHeight, null).Billboards).Id);
        visibility.SetGroup(VisibilityGroup.PlayerSpawns, false);
        Assert.Empty(SpawnBillboards.Build(document, visibility, field.SampleHeight, null).Billboards);

        // The public pick keeps the released box, based at the sampled ground.
        Assert.True(EditorPicking.Pick(document, field, new Vector3(3.5f, analyticGround + 1.4f, -12f), Vector3.UnitZ, 20f, _ => 1f,
            out EditorPicking.PickResult hit));
        Assert.Equal((SelectionKind.Spawn, "wolf-1"), (hit.Kind, hit.Id));
    }

    [Fact]
    public void WholeNativeLoad_StartsCameraAtCallerStart()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapAssetClosure closure = MarkerConsumerFixtures.Closure(MapDocumentFile.LoadTiled(root), root);
            (MarkerScene scene, _, _) = Push(null,
                Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner), closure));
            Assert.Null(scene.Window);
            MapPlayerStartView start = scene.MarkerReadouts!.PlayerStarts(enabledOnly: true).Single(s => s.Marker.Id == "start-a");
            Assert.Equal(new Vector3(-64.25f, 20f, 0.5f), new Vector3(start.Marker.Position.X.ToSingle(),
                start.Marker.Position.Y.ToSingle(), start.Marker.Position.Z.ToSingle()));
            Assert.Equal(new Vector3(-64.25f, 20f, 0.5f) + CameraOffset, scene.Camera.Position);

            (MarkerScene unknown, _, _) = Push(null,
                Options(root, new(new("unknown-role", "spawn-role", "landmark-role"), MarkerConsumerFixtures.SupportOwner), closure));
            Assert.Equal(CameraOffset, unknown.Camera.Position);

            (MarkerScene released, _, _) = Push(null, Options(root, null, closure));
            Assert.Equal(CameraOffset, released.Camera.Position);
        });
    }

    [Fact]
    public void MarkerConsumers_RefreshAfterUndoRedoAndReload()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapAssetClosure closure = MarkerConsumerFixtures.Closure(MapDocumentFile.LoadTiled(root), root);
            (MarkerScene scene, SceneManager manager, Pointer pointer) = Push(null,
                Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner), closure));
            scene.Document.BindNativeAssets(closure);
            scene.Document.Selection.Set(SelectionKind.Spawn, "old");
            NativeMarkerReadouts opened = scene.MarkerReadouts!;

            void Expect(Vector3 tie, float spawnGround, bool viewportFrame = true)
            {
                MapPlayerStartView start = scene.MarkerReadouts!.PlayerStarts(enabledOnly: true).Single(s => s.Marker.Id == "start-tie");
                Assert.Equal(tie, new Vector3(start.Marker.Position.X.ToSingle(), start.Marker.Position.Y.ToSingle(),
                    start.Marker.Position.Z.ToSingle()));
                // The headless viewport stays bound to the opened document, so after a reload only the scene frame reads.
                if (viewportFrame)
                {
                    SpawnBillboardFrame frame = scene.SceneViewport.SpawnFrame(scene.Visibility);
                    Assert.Equal(spawnGround + SpawnBillboards.Lift, frame.Billboards.Single(b => b.Kind == SelectionKind.Spawn).Position.Y);
                }
                Assert.Equal(spawnGround + SpawnBillboards.Lift, scene.SpawnFrame().Billboards.Single(b => b.Kind == SelectionKind.Spawn).Position.Y);
                PressF(manager, pointer);
                Assert.Equal(spawnGround + SpawnBillboards.FocusLift, scene.NavigationPivot!.Value.Y);
            }

            Vector3 explicitTie = new(64.25f, 20f, 0.5f);
            Expect(explicitTie, 20f);

            // A bound move, then a support edit under both the moved start and the legacy spawn.
            var bound = MapMarkerHeights.Apply(new MapLocalMarkerDoc(1, "start-tie", "start-tie-name", new Vector3(-0.5f, 0, -0.5f), 0,
                MarkerConsumerFixtures.StartRole, true, Array.Empty<string>()), new(null, MarkerFixtures.SurfaceBinding(MarkerConsumerFixtures.SupportOwner)));
            scene.Document.Execute(new MarkerEditCommand(new MapSetMarker(bound)));
            Expect(new Vector3(-0.5f, 20f, -0.5f), 20f);
            MapPatchKey patch = scene.Document.Doc.Surfaces.Patches[new(MarkerConsumerFixtures.SupportOwner, -1, -1)].Key;
            scene.Document.Execute(new TerrainEditCommand(new MapSetCornerHeights(patch, 0, 0, 3, 3, Enumerable.Repeat(2100, 9).ToArray())));
            Expect(new Vector3(-0.5f, 21f, -0.5f), 21f);

            Assert.True(scene.Document.Undo());
            Expect(new Vector3(-0.5f, 20f, -0.5f), 20f);
            Assert.True(scene.Document.Undo());
            Expect(explicitTie, 20f);
            Assert.True(scene.Document.Redo());
            Assert.True(scene.Document.Redo());
            Expect(new Vector3(-0.5f, 21f, -0.5f), 21f);
            Assert.Same(opened, scene.MarkerReadouts);

            // Reload reads the unchanged disk state into fresh readouts.
            scene.Document.MarkSaved();
            Assert.True(scene.ReloadDocument(), scene.StatusText);
            Assert.NotSame(opened, scene.MarkerReadouts);
            scene.Document.Selection.Set(SelectionKind.Spawn, "old");
            Assert.False(scene.SceneViewport.IsBuilt);
            Expect(explicitTie, 20f, viewportFrame: false);
        });
    }

    /// <summary>A Select-mode click through an input frame picks the resolver-2 legacy spawn on its drawn support
    /// height, before and after a reload, so the scene's spawn frame reaches both controller wirings.</summary>
    [Fact]
    public void SelectClick_PicksNativeSpawnThroughTheSceneFrame()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapAssetClosure closure = MarkerConsumerFixtures.Closure(MapDocumentFile.LoadTiled(root), root);
            (MarkerScene scene, SceneManager manager, Pointer pointer) = Push(null,
                Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner), closure));
            // A flat field keeps the terrain off the ray, so only a spawn box can be hit.
            var flat = new TerrainField(new TerrainConfig { GentleAmplitude = 0f, DetailOctaves = 0 });
            for (int pass = 0; pass < 2; pass++)
            {
                MapSpawn spawn = Assert.Single(scene.Document.Doc.Spawns);
                scene.Controller.Field = flat;
                scene.Document.Selection.Clear();
                Click(scene, manager, pointer, new Vector3(spawn.X, 20f + SpawnBillboards.Lift, spawn.Z));
                Assert.Equal((SelectionKind.Spawn, spawn.Id), (scene.Document.Selection.Kind, scene.Document.Selection.Id));
                if (pass == 0) Assert.True(scene.ReloadDocument(), scene.StatusText);
            }
        });
    }

    /// <summary>A host closure delegate that throws leaves the readouts unavailable with its diagnostic, and the frame
    /// draws no native legacy spawn rather than failing.</summary>
    [Fact]
    public void HostClosureFailure_ShowsUnavailable()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapEditorOptions options = Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner),
                MarkerConsumerFixtures.Closure(MapDocumentFile.LoadTiled(root), root));
            options.MarkerAssets = _ => throw new NotSupportedException("host closure offline");
            (MarkerScene scene, _, _) = Push(MapDocumentFile.LoadTiled(root), options);
            Assert.Null(scene.MarkerReadouts);
            Assert.Contains("host closure offline", scene.MarkerDiagnostic, StringComparison.Ordinal);
            Assert.Empty(scene.SpawnFrame().Billboards);
        });
    }

    [Fact]
    public void GuiWindow_UsesCallerStartRole()
    {
        WithRoot(root =>
        {
            MarkerConsumerFixtures.WindowedNative(root);
            MapAssetClosure closure = MarkerConsumerFixtures.Closure(MapDocumentFile.LoadTiled(root), root);
            MapEditorOptions options = Options(root, new(MarkerConsumerFixtures.Roles(), MarkerConsumerFixtures.SupportOwner), closure);
            options.WholeWorldTileLimit = 1;
            options.EditorWindowRadius = 0;
            (MarkerScene scene, _, _) = Push(null, options);

            MapTileRect window = scene.Window!.Value;
            Assert.Equal((-2, -2), (window.Min.X, window.Max.X));
            Assert.Equal(new Vector3(-64.25f, 0f, 0.5f) + CameraOffset, scene.Camera.Position);
            Assert.True(scene.Document.Doc.Tiles!.IsPartial);
            Assert.Contains("windowed", scene.MarkerDiagnostic, StringComparison.Ordinal);
            Assert.Empty(scene.MarkerReadouts!.PlayerStarts(enabledOnly: false));
            Assert.Empty(scene.SceneViewport.SpawnFrame(scene.Visibility).Billboards);

            // The released windowed start with no caller role centres on the bounds instead.
            (MarkerScene released, _, _) = Push(null, WithLimits(Options(root, null, closure)));
            Assert.Equal((0, 0), (released.Window!.Value.Min.X, released.Window.Value.Max.X));
            Assert.Equal(CameraOffset, released.Camera.Position);
        });
    }

    static MapEditorOptions WithLimits(MapEditorOptions options)
    {
        options.WholeWorldTileLimit = 1;
        options.EditorWindowRadius = 0;
        return options;
    }

    static MapEditorOptions Options(string root, MapMarkerConsumerOptions? markerOptions, MapAssetClosure closure) => new()
    {
        DocumentPath = root,
        MarkerOptions = markerOptions,
        MarkerAssets = _ => closure,
    };

    static (MarkerScene Scene, SceneManager Manager, Pointer Pointer) Push(MapDocument? document, MapEditorOptions options)
    {
        var scene = new MarkerScene(document);
        scene.Init(null!, null!, null!, options);
        var pointer = new Pointer();
        var manager = new SceneManager { Pointer = pointer, UiViewport = new UiViewport(960, 540, 960, 540) };
        manager.Push(scene);
        return (scene, manager, pointer);
    }

    // Looks along +X at the target from 5 units west, then presses and releases the left button where it projects.
    void Click(MarkerScene scene, SceneManager manager, Pointer pointer, Vector3 target)
    {
        scene.Camera.Position = target - new Vector3(5f, 0f, 0f);
        scene.Camera.Yaw = MathF.PI / 2f;
        scene.Camera.Pitch = 0f;
        Assert.True(scene.Camera.WorldToScreen(target, 960, 540, out Vector2 at));
        foreach (bool down in new[] { true, false })
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(held);
            var frame = new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(), held, pressed,
                at, default, 0f, 960, 540, windowFocused: true, mouseReleased: released);
            pointer.Update(frame);
            manager.Input = frame;
            manager.Update(0.016f);
        }
    }

    void PressF(SceneManager manager, Pointer pointer)
    {
        var held = new HashSet<MouseButton>();
        var (pressed, released) = _mouse.Advance(held);
        var frame = new InputState(new HashSet<Key> { Key.F }, new HashSet<Key> { Key.F }, new HashSet<Key>(), held, pressed,
            new Vector2(480f, 270f), default, 0f, 960, 540, windowFocused: true, mouseReleased: released);
        pointer.Update(frame);
        manager.Input = frame;
        manager.Update(0.016f);
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

    // Headless: the analytic field comes from the document, as the viewport builds it, and the viewport is bound to the
    // same document and field with no device.
    sealed class MarkerScene(MapDocument? document) : MapEditorScene
    {
        protected override MapDocument CreateDocument(MapDocRegistry registry) => document ?? base.CreateDocument(registry);

        protected override void BuildWorld()
        {
            Controller.Field = MapRuntime.BuildField(Document.Doc, Document.Registry);
            SceneViewport.BindHeadless(Document.Doc, Controller.Field);
        }

        protected override void TeardownWorld() { }
        protected override void UpdateStreaming(float dt) { }
        protected override bool RebuildWorld() => true;
    }
}
