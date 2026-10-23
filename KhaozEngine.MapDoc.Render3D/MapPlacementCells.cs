using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Half-open native region cells, subdivided equally to bound anchor-relative coordinates.</summary>
internal static class MapPlacementCells
{
    // Native contract. Render.Tests pins parity without a production TileWorld dependency.
    internal const int RegionSizeInTiles = 64;
    const double MaxCellEdge = 1024;

    internal static PropClusterKey Key(string layerId, MapEffectiveTransform pose, float tileSize, bool roof = false)
    {
        var grid = Grid(tileSize);
        try { return new(layerId, Axis(pose.X), Axis(pose.Z), 0) { Cohort = roof ? 1 : 0 }; }
        catch (OverflowException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.NotRepresentable, "Native placement cell is not representable."); }

        int Axis(double position)
        {
            int cell = checked((int)Math.Floor(position / (grid.RegionEdge / grid.Divisions)));
            // Compare with the same boundaries Domain uses, including non-binary equal subdivisions.
            if (position < grid.Boundary(cell)) return checked(cell - 1);
            if (position >= grid.Boundary((long)cell + 1)) return checked(cell + 1);
            return cell;
        }
    }

    internal static (RenderAnchor Anchor, RectArea Area) Domain(PropClusterKey key, float tileSize, long anchorY)
    {
        if (key.Plane != 0 || key.Cohort is not (0 or 1)) throw new MapDocumentException("Invalid native placement cell.");
        var grid = Grid(tileSize);
        double x = grid.Boundary(key.X), z = grid.Boundary(key.Z);
        try
        {
            // RenderAnchor is whole metres. Any fractional cell origin stays in the small local area.
            var anchor = new RenderAnchor(checked((long)Math.Floor(x)), anchorY, checked((long)Math.Floor(z)));
            return (anchor, new((float)(x - anchor.X), (float)(z - anchor.Z),
                (float)(grid.Boundary((long)key.X + 1) - anchor.X), (float)(grid.Boundary((long)key.Z + 1) - anchor.Z)));
        }
        catch (OverflowException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.NotRepresentable, "Native placement cell anchor is not representable."); }
    }

    internal static MapBox3 Coverage(PropClusterKey key, float tileSize)
    {
        var grid = Grid(tileSize);
        // Discovery uses exact binary64 cell boundaries, not the proxy's rounded local RectArea.
        return new(grid.Boundary(key.X), 0, grid.Boundary(key.Z),
            grid.Boundary((long)key.X + 1), 0, grid.Boundary((long)key.Z + 1));
    }

    static CellGrid Grid(float tileSize)
    {
        if (!float.IsFinite(tileSize) || tileSize <= 0) throw new MapDocumentException("Invalid native placement tile size.");
        double regionEdge = RegionSizeInTiles * (double)tileSize;
        return new(regionEdge, Math.Ceiling(regionEdge / MaxCellEdge));
    }

    readonly record struct CellGrid(double RegionEdge, double Divisions)
    {
        // Multiplication before division preserves the region grid instead of accumulating rounded edges.
        internal double Boundary(long cell) => RegionEdge * cell / Divisions;
    }

    internal static string Owner(PropClusterKey key) => FormattableString.Invariant($"cluster/{key.LayerId}/{key.X}/{key.Z}/{key.Plane}");
}
