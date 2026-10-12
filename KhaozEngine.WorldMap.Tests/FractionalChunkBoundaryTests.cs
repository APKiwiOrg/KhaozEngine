using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Fractional chunk edges describe one partition and one owner for each feature.</summary>
public sealed class FractionalChunkBoundaryTests
{
    /// <summary>Every canonical origin round-trips on both axes, including negative indices.</summary>
    [Fact]
    public void FractionalChunkOrigins_RoundTripToTheirOwner()
    {
        for (int index = -50; index <= 50; index++)
        {
            var coord = new WorldMapChunkCoord(index, index);
            MapRect rect = WorldMapChunking.ChunkRect(coord, 0.1f);
            Assert.Equal(coord, WorldMapChunking.ChunkAt(new(rect.X, rect.Y), 0.1f));
        }
    }

    /// <summary>Each canonical rectangle covers exactly itself and meets its neighbours without overlap.</summary>
    [Fact]
    public void FractionalChunkRects_EnumerateOnlyTheirOwnerAndShareEdges()
    {
        for (int index = -50; index <= 50; index++)
        {
            var coord = new WorldMapChunkCoord(index, index);
            MapRect rect = WorldMapChunking.ChunkRect(coord, 0.1f);
            MapRect next = WorldMapChunking.ChunkRect(new(index + 1, index + 1), 0.1f);
            Assert.Equal(next.X, rect.Right);
            Assert.Equal(next.Y, rect.Top);
            Assert.Equal(new[] { coord }, WorldMapChunking.ChunksTouching(rect, 0.1f).ToArray());
            Assert.Equal(coord, WorldMapChunking.ChunkAt(new(MathF.BitDecrement(rect.Right), MathF.BitDecrement(rect.Top)), 0.1f));
            Assert.Equal(new WorldMapChunkCoord(index - 1, index - 1),
                WorldMapChunking.ChunkAt(new(MathF.BitDecrement(rect.X), MathF.BitDecrement(rect.Y)), 0.1f));
        }
    }

    /// <summary>The bank reproduction has one marker icon in chunk seven, rather than two copies.</summary>
    [Fact]
    public void FractionalBoundaryBankMarker_HasExactlyOneIcon()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 1f, 1f));
        source.Markers.Add(new("bank", new(0.7f, 0.05f), ["bank"]));
        var style = new WorldMapStyle
        {
            ChunkMetres = 0.1f,
            PixelsPerMetre = 40f,
            IconRules = [new(WorldMapFeatureSource.Marker, ["bank"], "bank", 1)]
        };
        Assert.Empty(WorldMapFeatureCollector.Collect(source, style, new(6, 0)).Icons);
        WorldMapIconAnchor icon = Assert.Single(WorldMapFeatureCollector.Collect(source, style, new(7, 0)).Icons);
        Assert.Equal(new Vector2(0.7f, 0.05f), icon.Position);
    }

    /// <summary>Crossing footprint centres and marker labels follow the same fractional ownership rule.</summary>
    [Fact]
    public void FractionalBoundaryFootprintAndLabel_HaveExactlyOneOwner()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 1f, 1f));
        var position = new Vector2(0.7f, 0.05f);
        source.Footprints.Add(new(1, "bank", position, new(0.04f), 0f, ["bank"], true, WorldMapCollision.Solid));
        source.Markers.Add(new("bank", position, ["label", "town"]));
        var style = new WorldMapStyle
        {
            ChunkMetres = 0.1f,
            PixelsPerMetre = 40f,
            IconRules = [new(WorldMapFeatureSource.Footprint, ["bank"], "bank", 1)],
            Labels = new("label", [new("town", 1, 0f, 8f, 1f)])
        };
        WorldMapChunkFeatures west = WorldMapFeatureCollector.Collect(source, style, new(6, 0));
        Assert.Empty(west.Icons);
        Assert.Empty(west.Labels);
        WorldMapChunkFeatures east = WorldMapFeatureCollector.Collect(source, style, new(7, 0));
        Assert.Equal(position, Assert.Single(east.Icons).Position);
        Assert.Equal(position, Assert.Single(east.Labels).Position);
    }
}
