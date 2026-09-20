using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Deterministic CPU-only editor workloads. Durations are observations, never CI thresholds.</summary>
public sealed class MapEditorResponsivenessTests
{
    const int WarmupSamples = 3;
    const int MeasuredSamples = 21;
    const int NavigationOperations = 64;
    const int EditOperations = 8;

    static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
    static readonly IReadOnlySet<Key> Shift = new HashSet<Key> { Key.LeftShift };
    static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();
    static readonly IReadOnlySet<MouseButton> Middle = new HashSet<MouseButton> { MouseButton.Middle };

    readonly ITestOutputHelper _output;

    public MapEditorResponsivenessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SceneSamplesTerrainOnlyForPivotAcquisitionAndUncapturedDolly()
    {
        MapDocument document = LoadProfileDocument(out _);
        var scene = new ProfileScene(document);
        var manager = new SceneManager
        {
            Pointer = new Pointer(),
            UiViewport = new UiViewport(960, 540, 960, 540),
        };
        scene.Init(null!, null!, null!, new MapEditorOptions());
        manager.Push(scene);
        string hash = MapDocumentHash.OfWorld(scene.Document.Doc);
        Vector3 initialPosition = scene.Camera.Position;

        for (int i = 0; i < 10; i++)
            Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero));
        Assert.Equal(0, scene.NavigationTerrainSamples);

        Step(manager, Frame(NoKeys, Middle, Middle, Vector2.Zero));
        Assert.Equal(1, scene.NavigationTerrainSamples);
        for (int i = 0; i < 10; i++)
        {
            float x = (i & 1) == 0 ? 2f : -2f;
            Step(manager, Frame(NoKeys, Middle, NoButtons, new Vector2(x, 0f)));
        }
        Assert.Equal(1, scene.NavigationTerrainSamples);
        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, released: Middle));

        Step(manager, Frame(Shift, Middle, Middle, Vector2.Zero));
        Assert.Equal(2, scene.NavigationTerrainSamples);
        for (int i = 0; i < 10; i++)
        {
            float x = (i & 1) == 0 ? 2f : -2f;
            Step(manager, Frame(Shift, Middle, NoButtons, new Vector2(x, 0f)));
        }
        Assert.Equal(2, scene.NavigationTerrainSamples);
        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, released: Middle));

        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, scroll: 1f));
        Assert.Equal(3, scene.NavigationTerrainSamples);
        Assert.True(Vector3.Distance(scene.Camera.Position, initialPosition) > 0.1f);
        Assert.Equal(hash, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.False(scene.Document.History.CanUndo);
    }

    [Fact]
    public void ReleasedCaptureResolvesFreshHitBeforeDollyOrOtherButtonAcquisition()
    {
        MapDocument document = FlatProfileDocument();
        var scene = new ProfileScene(document);
        var manager = new SceneManager
        {
            Pointer = new Pointer(),
            UiViewport = new UiViewport(960, 540, 960, 540),
        };
        scene.Init(null!, null!, null!, new MapEditorOptions());
        manager.Push(scene);

        Step(manager, Frame(NoKeys, Middle, Middle, Vector2.Zero));
        Assert.Equal(1, scene.NavigationTerrainSamples);

        IReadOnlySet<MouseButton> right = new HashSet<MouseButton> { MouseButton.Right };
        Step(manager, Frame(NoKeys, right, right, Vector2.Zero, released: Middle, scroll: 1f));
        Assert.Equal(2, scene.NavigationTerrainSamples);

        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, released: right, scroll: 1f));
        Assert.Equal(3, scene.NavigationTerrainSamples);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void CpuOnlyWorkloadsReportTimingAllocationAndCorrectnessSeparately()
    {
        MapDocument document = LoadProfileDocument(out string workload);
        var scene = new ProfileScene(document);
        var manager = new SceneManager
        {
            Pointer = new Pointer(),
            UiViewport = new UiViewport(960, 540, 960, 540),
        };
        scene.Init(null!, null!, null!, new MapEditorOptions());
        manager.Push(scene);

        string originalHash = MapDocumentHash.OfWorld(scene.Document.Doc);
        WriteHeader(workload);

        InputState idle = Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero);
        Report(Measure("idle", NavigationOperations, () => Step(manager, idle)));

        var skyScene = new ProfileScene(FlatProfileDocument());
        var skyManager = new SceneManager
        {
            Pointer = new Pointer(),
            UiViewport = new UiViewport(960, 540, 960, 540),
        };
        skyScene.Init(null!, null!, null!, new MapEditorOptions());
        skyManager.Push(skyScene);
        skyScene.Camera.Pitch = 0.25f;
        Report(Measure("base-equivalent-idle-sky-miss", NavigationOperations, () =>
        {
            Step(skyManager, idle);
            _ = skyScene.ForceNavigationTerrainHit();
        }));

        InputState orbitPress = Frame(NoKeys, Middle, Middle, Vector2.Zero);
        Step(manager, orbitPress);
        int orbitDirection = 1;
        Report(Measure("orbit", NavigationOperations, () =>
        {
            orbitDirection = -orbitDirection;
            Step(manager, Frame(NoKeys, Middle, NoButtons, new Vector2(orbitDirection * 2f, 0f)));
        }));
        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, released: Middle));

        InputState panPress = Frame(Shift, Middle, Middle, Vector2.Zero);
        Step(manager, panPress);
        int panDirection = 1;
        Report(Measure("pan", NavigationOperations, () =>
        {
            panDirection = -panDirection;
            Step(manager, Frame(Shift, Middle, NoButtons, new Vector2(panDirection * 2f, 0f)));
        }));
        Step(manager, Frame(NoKeys, NoButtons, NoButtons, Vector2.Zero, released: Middle));

        Report(Measure("visibility-toggle", NavigationOperations, () =>
        {
            scene.Visibility.SetCategory(EditorPropCategory.Trees,
                !scene.Visibility.GetCategoryChoice(EditorPropCategory.Trees));
            scene.Visibility.SetLayer("profile-layer", !scene.Visibility.GetLayerChoice("profile-layer"));
        }));

        TerrainField field = scene.Controller.Field!;
        IReadOnlyList<EditorPlacement> authored = PlacementCache.Build(scene.Document.Doc, field);
        IReadOnlyList<EditorPlacement> visible = Array.Empty<EditorPlacement>();
        Report(Measure("authored-filter-all-visible", EditOperations, () =>
            visible = ViewportWorld.FilterVisiblePlacements(
                authored, scene.Visibility, static _ => true)));
        Assert.Same(authored, visible);

        string? selectedId = authored.Count == 0 ? null : authored[authored.Count / 2].Id;
        IReadOnlyList<EditorPlacement> unselected = Array.Empty<EditorPlacement>();
        Report(Measure("authored-partition", EditOperations, () =>
            (unselected, _) = ViewportWorld.Partition(authored, selectedId)));
        Assert.Equal(authored.Count - (selectedId is null ? 0 : 1), unselected.Count);

        IReadOnlyList<PropPlacement> legacyProps = Array.Empty<PropPlacement>();
        Report(Measure("base-equivalent-authored-prepare", EditOperations, () =>
        {
            IReadOnlyList<EditorPlacement> filtered = ViewportWorld.FilterVisiblePlacements(
                authored, scene.Visibility, static _ => true);
            (IReadOnlyList<EditorPlacement> legacyUnselected, _) =
                ViewportWorld.Partition(filtered, selectedId);
            var projected = new List<PropPlacement>(legacyUnselected.Count);
            for (int i = 0; i < legacyUnselected.Count; i++)
                projected.Add(legacyUnselected[i].Prop);
            legacyProps = projected;
        }));
        Assert.Equal(unselected.Count, legacyProps.Count);

        var authoredBuffer = new AuthoredPlacementBuffer();
        authoredBuffer.Prepare(authored, scene.Visibility, static _ => true, selectedId);
        Report(Measure("authored-prepare", EditOperations, () =>
            authoredBuffer.Prepare(authored, scene.Visibility, static _ => true, selectedId)));
        Assert.Equal(legacyProps, authoredBuffer.Unselected);

        var terrainOnlyCache = new PlacementCache();
        IReadOnlyList<EditorPlacement> rebuilt = Array.Empty<EditorPlacement>();
        Report(Measure("base-equivalent-terrain-only-dirty-cache", 1, () =>
        {
            terrainOnlyCache.Invalidate();
            rebuilt = terrainOnlyCache.Get(scene.Document.Doc, field);
        }));
        Assert.Equal(authored.Count, rebuilt.Count);
        scene.Visibility.TerrainOnly = true;
        Report(Measure("terrain-only-dirty-cache", EditOperations, () =>
        {
            terrainOnlyCache.Invalidate();
            authoredBuffer.Prepare(terrainOnlyCache, scene.Document.Doc, field,
                scene.Visibility, static _ => true, selectedId);
        }));
        Assert.True(terrainOnlyCache.IsDirty);
        Assert.Empty(authoredBuffer.Unselected);
        scene.Visibility.TerrainOnly = false;

        MapBounds mapBounds = scene.Document.Doc.Bounds;
        float sculptCellSize = scene.Document.Doc.TerrainOverrides?.CellSize
            ?? MapTerrainOverrides.DefaultCellSize;
        float centerX = (mapBounds.MinX + mapBounds.MaxX) * 0.5f;
        float centerZ = (mapBounds.MinZ + mapBounds.MaxZ) * 0.5f;
        var overlayLines = new SculptOverlayLine[SculptBrushOverlay.MaxLines];
        SculptBounds bounds = SculptBounds.FromBounds(mapBounds.MinX, mapBounds.MinZ,
            mapBounds.MaxX, mapBounds.MaxZ, sculptCellSize);
        Func<float, float, float> sampleHeight = field.SampleHeight;
        Func<float, float, bool> loaded = static (_, _) => true;
        Func<Vector2, Vector2, bool> segmentLoaded = static (_, _) => true;
        Report(Measure("overlay-geometry", NavigationOperations, () =>
        {
            int count = SculptBrushOverlay.Build(
                new Vector3(centerX, 0f, centerZ), 24f, bounds, sculptCellSize,
                sampleHeight, loaded, segmentLoaded, overlayLines);
            if (count <= 0) throw new InvalidOperationException("Profile overlay produced no geometry.");
        }));

        scene.Controller.Mode = EditorToolMode.SculptTerrain;
        scene.Controller.Brush = SculptBrush.Raise;
        scene.Controller.BrushRadius = 4f;
        scene.Controller.BrushStrength = 8f;
        Vector3 rayOrigin = new(centerX, 1000f, centerZ);
        var press = new EditorFrameInput(rayOrigin, -Vector3.UnitY,
            pointerPressed: true, pointerDown: true, dt: 0.016f);
        var drag = new EditorFrameInput(rayOrigin + new Vector3(2f, 0f, 2f), -Vector3.UnitY,
            pointerDown: true, dt: 0.016f);
        var release = new EditorFrameInput(rayOrigin + new Vector3(2f, 0f, 2f), -Vector3.UnitY,
            pointerReleased: true, dt: 0.016f);
        Report(Measure("sculpt-stroke", EditOperations, () =>
        {
            scene.Controller.Update(press);
            scene.Controller.Update(drag);
            scene.Controller.Update(release);
        }));
        Report(Measure("undo", EditOperations, () =>
        {
            if (!scene.Document.Undo()) throw new InvalidOperationException("Profile undo stack was empty.");
        }));

        Assert.Equal(0, scene.VisibilityRebuilds);
        Assert.Equal(originalHash, MapDocumentHash.OfWorld(scene.Document.Doc));
        Assert.False(scene.Document.History.CanUndo);
        Assert.True(Finite(scene.Camera.Position));
        _output.WriteLine($"correctness terrainSamples={scene.NavigationTerrainSamples} visibilityRebuilds={scene.VisibilityRebuilds} mapHashUnchanged=true undoDepth=0 camera=({scene.Camera.Position.X:R},{scene.Camera.Position.Y:R},{scene.Camera.Position.Z:R})");
    }

    void WriteHeader(string workload)
    {
        string commit = Environment.GetEnvironmentVariable("KE_MAP_EDITOR_PROFILE_COMMIT") ?? "unspecified";
        string machine = Environment.GetEnvironmentVariable("KE_MAP_EDITOR_PROFILE_MACHINE") ?? "unspecified";
        _output.WriteLine($"CPU-only profile commit={commit} runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} architecture={RuntimeInformation.ProcessArchitecture} machine={machine}");
        _output.WriteLine($"workload={workload} viewport=960x540 warmupSamples={WarmupSamples} measuredSamples={MeasuredSamples}");
    }

    void Report(Measurement result) => _output.WriteLine(
        $"operation={result.Name} medianUs={result.MedianMicroseconds:F3} p95Us={result.P95Microseconds:F3} medianAllocatedBytes={result.MedianAllocatedBytes:F1} p95AllocatedBytes={result.P95AllocatedBytes:F1}");

    static Measurement Measure(string name, int operations, Action action)
    {
        for (int sample = 0; sample < WarmupSamples; sample++) RunBatch(operations, action);

        var durations = new double[MeasuredSamples];
        var allocations = new double[MeasuredSamples];
        for (int sample = 0; sample < MeasuredSamples; sample++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long ticksBefore = Stopwatch.GetTimestamp();
            for (int operation = 0; operation < operations; operation++) action();
            long elapsedTicks = Stopwatch.GetTimestamp() - ticksBefore;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            durations[sample] = elapsedTicks * 1_000_000d / Stopwatch.Frequency / operations;
            allocations[sample] = (double)allocated / operations;
        }
        Array.Sort(durations);
        Array.Sort(allocations);
        return new Measurement(name, Median(durations), Percentile95(durations),
            Median(allocations), Percentile95(allocations));
    }

    static void RunBatch(int operations, Action action)
    {
        for (int operation = 0; operation < operations; operation++) action();
    }

    static double Median(double[] values) => values[values.Length / 2];
    static double Percentile95(double[] values) => values[(int)Math.Ceiling(values.Length * 0.95) - 1];

    static MapDocument LoadProfileDocument(out string workload)
    {
        string? path = Environment.GetEnvironmentVariable("KE_MAP_EDITOR_PROFILE_MAP");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            workload = $"copied-tiled-map:{Path.GetFileName(path)}";
            return MapDocumentFile.LoadTiled(path);
        }

        workload = "deterministic-flat-map";
        return FlatProfileDocument();
    }

    static MapDocument FlatProfileDocument()
    {
        var document = new MapDocument
        {
            Id = "responsiveness-profile",
            Bounds = new MapBounds { MinX = -512f, MinZ = -512f, MaxX = 512f, MaxZ = 512f },
        };
        document.Terrain.GentleAmplitude = 0f;
        return document;
    }

    static InputState Frame(IReadOnlySet<Key> keys, IReadOnlySet<MouseButton> down,
        IReadOnlySet<MouseButton> pressed, Vector2 delta, IReadOnlySet<MouseButton>? released = null,
        float scroll = 0f) =>
        new(keys, NoKeys, NoKeys, down, pressed, new Vector2(480f, 270f), delta, scroll, 960, 540,
            mouseReleased: released ?? NoButtons);

    static void Step(SceneManager manager, InputState frame)
    {
        manager.Pointer!.Update(frame);
        manager.Input = frame;
        manager.Update(0.016f);
    }

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    readonly record struct Measurement(string Name, double MedianMicroseconds, double P95Microseconds,
        double MedianAllocatedBytes, double P95AllocatedBytes);

    sealed class ProfileScene : MapEditorScene
    {
        readonly MapDocument _document;

        internal ProfileScene(MapDocument document) => _document = document;

        internal int NavigationTerrainSamples { get; private set; }
        internal int VisibilityRebuilds { get; private set; }

        protected override MapDocument CreateDocument(MapDocRegistry registry) => _document;

        protected override void BuildWorld()
        {
            Controller.Field = MapRuntime.BuildField(Document.Doc, Document.Registry);
        }

        protected override void TeardownWorld() { }
        protected override void UpdateStreaming(float dt) { }
        protected override void RebuildWorldForVisibility() => VisibilityRebuilds++;

        internal Vector3? ForceNavigationTerrainHit() => NavigationTerrainHit();

        protected override Vector3? NavigationTerrainHit()
        {
            NavigationTerrainSamples++;
            return base.NavigationTerrainHit();
        }
    }
}
