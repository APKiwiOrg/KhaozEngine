using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>The viewport's authored placement layer, driven exactly as <see cref="ViewportWorld"/> drives it: the
/// document's <see cref="EditorDocument.DocumentChanged"/> invalidates it, a per-frame refresh publishes the change
/// and refreshes only the touched chunks' props on a real synchronous <see cref="TerrainStreamer"/>, and a
/// capturing sink records what each chunk build and refresh queried from the layer. No GPU: the capturing sink
/// stands in for <see cref="Scene3DChunkSink"/>, which queries a live placement source the same way.</summary>
[Collection("AllocSensitive")]
public sealed class AuthoredPlacementLayerTests
{
    const float Chunk = TerrainChunkRegion.DefaultSize;
    const float Ground = 3f;

    [Fact]
    public void EveryEditKind_ReachesTheLayerOnTheNextRefresh_AndUndoRedoRestoreIt()
    {
        using var rig = new Rig();
        rig.Frame();
        Assert.Empty(rig.Served());

        rig.Editor.Execute(new AddPlacementCommand(Placement("p1", "oak", 10f, 20f)));
        rig.Frame();
        PropPlacement added = Assert.Single(rig.Served());
        Assert.Equal(("oak", 10f, Ground, 20f), (added.Id, added.X, added.Y, added.Z));
        Assert.Equal(new[] { new ChunkCoord(0, 0) }, rig.Sink.TakeRefreshes());

        rig.Editor.SealGesture();
        rig.Editor.Execute(new MovePlacementCommand("p1", 70f, 20f, null));
        rig.Frame();
        Assert.Equal(70f, Assert.Single(rig.Served()).X);
        Assert.Empty(rig.Sink.Props[new ChunkCoord(0, 0)]);
        Assert.Equal(new[] { new ChunkCoord(0, 0), new ChunkCoord(1, 0) }, Sorted(rig.Sink.TakeRefreshes()));

        rig.Editor.SealGesture();
        rig.Editor.Execute(new RotatePlacementCommand("p1", 1.25f));
        rig.Frame();
        Assert.Equal(1.25f, Assert.Single(rig.Served()).Yaw);
        Assert.Equal(new[] { new ChunkCoord(1, 0) }, rig.Sink.TakeRefreshes());

        rig.Editor.SealGesture();
        rig.Editor.Execute(new ScalePlacementCommand("p1", 2.5f));
        rig.Frame();
        Assert.Equal(2.5f, Assert.Single(rig.Served()).Scale);
        Assert.Equal(new[] { new ChunkCoord(1, 0) }, rig.Sink.TakeRefreshes());

        rig.Editor.SealGesture();
        rig.Editor.Execute(new RemovePlacementCommand("p1"));
        rig.Frame();
        Assert.Empty(rig.Served());

        Assert.True(rig.Editor.Undo());   // delete
        rig.Frame();
        Assert.Equal((70f, 1.25f, 2.5f), Transform(Assert.Single(rig.Served())));
        Assert.True(rig.Editor.Undo());   // scale
        rig.Frame();
        Assert.Equal((70f, 1.25f, 1f), Transform(Assert.Single(rig.Served())));
        Assert.True(rig.Editor.Undo());   // rotate
        rig.Frame();
        Assert.Equal((70f, 0f, 1f), Transform(Assert.Single(rig.Served())));
        Assert.True(rig.Editor.Undo());   // move
        rig.Frame();
        Assert.Equal((10f, 0f, 1f), Transform(Assert.Single(rig.Served())));
        Assert.True(rig.Editor.Undo());   // add
        rig.Frame();
        Assert.Empty(rig.Served());

        for (int i = 0; i < 5; i++) Assert.True(rig.Editor.Redo());
        rig.Frame();
        Assert.Empty(rig.Served());
        Assert.True(rig.Editor.Undo());
        rig.Frame();
        Assert.Equal((70f, 1.25f, 2.5f), Transform(Assert.Single(rig.Served())));
    }

    [Fact]
    public void SelectionChangesAndPlacementEdits_RefreshPropsOnly_AndNeverRebuildTerrain()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("a", "oak", 10f, 10f)));
        rig.Editor.SealGesture();
        rig.Editor.Execute(new AddPlacementCommand(Placement("b", "pine", 70f, 10f)));
        rig.Frame();

        rig.SelectedId = "a";
        rig.Frame();
        rig.SelectedId = "b";
        rig.Frame();
        rig.Editor.SealGesture();
        rig.Editor.Execute(new RotatePlacementCommand("a", 0.75f));
        rig.Frame();
        rig.Editor.SealGesture();
        rig.Editor.Execute(new MovePlacementCommand("a", 130f, 10f, null));
        rig.Frame();
        rig.Editor.SealGesture();
        rig.Editor.Execute(new RemovePlacementCommand("a"));
        rig.Frame();
        Assert.True(rig.Editor.Undo());
        rig.Frame();
        rig.Visibility.SetElementHidden(SelectionKind.Placement, "a", true);
        rig.Frame();

        Assert.NotEmpty(rig.Sink.TakeRefreshes());
        Assert.Empty(rig.Sink.TerrainRebuilds);
    }

    [Fact]
    public void UnchangedFrames_RebuildNothing_AndAnEditRebuildsOnlyItsChunk()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("a", "oak", 10f, 10f)));
        rig.Editor.SealGesture();
        rig.Editor.Execute(new AddPlacementCommand(Placement("b", "pine", 70f, 70f)));
        rig.Frame();
        rig.Sink.TakeRefreshes();

        for (int i = 0; i < 10; i++) Assert.False(rig.Frame());
        Assert.Empty(rig.Sink.TakeRefreshes());

        rig.Editor.SealGesture();
        rig.Editor.Execute(new RotatePlacementCommand("b", 0.5f));
        Assert.True(rig.Frame());
        Assert.Equal(new[] { new ChunkCoord(1, 1) }, rig.Sink.TakeRefreshes());
    }

    [Fact]
    public void SelectedPlacement_IsServedOutsideTheLayer_AndDraggingItRebuildsNoChunk()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("a", "oak", 10f, 10f)));
        rig.Editor.SealGesture();
        rig.Editor.Execute(new AddPlacementCommand(Placement("b", "pine", 20f, 10f)));
        rig.Frame();
        rig.Sink.TakeRefreshes();

        rig.SelectedId = "a";
        rig.Frame();
        Assert.Equal("pine", Assert.Single(rig.Served()).Id);
        Assert.Equal("a", rig.Layer.Selected?.Id);
        Assert.Equal(new[] { new ChunkCoord(0, 0) }, rig.Sink.TakeRefreshes());

        rig.Editor.SealGesture();
        for (int step = 1; step <= 20; step++)
        {
            rig.Editor.Execute(new MovePlacementCommand("a", 10f + step * 5f, 10f, null));
            rig.Frame();
            Assert.Equal(10f + step * 5f, rig.Layer.Selected?.Prop.X);
        }
        Assert.Empty(rig.Sink.TakeRefreshes());

        rig.SelectedId = null;
        rig.Frame();
        Assert.Contains(rig.Served(), p => p.Id == "oak" && p.X == 110f);
        Assert.Null(rig.Layer.Selected);
        Assert.Equal(new[] { new ChunkCoord(1, 0) }, rig.Sink.TakeRefreshes());
    }

    [Fact]
    public void ElementHide_LeavesTheLayer_AndAHiddenGroupDefersEveryRefresh()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("a", "oak", 10f, 10f)));
        rig.Frame();

        rig.Visibility.SetElementHidden(SelectionKind.Placement, "a", true);
        rig.Frame();
        Assert.Empty(rig.Served());
        rig.Visibility.SetElementHidden(SelectionKind.Placement, "a", false);
        rig.Frame();
        Assert.Single(rig.Served());
        rig.Sink.TakeRefreshes();

        rig.Visibility.SetGroup(VisibilityGroup.Placements, false);
        Assert.False(rig.Frame());
        rig.Editor.SealGesture();
        rig.Editor.Execute(new RotatePlacementCommand("a", 2f));
        Assert.False(rig.Frame());
        Assert.True(rig.Layer.IsDirty);
        Assert.Empty(rig.Sink.TakeRefreshes());

        rig.Visibility.SetGroup(VisibilityGroup.Placements, true);
        Assert.True(rig.Frame());
        Assert.Equal(2f, Assert.Single(rig.Served()).Yaw);
    }

    [Fact]
    public void StreamingFromTheCamera_ExcludesAPlacementOutsideTheResidentRing()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("near", "oak", 10f, 10f)));
        rig.Editor.SealGesture();
        rig.Editor.Execute(new AddPlacementCommand(Placement("far", "pine", 1000f, 10f)));
        rig.Frame();

        Assert.Equal(new[] { "oak" }, rig.Served().Select(p => p.Id).ToArray());
        var everything = new List<PropPlacement>();
        rig.Layer.PlacementsIn(new RectArea(-2000f, -2000f, 2000f, 2000f), everything);
        Assert.Equal(2, everything.Count);

        var camera = new Vector3(1000f, 0f, 10f);
        for (int i = 0; i < 20; i++) rig.Streamer.Update(camera, 1f / 60f);
        Assert.Equal(new[] { "pine" }, rig.Served().Select(p => p.Id).ToArray());
    }

    [Fact]
    public void SelectionOnlyChanges_ServeExactlyWhatAFullRefreshServes()
    {
        var doc = Doc();
        var rng = new Random(5);
        for (int i = 0; i < 200; i++)
            doc.Placements.Add(Placement("p" + (i % 190), i % 2 == 0 ? "oak" : "pine",
                rng.NextSingle() * 300f - 150f, rng.NextSingle() * 300f - 150f));
        TerrainField field = Field(Ground);
        var visibility = new EditorVisibility();
        visibility.SetElementHidden(SelectionKind.Placement, "p7", true);
        var layer = new AuthoredPlacementLayer(Chunk);
        layer.Refresh(doc, field, visibility, null, invalidate: null);
        var dirty = new List<ChunkCoord>();

        foreach (string? selected in new[] { "p3", "p7", "p12", "p185", "missing", null, "p3", "p4" })
        {
            dirty.Clear();
            layer.Refresh(doc, field, visibility, selected, dirty.Add);
            Assert.True(dirty.Count <= 2);
            var full = new AuthoredPlacementLayer(Chunk);
            full.Refresh(doc, field, visibility, selected, invalidate: null);

            Assert.Equal(ChunkByChunk(full), ChunkByChunk(layer));
            Assert.Equal(full.Selected, layer.Selected);
        }
    }

    // Every chunk's served placements in the order the sink would receive them, chunk by chunk in a fixed coord
    // order, so two layers compare on exact per-chunk order rather than as sets.
    static string[] ChunkByChunk(AuthoredPlacementLayer layer)
    {
        var all = new List<string>();
        var served = new List<PropPlacement>();
        for (int z = -4; z <= 3; z++)
        for (int x = -4; x <= 3; x++)
        {
            served.Clear();
            layer.PlacementsIn(ChunkGrid.AreaOf(new ChunkCoord(x, z), Chunk), served);
            foreach (PropPlacement p in served) all.Add($"{x},{z}:{p.Id}@{p.X},{p.Z}");
        }
        return all.ToArray();
    }

    [Fact]
    public void AMoveThatChangesOnlyZ_ReachesTheLayer()
    {
        using var rig = new Rig();
        rig.Editor.Execute(new AddPlacementCommand(Placement("a", "oak", 10f, 10f)));
        rig.Frame();
        rig.Sink.TakeRefreshes();

        rig.Editor.SealGesture();
        rig.Editor.Execute(new MovePlacementCommand("a", 10f, 30f, null));
        rig.Frame();

        PropPlacement moved = Assert.Single(rig.Served());
        Assert.Equal((10f, Ground, 30f), (moved.X, moved.Y, moved.Z));
        Assert.Equal(new[] { new ChunkCoord(0, 0) }, rig.Sink.TakeRefreshes());
    }

    [Fact]
    public void BuildSinkLayers_AuthoredLayerStaysEqual_SoUpdateLayersKeepsIt()
    {
        var doc = new MapDocument { Id = "update-layers" };
        doc.ScatterLayers.Add(new MapScatterLayer { Name = "forest" });
        doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ferns", HostLayer = "forest" });
        var world = new ViewportWorld(null!, Array.Empty<string>());
        IReadOnlyList<PropLayer> before = world.BuildSinkLayers(doc);
        IReadOnlyList<PropLayer> after = world.BuildSinkLayers(doc);

        Assert.Equal(before[^1], after[^1]);
        var sink = new Scene3DChunkSink(null!, Field(Ground), before, Chunk);
        sink.UpdateLayers(after);
    }

    [Fact]
    public void PlacementsIn_IsHalfOpen_SoAnEdgePlacementBelongsToTheNextChunk()
    {
        var layer = new AuthoredPlacementLayer(Chunk);
        var doc = Doc();
        doc.Placements.Add(Placement("edge", "oak", Chunk, 5f));
        layer.Refresh(doc, Field(Ground), new EditorVisibility(), null, invalidate: null);

        var first = new List<PropPlacement>();
        var second = new List<PropPlacement>();
        layer.PlacementsIn(ChunkGrid.AreaOf(new ChunkCoord(0, 0), Chunk), first);
        layer.PlacementsIn(ChunkGrid.AreaOf(new ChunkCoord(1, 0), Chunk), second);

        Assert.Empty(first);
        Assert.Single(second);
    }

    [Fact]
    public void DuplicateSelectedId_KeepsOnlyTheFirstMatchOutOfTheLayer()
    {
        var layer = new AuthoredPlacementLayer(Chunk);
        var doc = Doc();
        doc.Placements.Add(Placement("dup", "oak", 1f, 1f));
        doc.Placements.Add(Placement("dup", "pine", 2f, 1f));

        layer.Refresh(doc, Field(Ground), new EditorVisibility(), "dup", invalidate: null);

        var served = new List<PropPlacement>();
        layer.PlacementsIn(ChunkGrid.AreaOf(new ChunkCoord(0, 0), Chunk), served);
        Assert.Equal("pine", Assert.Single(served).Id);
        Assert.Equal("oak", layer.Selected?.Prop.Id);
    }

    [Fact]
    public void FieldSwap_ReGroundsDeferredPlacementsOnReveal()
    {
        var layer = new AuthoredPlacementLayer(Chunk);
        var doc = Doc();
        doc.Placements.Add(Placement("grounded", "oak", 1f, 2f));
        var visibility = new EditorVisibility { TerrainOnly = true };

        Assert.False(layer.Refresh(doc, Field(1f), visibility, null, invalidate: null));
        Assert.True(layer.IsDirty);

        visibility.TerrainOnly = false;
        var dirty = new List<ChunkCoord>();
        Assert.True(layer.Refresh(doc, Field(7f), visibility, null, dirty.Add));
        Assert.False(layer.IsDirty);
        Assert.Equal(new[] { new ChunkCoord(0, 0) }, dirty);
        var served = new List<PropPlacement>();
        layer.PlacementsIn(ChunkGrid.AreaOf(new ChunkCoord(0, 0), Chunk), served);
        Assert.Equal(7f, Assert.Single(served).Y);

        layer.Invalidate();
        dirty.Clear();
        Assert.True(layer.Refresh(doc, Field(9f), invalidate: dirty.Add));
        Assert.Equal(new[] { new ChunkCoord(0, 0) }, dirty);
    }

    [Fact]
    public void IdleRefresh_AllocatesNothing()
    {
        var layer = new AuthoredPlacementLayer(Chunk);
        var doc = Doc();
        for (int i = 0; i < 4096; i++) doc.Placements.Add(Placement(i.ToString(), "oak", i % 500, i / 500));
        TerrainField field = Field(Ground);
        var visibility = new EditorVisibility();
        Action<ChunkCoord> invalidate = static _ => { };
        layer.Refresh(doc, field, visibility, "2048", invalidate);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) layer.Refresh(doc, field, visibility, "2048", invalidate);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void EveryVisibilityMutator_BumpsVersion()
    {
        var visibility = new EditorVisibility();
        var mutators = new Action[]
        {
            () => visibility.TerrainOnly = true,
            () => visibility.SetGroup(VisibilityGroup.Spawns, false),
            () => visibility.SetCategory(EditorPropCategory.Trees, false),
            () => visibility.SetLayer("forest", false),
            () => visibility.RenameLayer("forest", "woods"),
            () => visibility.SetElementHidden(SelectionKind.Placement, "a", true),
            () => visibility.RenameKey(SelectionKind.Placement, "a", "b"),
            () => visibility.SetElementHidden(SelectionKind.Exclusion, "2", true),
            () => visibility.RemapIndex(SelectionKind.Exclusion, 2, 0),
            () => visibility.InsertIndex(SelectionKind.Exclusion, 0),
            () => visibility.RemoveIndex(SelectionKind.Exclusion, 0),
            () => visibility.ShowAll(),
        };
        foreach (Action mutate in mutators)
        {
            int before = visibility.Version;
            mutate();
            Assert.NotEqual(before, visibility.Version);
        }
    }

    [Fact]
    public void DrawFilter_GatesTheAuthoredLayerByGroupAndKitOnly()
    {
        Func<string, bool> noScatter = static _ => false;
        Func<string, bool> allKits = static _ => true;
        Func<string, bool> noOak = static kit => kit != "oak";
        const string authored = ViewportWorld.AuthoredLayerIdentity;

        Assert.True(ViewportWorld.PropVisible(authored, "pine", true, noScatter, allKits));
        Assert.False(ViewportWorld.PropVisible(authored, "pine", false, noScatter, allKits));
        Assert.False(ViewportWorld.PropVisible(authored, "oak", true, noScatter, noOak));
        Assert.False(ViewportWorld.PropVisible("forest", "pine", true, noScatter, allKits));
        Assert.True(ViewportWorld.PropVisible("forest", "pine", false, static _ => true, allKits));
        Assert.True(ViewportWorld.PropVisible(null, "pine", false, noScatter, allKits));
    }

    [Fact]
    public void BuildSinkLayers_AppendsOneLiveRenderOnlyAuthoredLayerLast()
    {
        var doc = new MapDocument { Id = "layers" };
        doc.ScatterLayers.Add(new MapScatterLayer { Name = "forest" });
        doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ferns", HostLayer = "forest" });
        var world = new ViewportWorld(null!, Array.Empty<string>());

        IReadOnlyList<PropLayer> layers = world.BuildSinkLayers(doc);

        Assert.Equal(3, layers.Count);
        Assert.Equal(0, layers[1].HostLayerIndex);
        PropLayer authoredLayer = layers[2];
        Assert.True(authoredLayer.IsPlacement);
        Assert.NotNull(authoredLayer.PlacementSource);
        Assert.Null(authoredLayer.Placements);
        Assert.False(authoredLayer.RegisterColliders);
        Assert.Equal(ViewportWorld.AuthoredLayerIdentity, authoredLayer.Identity);
        Assert.Equal(world.RenderDistance.PropDrawRadius, authoredLayer.DrawRadius);
        Assert.Same(authoredLayer.PlacementSource, world.BuildSinkLayers(doc)[2].PlacementSource);
    }

    static (float X, float Yaw, float Scale) Transform(PropPlacement p) => (p.X, p.Yaw, p.Scale);

    static ChunkCoord[] Sorted(IEnumerable<ChunkCoord> coords) =>
        coords.OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();

    static MapPlacement Placement(string id, string kind, float x, float z) =>
        new() { Id = id, Kind = kind, X = x, Z = z, Y = null };

    static MapDocument Doc() => new()
    {
        Id = "authored-layer",
        Bounds = new MapBounds { MinX = -1024f, MinZ = -1024f, MaxX = 1024f, MaxZ = 1024f },
    };

    static TerrainField Field(float height) => new(new TerrainConfig
    {
        GentleAmplitude = 0f,
        DetailOctaves = 0,
        Biomes = new[]
        {
            new BiomeBand
            {
                Start = float.NegativeInfinity,
                End = float.PositiveInfinity,
                Biome = BiomeId.Meadow,
                BaseHeight = height,
                HillAmplitude = 0f,
            },
        },
    });

    /// <summary>The viewport's wiring without a device: document, layer, synchronous streamer around the camera,
    /// and a sink that records every chunk build's query.</summary>
    sealed class Rig : IDisposable
    {
        public readonly EditorDocument Editor = new(Doc());
        public readonly AuthoredPlacementLayer Layer = new(Chunk);
        public readonly EditorVisibility Visibility = new();
        public readonly CapturingSink Sink;
        public readonly TerrainStreamer Streamer;
        public string? SelectedId;
        readonly TerrainField _field = Field(Ground);
        readonly Action<ChunkCoord> _refresh;

        public Rig()
        {
            Editor.DocumentChanged += Layer.Invalidate;
            Sink = new CapturingSink(Layer);
            Streamer = new TerrainStreamer(
                new StreamerConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerFrame: 64, ChunkSize: Chunk,
                    Async: false),
                Sink);
            _refresh = coord => Streamer.RefreshPlacements(coord);
            Streamer.PrimeAround(Vector3.Zero);
            Sink.TakeRefreshes();
        }

        /// <summary>One viewport frame's authored refresh, as <see cref="ViewportWorld.Draw"/> runs it.</summary>
        public bool Frame() => Layer.Refresh(Editor.Doc, _field, Visibility, SelectedId, _refresh);

        /// <summary>Every placement currently held by a resident chunk.</summary>
        public List<PropPlacement> Served() => Sink.Props.Values.SelectMany(p => p).ToList();

        public void Dispose() => Streamer.Dispose();
    }

    /// <summary>Records what each chunk build and each props-only refresh queried from the layer. A terrain rebuild
    /// (<see cref="IChunkSink.ReLod"/>) is recorded apart, so a test can prove an edit never reaches it.</summary>
    sealed class CapturingSink : IChunkPlacementRefreshSink
    {
        readonly IPlacementSource _source;
        readonly List<ChunkCoord> _refreshes = new();
        public readonly List<ChunkCoord> TerrainRebuilds = new();
        public readonly Dictionary<ChunkCoord, List<PropPlacement>> Props = new();

        public CapturingSink(IPlacementSource source) => _source = source;

        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Query(coord);

        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring)
        {
            TerrainRebuilds.Add(coord);
            Query(coord);
        }

        public void RefreshPlacements(ChunkCoord coord, object handle)
        {
            _refreshes.Add(coord);
            Query(coord);
        }

        public void Unload(ChunkCoord coord, object handle) => Props.Remove(coord);

        public ChunkCoord[] TakeRefreshes()
        {
            ChunkCoord[] taken = _refreshes.ToArray();
            _refreshes.Clear();
            return taken;
        }

        object Query(ChunkCoord coord)
        {
            var into = new List<PropPlacement>();
            _source.PlacementsIn(ChunkGrid.AreaOf(coord, Chunk), into);
            Props[coord] = into;
            return coord;
        }
    }
}
