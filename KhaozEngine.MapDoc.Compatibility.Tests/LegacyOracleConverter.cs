using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

internal static class LegacyOracleConverter
{
    internal static IEnumerable<(RegionCoord Region, int Plane)> RegionPlanes(TileWorldDocument document)
    {
        foreach (TileRegion region in document.Regions.Values.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx))
            for (int plane = 0; plane < region.Planes.Length; plane++)
                if (!region.Planes[plane].IsEmpty) yield return (region.Coord, plane);
    }

    internal static (MapSurfaceRef Surface, MapSurfacePatch Patch) ToNative(TileWorldDocument document,
        RegionCoord region, int plane)
    {
        TilePlaneData layer = Layer(document, region, plane);
        string id = "plane-" + plane.ToString(CultureInfo.InvariantCulture);
        var surface = new MapSurfaceRef(id, MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.LegacyTileWorld, null, null, "");
        var patch = new MapSurfacePatch
        {
            Key = new(id, region.Rx, region.Rz),
            Width = TileRegion.Size,
            Depth = TileRegion.Size,
            Heights = new int[(TileRegion.Size + 1) * (TileRegion.Size + 1)],
            Cells = new MapSurfaceCell[TileRegion.TileCount],
            Presence = Enumerable.Repeat(ulong.MaxValue, TileRegion.TileCount / 64).ToArray(),
        };
        for (int z = 0; z <= TileRegion.Size; z++)
            for (int x = 0; x <= TileRegion.Size; x++)
                patch.Heights[z * (TileRegion.Size + 1) + x] =
                    document.CornerHeightCm(region.OriginX + x, region.OriginZ + z, plane);
        for (int i = 0; i < patch.Cells.Length; i++)
            patch.Cells[i] = new(layer.Underlay?[i] ?? 0, layer.Overlay?[i] ?? 0,
                (MapOverlayCut)(layer.OverlayShape?[i] ?? 0), layer.OverlayRotation?[i] ?? 0,
                (MapCellFlags)(layer.Settings?[i] ?? 0), MapCellTopology.Auto);
        return (surface, patch);
    }

    internal static void AssertSameTriangles(TileGroundMesh legacy, MapCompiledPatch native,
        float vertexTolerance, float normalTolerance)
    {
        Assert.Equal(legacy.Indices.Length / 3, native.Faces.Count);
        var legacyOrigin = new Vector3(legacy.Region.OriginX, 0, -legacy.Region.OriginZ);
        var nativeOrigin = new Vector3((float)native.Anchor.X, (float)native.Anchor.Y, (float)native.Anchor.Z);
        for (int i = 0; i < native.Faces.Count; i++)
        {
            Vector3 a = legacy.Positions[legacy.Indices[3 * i]],
                b = legacy.Positions[legacy.Indices[3 * i + 1]], c = legacy.Positions[legacy.Indices[3 * i + 2]];
            MapCompiledFace face = native.Faces[i];
            AssertNear(legacyOrigin + a, nativeOrigin + native.Offsets[face.A], vertexTolerance);
            AssertNear(legacyOrigin + b, nativeOrigin + native.Offsets[face.B], vertexTolerance);
            AssertNear(legacyOrigin + c, nativeOrigin + native.Offsets[face.C], vertexTolerance);
            AssertNear(LegacyNormal(a, b, c), face.Normal, normalTolerance);
        }
    }

    internal static Vector3 LegacyNormal(Vector3 a, Vector3 b, Vector3 c)
        => Vector3.Normalize(Vector3.Cross(b - a, c - a));

    internal static IEnumerable<int> FallbackCells(TileWorldDocument document, RegionCoord region, int plane)
    {
        TilePlaneData layer = Layer(document, region, plane);
        for (int i = 0; i < TileRegion.TileCount; i++)
            if ((layer.Underlay?[i] ?? 0) == 0 || ((layer.Settings?[i] ?? 0) & (byte)TileSettings.NoDraw) != 0)
                yield return i;
    }

    internal static byte[] CellBytes(TileWorldDocument document, RegionCoord region, int plane)
    {
        TilePlaneData layer = Layer(document, region, plane);
        var bytes = new byte[TileRegion.TileCount * 8];
        for (int i = 0; i < TileRegion.TileCount; i++)
            WriteCellBytes(bytes, i, layer.Underlay?[i] ?? 0, layer.Overlay?[i] ?? 0,
                layer.OverlayShape?[i] ?? 0, layer.OverlayRotation?[i] ?? 0, layer.Settings?[i] ?? 0,
                (byte)MapCellTopology.Auto);
        return bytes;
    }

    internal static byte[] CellBytes(MapSurfacePatch patch)
    {
        var bytes = new byte[patch.Cells.Length * 8];
        for (int i = 0; i < patch.Cells.Length; i++)
        {
            MapSurfaceCell cell = patch.Cells[i];
            WriteCellBytes(bytes, i, cell.Underlay, cell.Overlay, (byte)cell.Cut, cell.Rotation,
                (byte)cell.Flags, (byte)cell.Topology);
        }
        return bytes;
    }

    static TilePlaneData Layer(TileWorldDocument document, RegionCoord region, int plane)
        => (document.GetRegion(region) ?? throw new InvalidOperationException("oracle region is missing")).Plane(plane);

    static void AssertNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, tolerance);
    }

    static void WriteCellBytes(byte[] bytes, int cell, ushort underlay, ushort overlay,
        byte cut, byte rotation, byte flags, byte topology)
    {
        int offset = cell * 8;
        bytes[offset] = (byte)underlay;
        bytes[offset + 1] = (byte)(underlay >> 8);
        bytes[offset + 2] = (byte)overlay;
        bytes[offset + 3] = (byte)(overlay >> 8);
        bytes[offset + 4] = cut;
        bytes[offset + 5] = rotation;
        bytes[offset + 6] = flags;
        bytes[offset + 7] = topology;
    }
}
