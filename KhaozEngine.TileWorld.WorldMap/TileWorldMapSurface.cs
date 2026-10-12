using System;
using System.Collections.Concurrent;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;

namespace KhaozEngine.TileWorld.WorldMap;

/// <summary>Samples the static tile lattice and caches material colours and region water surfaces.</summary>
internal sealed class TileWorldMapSurface
{
    readonly TileWorldDocument _document;
    readonly TileWorldCatalogs _catalogs;
    readonly int _plane;
    readonly float _tileSize;
    readonly ConcurrentDictionary<ushort, Color> _colours = new();
    readonly ConcurrentDictionary<RegionCoord, float[]> _waterSurfaces = new();

    internal TileWorldMapSurface(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, float tileSize)
    {
        _document = document;
        _catalogs = catalogs;
        _plane = plane;
        _tileSize = tileSize;
    }

    internal WorldMapSurfaceGrid Sample(MapRect area)
    {
        TileRect rect = TileWorldMapSource.TilesCovering(area, _tileSize);
        var grid = new WorldMapSurfaceGrid(new Vector2(rect.X * _tileSize, rect.Z * _tileSize),
            _tileSize, rect.Width, rect.Height);
        for (int row = 0; row < rect.Height; row++)
            for (int column = 0; column < rect.Width; column++)
            {
                int x = rect.X + column, z = rect.Z + row;
                ushort underlay = _document.GetUnderlay(x, z, _plane);
                if (_catalogs.Material(underlay) is null) continue;
                ushort overlay = _document.GetOverlay(x, z, _plane);
                ushort material = overlay != 0 && _catalogs.Material(overlay) is not null ? overlay : underlay;
                int index = grid.Index(column, row);
                grid.Ground[index] = _colours.GetOrAdd(material, ParseColour);
                grid.SurfaceHeight[index] = (_document.CornerHeight(x, z, _plane)
                    + _document.CornerHeight(x + 1, z, _plane)
                    + _document.CornerHeight(x, z + 1, _plane)
                    + _document.CornerHeight(x + 1, z + 1, _plane)) * 0.25f;
                if (!TileWaterBodies.IsWater(_document, _catalogs, x, z, _plane)) continue;
                RegionCoord region = RegionCoord.Of(x, z);
                float[] water = _waterSurfaces.GetOrAdd(region, CollectWater);
                grid.WaterSurface[index] = water[(z - region.OriginZ) * TileRegion.Size + x - region.OriginX];
            }
        return grid;
    }

    Color ParseColour(ushort material) => Color.FromHex(_catalogs.Material(material)!.Color);

    float[] CollectWater(RegionCoord region)
    {
        var surfaces = new float[TileRegion.TileCount];
        Array.Fill(surfaces, float.NaN);
        foreach (TileWaterBody body in TileWaterBodies.Collect(_document, _catalogs, region, _plane))
            foreach (TileRect rect in body.Rects)
                for (int z = rect.Z; z < rect.Z1; z++)
                    for (int x = rect.X; x < rect.X1; x++)
                        surfaces[(z - region.OriginZ) * TileRegion.Size + x - region.OriginX] = body.SurfaceY;
        return surfaces;
    }
}
