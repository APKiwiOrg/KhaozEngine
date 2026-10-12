using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.WorldMap;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.WorldMap;

/// <summary>Proves full cache builds avoid materializing footprints unused by feature rules.</summary>
public sealed class TileWorldMapFootprintQueryTests
{
    /// <summary>A coarse build without footprint icon rules materializes none of its 1,024 small trees.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoarseCacheBuild_WithoutFootprintRules_MaterializesNoTrees(bool markerRule)
    {
        TileWorldDocument document = TreeField();
        document.SetMarker("bank", 40, 40, 0, ["bank"]);
        var source = new CountingSource(new TileWorldMapSource(document, Catalogs()));
        var style = new WorldMapStyle
        {
            IconRules = markerRule ? [new(WorldMapFeatureSource.Marker, ["bank"], "bank", 1)] : []
        };
        using var cache = new WorldMapChunkCache(source, style);

        WorldMapChunk built = Build(cache);

        Assert.Equal(0, source.MaterializedFootprints);
        Assert.Equal(1, source.FootprintCalls);
        Assert.Equal(markerRule ? 1 : 0, source.MarkerCalls);
        Assert.Equal(markerRule ? 1 : 0, built.Features.Icons.Count);
        if (markerRule) Assert.Equal("bank", Assert.Single(built.Features.Icons).IconKey);
    }

    /// <summary>A sparse bank rule retains its small icon and rejects untagged trees before materialization.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoarseCacheBuild_BankRule_MaterializesOnlyBank(bool archetypeTag)
    {
        TileWorldDocument document = TreeField();
        document.AddObject(archetypeTag ? "bank" : "tree", 40, 40, 0, 0,
            archetypeTag ? null : ["bank"]);
        var source = new CountingSource(new TileWorldMapSource(document, Catalogs()));
        var style = new WorldMapStyle { IconRules = [new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 1)] };
        using var cache = new WorldMapChunkCache(source, style);

        WorldMapChunk built = Build(cache);

        Assert.Equal(1, source.MaterializedFootprints);
        Assert.Equal(2, source.FootprintCalls);
        Assert.Equal(0, source.MarkerCalls);
        Assert.Equal(new WorldMapIconAnchor("bank", new(40.5f, 40.5f), 1, 24f), Assert.Single(built.Features.Icons));
    }

    /// <summary>An explicit empty any-of tag query matches no footprints, unlike null.</summary>
    [Fact]
    public void FootprintQuery_EmptyAnyTags_ReturnsNoRecords()
    {
        var source = new TileWorldMapSource(TreeField(), Catalogs());
        var footprints = new List<WorldMapFootprint>();

        source.CollectFootprints(new WorldMapFootprintQuery(new(0, 0, 64, 64), AnyTags: Array.Empty<string>()), footprints);

        Assert.Empty(footprints);
        source.CollectFootprints(new WorldMapFootprintQuery(new(0, 0, 64, 64)), footprints);
        Assert.Equal(1024, footprints.Count);
    }

    /// <summary>Chunk collection reuses its tag union for one style but not for an equal distinct instance.</summary>
    [Fact]
    public void FeatureCollection_ReusesTagUnionByStyleIdentity()
    {
        var source = new CountingSource(new TileWorldMapSource(TreeField(), Catalogs()));
        var style = new WorldMapStyle { IconRules = [new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 1)] };

        WorldMapFeatureCollector.Collect(source, style, new(0, 0));
        WorldMapFeatureCollector.Collect(source, style, new(0, 0, 1));
        WorldMapFeatureCollector.Collect(source, style with { }, new(0, 0));

        Assert.Equal(3, source.Queries.Count);
        Assert.NotNull(source.Queries[0].AnyTags);
        Assert.Same(source.Queries[0].AnyTags, source.Queries[1].AnyTags);
        Assert.NotSame(source.Queries[0].AnyTags, source.Queries[2].AnyTags);
        Assert.Equal(0f, source.Queries[0].MinExtentMetres);
        Assert.Equal("bank", Assert.Single(source.Queries[0].AnyTags!));
    }

    /// <summary>An empty footprint rule preserves every anchor despite preceding filtered rules.</summary>
    [Fact]
    public void CoarseCacheBuild_EmptyTags_MaterializesEveryFootprint()
    {
        TileWorldDocument document = TreeField();
        document.AddObject("bank", 40, 40, 0, 0);
        var source = new CountingSource(new TileWorldMapSource(document, Catalogs()));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 1),
                new(WorldMapFeatureSource.Footprint, [], "tree", 0)
            ]
        };
        using var cache = new WorldMapChunkCache(source, style);

        WorldMapChunk built = Build(cache);

        Assert.Equal(1025, source.MaterializedFootprints);
        Assert.Equal(1025, built.Features.Icons.Count);
        Assert.Single(built.Features.Icons, icon => icon.IconKey == "bank");
    }

    /// <summary>Every footprint rule contributes tags, with exact all-of ordinal matching after the query.</summary>
    [Fact]
    public void FeatureCollection_UnionIncludesAllRules_ExactMatchRemainsOrdinalAllOf()
    {
        TileWorldDocument document = TreeField();
        document.AddObject("tree", 40, 40, 0, 0, ["bank", "service"]);
        document.AddObject("tree", 41, 40, 0, 0, ["smith"]);
        document.AddObject("tree", 42, 40, 0, 0, ["bank"]);
        document.AddObject("tree", 43, 40, 0, 0, ["Bank"]);
        var source = new CountingSource(new TileWorldMapSource(document, Catalogs()));
        var style = new WorldMapStyle
        {
            IconRules =
            [
                new(WorldMapFeatureSource.Footprint, ["bank", "service"], "bank", 1),
                new(WorldMapFeatureSource.Footprint, ["smith"], "smith", 1),
                new(WorldMapFeatureSource.Marker, [], "marker", 1)
            ]
        };

        WorldMapChunkFeatures features = WorldMapFeatureCollector.Collect(source, style, new(0, 0, 4));

        Assert.Equal(3, source.MaterializedFootprints);
        Assert.Collection(features.Icons,
            icon => Assert.Equal(new WorldMapIconAnchor("bank", new(40.5f, 40.5f), 1, 24f), icon),
            icon => Assert.Equal(new WorldMapIconAnchor("smith", new(41.5f, 40.5f), 1, 24f), icon));
    }

    static WorldMapChunk Build(WorldMapChunkCache cache)
    {
        var chunk = new WorldMapChunkCoord(0, 0, 4);
        cache.Request(chunk, 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(chunk, out WorldMapChunk? built));
        return built;
    }

    static TileWorldDocument TreeField()
    {
        TileWorldDocument document = TileWorldMapSourceTests.FlatWorld();
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++) document.AddObject("tree", x, y, 0, 0);
        return document;
    }

    static TileWorldCatalogs Catalogs() => TileWorldCatalogs.LoadJson(
        """
        {
          "materials": [{ "id": 1, "name": "ground", "color": "#527448" }],
          "archetypes": [
            { "id": "tree", "name": "tree", "meshRef": "test/tree.glb", "sizeX": 1, "sizeZ": 1 },
            { "id": "bank", "name": "bank", "meshRef": "test/bank.glb", "sizeX": 1, "sizeZ": 1,
              "tags": ["bank"] }
          ]
        }
        """, "footprint-query-tests");

    sealed class CountingSource(TileWorldMapSource inner) : IWorldMapSource
    {
        public MapRect Bounds => inner.Bounds;
        public float SurfaceCellSize => inner.SurfaceCellSize;
        public long Revision => inner.Revision;
        public event Action<MapRect>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        internal int MaterializedFootprints { get; private set; }
        internal int FootprintCalls { get; private set; }
        internal int MarkerCalls { get; private set; }
        internal List<WorldMapFootprintQuery> Queries { get; } = [];

        public WorldMapSurfaceGrid SampleSurface(MapRect area, float maxCellSize) => inner.SampleSurface(area, maxCellSize);

        public void CollectFootprints(in WorldMapFootprintQuery query, List<WorldMapFootprint> into)
        {
            int before = into.Count;
            Queries.Add(query);
            inner.CollectFootprints(in query, into);
            MaterializedFootprints += into.Count - before;
            FootprintCalls++;
        }

        public void CollectMarkers(MapRect area, List<WorldMapMarker> into)
        {
            MarkerCalls++;
            inner.CollectMarkers(area, into);
        }
        public Vector2 ToMap(Vector3 worldPosition) => inner.ToMap(worldPosition);
        public float ToMapHeading(Vector3 worldForward) => inner.ToMapHeading(worldForward);
    }
}
