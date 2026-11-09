using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Generation ownership inverted through the same model reach used by scene discovery.</summary>
internal static class MapFoliageWindowChunks
{
    internal static IEnumerable<MapFoliageChunkKey> Discover(MapFoliageLayer layer, MapAssetClosure assets, MapBox3 bounds)
    {
        if (bounds.MinX >= bounds.MaxX || bounds.MinZ >= bounds.MaxZ) yield break;
        double radius = MapSceneResourceBounds.FoliageRadius(layer, assets);
        int x0 = MapFoliageRasterReads.Owner(layer, MapFoliageSupport.Outward(bounds.MinX - radius, true), true);
        int z0 = MapFoliageRasterReads.Owner(layer, MapFoliageSupport.Outward(bounds.MinZ - radius, true), false);
        int x1 = MapFoliageRasterReads.Owner(layer, MathF.BitDecrement(MapFoliageSupport.Outward(bounds.MaxX + radius, false)), true);
        int z1 = MapFoliageRasterReads.Owner(layer, MathF.BitDecrement(MapFoliageSupport.Outward(bounds.MaxZ + radius, false)), false);
        foreach (int z in MapFoliageRasterReads.Chunks(layer, z0, z1, false))
            foreach (int x in MapFoliageRasterReads.Chunks(layer, x0, x1, true))
                // Empty candidates count too, so a broad model cannot scan an unbounded off-raster range.
                yield return new(x, z);
    }
}
