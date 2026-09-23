using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Placement picking follows what the viewport draws: a placement outside the streamed gameplay ring or the
/// prop cull is not clickable, one inside is, and the current selection stays clickable because the viewport always
/// draws it. The drawn-here rule itself runs against a real synchronous <see cref="TerrainStreamer"/>.</summary>
public sealed class EditorPlacementPickingTests
{
    const float Ground = 3f;
    const float KindHeight = 4f;
    static readonly Vector3 Down = new(0f, -1f, 0f);

    [Fact]
    public void Pick_SkipsAPlacementTheViewportDoesNotDraw()
    {
        MapDocument doc = Doc();
        doc.Placements.Add(Placement("p", 0f, 0f));
        var origin = new Vector3(0f, 50f, 0f);

        Assert.True(EditorPicking.Pick(doc, Field(), origin, Down, 1000f, static _ => KindHeight,
            out EditorPicking.PickResult undrawn, null, null, placementDrawn: static _ => false));
        Assert.Equal(SelectionKind.None, undrawn.Kind);

        Assert.True(EditorPicking.Pick(doc, Field(), origin, Down, 1000f, static _ => KindHeight,
            out EditorPicking.PickResult drawn, null, null, placementDrawn: static _ => true));
        Assert.Equal((SelectionKind.Placement, "p"), (drawn.Kind, drawn.Id));
    }

    [Fact]
    public void Controller_PicksOnlyDrawnPlacements_ButKeepsTheSelectionPickable()
    {
        MapDocument md = Doc();
        md.Placements.Add(Placement("near", 0f, 0f));
        md.Placements.Add(Placement("far", 500f, 0f));
        var doc = new EditorDocument(md);
        var c = new EditorToolController(doc)
        {
            Field = Field(), HeightOf = static _ => KindHeight, GizmoScale = 1f,
            PlacementDrawnAt = static (x, _) => x < 100f,
        };

        c.Update(Click(0f, 0f));
        Assert.Equal((SelectionKind.Placement, "near"), (doc.Selection.Kind, doc.Selection.Id));

        c.Update(Click(500f, 0f));
        Assert.Equal(SelectionKind.None, doc.Selection.Kind);

        doc.Selection.Set(SelectionKind.Placement, "far");
        c.Update(Click(500f - 0.4f, -0.4f));
        Assert.Equal((SelectionKind.Placement, "far"), (doc.Selection.Kind, doc.Selection.Id));
    }

    [Fact]
    public void DrawnHere_IsTheGameplayRingInsideThePropCull()
    {
        var sink = new NullSink();
        var config = new StreamerConfig(LoadRadius: 2, UnloadRadius: 5, MaxLoadsPerFrame: 64, ChunkSize: 60f,
            Async: false, DecorRadius: 4);
        using var streamer = new TerrainStreamer(config, sink);
        var focus = new Vector3(30f, 0f, 30f);
        streamer.PrimeAround(focus);

        Assert.True(ViewportWorld.IsDrawnAt(streamer, focus, 500f, 40f, 40f));
        Assert.False(ViewportWorld.IsDrawnAt(streamer, focus, 500f, 30f + 3.5f * 60f, 30f));
        Assert.False(ViewportWorld.IsDrawnAt(streamer, focus, 500f, 5000f, 30f));
        Assert.False(ViewportWorld.IsDrawnAt(streamer, focus, 40f, 30f + 90f, 30f));
        Assert.True(ViewportWorld.IsDrawnAt(streamer, focus, 90f, 30f + 90f, 30f));
    }

    static EditorFrameInput Click(float x, float z) =>
        new(new Vector3(x, 50f, z), Down, pointerPressed: true, pointerDown: true, dt: 0.016f);

    static MapPlacement Placement(string id, float x, float z) =>
        new() { Id = id, Kind = "hut", X = x, Z = z, Y = null };

    static MapDocument Doc() => new()
    {
        Id = "picking",
        Bounds = new MapBounds { MinX = -1000f, MinZ = -1000f, MaxX = 1000f, MaxZ = 1000f },
    };

    static TerrainField Field() => new(new TerrainConfig
    {
        GentleAmplitude = 0f,
        DetailOctaves = 0,
        Biomes = new[]
        {
            new BiomeBand
            {
                Start = float.NegativeInfinity, End = float.PositiveInfinity,
                Biome = BiomeId.Meadow, BaseHeight = Ground, HillAmplitude = 0f,
            },
        },
    });

    sealed class NullSink : IChunkSink
    {
        readonly HashSet<ChunkCoord> _loaded = new();
        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => _loaded.Add(coord);
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { }
        public void Unload(ChunkCoord coord, object handle) => _loaded.Remove(coord);
    }
}
