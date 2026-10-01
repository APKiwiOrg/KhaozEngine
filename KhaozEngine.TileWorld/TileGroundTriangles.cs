using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>How one drawable tile is cut into ground triangles.</summary>
/// <param name="Cut">The shape the tile is triangulated with: the authored overlay shape when the tile has an
/// overlay, <see cref="TileOverlayShape.Full"/> when it has none.</param>
/// <param name="Rotation">The authored overlay rotation, as it stands.</param>
/// <param name="SplitSwNe">True when the tile splits SW to NE, see <see cref="TileTriangulation.SplitSwNe"/>. Decided
/// from the AUTHORED shape, so a diagonal half forces it even on a tile with no overlay to cut.</param>
/// <param name="TriangleCount">How many triangles the cut writes: two, or four for a corner cut.</param>
public readonly record struct TileGroundCell(TileOverlayShape Cut, int Rotation, bool SplitSwNe, int TriangleCount);

/// <summary>
/// The full-detail ground triangle rule, GPU-free: which tiles draw ground, how each drawable tile is cut and split,
/// and where each of its lattice points sits. The ground mesher draws exactly these triangles, so a headless reader
/// (a server walk test, a raycast, a bake) can ask what the ground looks like without a renderer. A tile marked
/// <see cref="TileSettings.FeatherOverlay"/> draws more, smaller triangles, but it subdivides this same surface
/// without moving it.
/// <para>A tile has an overlay when its overlay id is not 0, whether or not the catalogs define that material. A
/// slot map hands every nonzero id a slot, a dangling one landing on the reserved missing slot, so the mesher paints
/// the cut of an undefined overlay rather than dropping it. That is why the rule reads no catalogs.</para>
/// </summary>
public static class TileGroundTriangles
{
    /// <summary>True when the tile draws ground: it has an underlay and is not marked
    /// <see cref="TileSettings.NoDraw"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static bool IsDrawable(TileWorldDocument document, int worldX, int worldZ, int plane)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.GetUnderlay(worldX, worldZ, plane) != 0
            && (document.GetSettings(worldX, worldZ, plane) & TileSettings.NoDraw) == 0;
    }

    /// <summary>How the tile is cut, with its triangles written into <paramref name="triangles"/> in the order and
    /// winding <see cref="TileTriangulation.Triangulate"/> gives them. False, with a default cell and nothing
    /// written, when the tile does not draw.</summary>
    /// <param name="document">The world.</param>
    /// <param name="worldX">World tile x.</param>
    /// <param name="worldZ">World tile z.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="cell">The cut, rotation, split and triangle count.</param>
    /// <param name="triangles">Room for <see cref="TileTriangulation.MaxTriangles"/> triangles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is shorter than
    /// <see cref="TileTriangulation.MaxTriangles"/>.</exception>
    public static bool TryDescribe(
        TileWorldDocument document,
        int worldX,
        int worldZ,
        int plane,
        out TileGroundCell cell,
        Span<TileLatticeTriangle> triangles)
    {
        if (triangles.Length < TileTriangulation.MaxTriangles)
            throw new ArgumentException($"Needs room for {TileTriangulation.MaxTriangles} triangles.", nameof(triangles));
        if (!IsDrawable(document, worldX, worldZ, plane))
        {
            cell = default;
            return false;
        }

        short h00 = document.CornerHeightCm(worldX, worldZ, plane);
        short h10 = document.CornerHeightCm(worldX + 1, worldZ, plane);
        short h01 = document.CornerHeightCm(worldX, worldZ + 1, plane);
        short h11 = document.CornerHeightCm(worldX + 1, worldZ + 1, plane);
        TileOverlayShape shape = document.GetOverlayShape(worldX, worldZ, plane);
        int rotation = document.GetOverlayRotation(worldX, worldZ, plane);
        bool splitSwNe = TileTriangulation.SplitSwNe(h00, h10, h01, h11, shape, rotation);

        // A shape only cuts the tile when there is an overlay to paint into the cut, and the split above still
        // comes from the authored shape, because a diagonal half forces its diagonal either way.
        TileOverlayShape cut = document.GetOverlay(worldX, worldZ, plane) == 0 ? TileOverlayShape.Full : shape;
        int count = TileTriangulation.Triangulate(cut, rotation, splitSwNe, triangles);
        cell = new TileGroundCell(cut, rotation, splitSwNe, count);
        return true;
    }

    /// <summary>Where one lattice point of the tile sits, relative to the tile corner
    /// (<paramref name="originX"/>, <paramref name="originZ"/>): a corner as it stands, a mid-edge point midway
    /// between the two corners it lies between. Heights are absolute metres and z is negated against tile z.</summary>
    /// <param name="document">The world.</param>
    /// <param name="worldX">World tile x of the tile.</param>
    /// <param name="worldZ">World tile z of the tile.</param>
    /// <param name="plane">The plane whose height lattice is read.</param>
    /// <param name="point">The lattice point.</param>
    /// <param name="originX">World tile x the position is relative to, a region's origin for a region mesh.</param>
    /// <param name="originZ">World tile z the position is relative to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static Vector3 LatticePosition(
        TileWorldDocument document,
        int worldX,
        int worldZ,
        int plane,
        TileLatticePoint point,
        int originX,
        int originZ)
    {
        ArgumentNullException.ThrowIfNull(document);
        TileTriangulation.Ends(point, out TileLatticePoint first, out TileLatticePoint second);
        Vector3 a = CornerPosition(document, worldX, worldZ, plane, first, originX, originZ);
        // A corner is its own pair, and taking it as it stands rather than averaging it with itself keeps every
        // copy of that corner bit-identical across the tiles and regions that share it.
        return first == second
            ? a
            : (a + CornerPosition(document, worldX, worldZ, plane, second, originX, originZ)) * 0.5f;
    }

    /// <summary>Every full-detail ground triangle of one region-plane, in region-local positions, tile by tile with
    /// z outer and x inner, each tile's triangles in <see cref="TryDescribe"/> order and each point placed by
    /// <see cref="LatticePosition"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static TileGroundMesh Build(TileWorldDocument document, RegionCoord region, int plane)
    {
        ArgumentNullException.ThrowIfNull(document);

        var positions = new List<Vector3>();
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int originX = region.OriginX;
        int originZ = region.OriginZ;
        for (int lz = 0; lz < TileRegion.Size; lz++)
            for (int lx = 0; lx < TileRegion.Size; lx++)
            {
                int x = originX + lx;
                int z = originZ + lz;
                if (!TryDescribe(document, x, z, plane, out TileGroundCell cell, triangles)) continue;

                for (int i = 0; i < cell.TriangleCount; i++)
                {
                    positions.Add(LatticePosition(document, x, z, plane, triangles[i].A, originX, originZ));
                    positions.Add(LatticePosition(document, x, z, plane, triangles[i].B, originX, originZ));
                    positions.Add(LatticePosition(document, x, z, plane, triangles[i].C, originX, originZ));
                }
            }

        var indices = new int[positions.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        return new TileGroundMesh(region, plane, positions.ToArray(), indices);
    }

    static Vector3 CornerPosition(
        TileWorldDocument document, int worldX, int worldZ, int plane, TileLatticePoint corner, int originX, int originZ)
    {
        int cornerX = worldX + (corner is TileLatticePoint.Se or TileLatticePoint.Ne ? 1 : 0);
        int cornerZ = worldZ + (corner is TileLatticePoint.Nw or TileLatticePoint.Ne ? 1 : 0);
        return TileWorldSpace.ToWorld(cornerX - originX, document.CornerHeightCm(cornerX, cornerZ, plane) * 0.01f,
                                      cornerZ - originZ, document.TileSize);
    }
}
