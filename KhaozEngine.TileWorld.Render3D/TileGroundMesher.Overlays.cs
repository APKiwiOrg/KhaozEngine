using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// The vertex half of the tile triangulation. Which triangles a tile is cut into is TileTriangulation's business,
// shared with the raycast so a click lands on the triangle that is drawn. What each of their lattice points
// becomes, its position, normal, colour and uv, is this file's. Both parts of a cut tile are built from the same
// points, so the cut edge carries one position and one normal on each side and the parts meet without a crack.
public static partial class TileGroundMesher
{
    /// <summary>Emits one tile the way the shared triangulation cuts it, painting each triangle with the flat
    /// overlay colour or leaving it to the blended underlay.</summary>
    static void AddCutTile(
        MeshAccumulator mesh,
        in TileMeshContext c,
        int lx,
        int lz,
        TileOverlayShape shape,
        int rotation,
        Vector4? flat,
        bool splitSwNe)
    {
        Span<TileTriangle> triangles = stackalloc TileTriangle[TileTriangulation.MaxTriangles];
        int count = TileTriangulation.Triangulate(shape, rotation, splitSwNe, triangles);

        // A full overlay paints every corner, so the blended underlay is never read there and its per-corner
        // blends are skipped. A cut tile always needs them, because it always keeps some ground.
        Vector4? corners = shape == TileOverlayShape.Full ? flat : null;
        LatticePoint sw = Corner(c, lx, lz, 0, 0, corners);
        LatticePoint se = Corner(c, lx, lz, 1, 0, corners);
        LatticePoint nw = Corner(c, lx, lz, 0, 1, corners);
        LatticePoint ne = Corner(c, lx, lz, 1, 1, corners);

        for (int i = 0; i < count; i++)
        {
            TileTriangle t = triangles[i];
            Vector4? paint = t.Overlay ? flat : null;
            AddTriangle(
                mesh,
                c,
                At(t.A, sw, se, nw, ne).ToVertex(paint),
                At(t.B, sw, se, nw, ne).ToVertex(paint),
                At(t.C, sw, se, nw, ne).ToVertex(paint));
        }
    }

    /// <summary>The vertex data at one lattice point of the tile: a corner as it stands, a mid-edge point as the
    /// average of the two corners it lies between.</summary>
    static LatticePoint At(TilePoint point, in LatticePoint sw, in LatticePoint se, in LatticePoint nw, in LatticePoint ne)
    {
        TileTriangulation.Ends(point, out TilePoint first, out TilePoint second);
        LatticePoint a = Pick(first, sw, se, nw, ne);
        // A corner is its own pair, and taking it as it stands rather than averaging it with itself keeps every
        // copy of that corner bit-identical across the tiles and regions that share it.
        return first == second ? a : Midpoint(a, Pick(second, sw, se, nw, ne));
    }

    static LatticePoint Pick(TilePoint corner, in LatticePoint sw, in LatticePoint se, in LatticePoint nw, in LatticePoint ne) =>
        corner switch
        {
            TilePoint.Se => se,
            TilePoint.Nw => nw,
            TilePoint.Ne => ne,
            _ => sw,
        };

    /// <summary>One corner of the tile at region-local (lx, lz), offset by a 0 or 1 corner step on each axis. A
    /// non-null <paramref name="flat"/> is the overlay colour, which replaces the blended underlay.</summary>
    static LatticePoint Corner(in TileMeshContext c, int lx, int lz, int dx, int dz, Vector4? flat)
    {
        int cx = c.OriginX + lx + dx;
        int cz = c.OriginZ + lz + dz;
        Vector3 position = TileWorldSpace.ToWorld(
            lx + dx,
            c.Doc.CornerHeightCm(cx, cz, c.Plane) * 0.01f,
            lz + dz,
            c.TileSize);
        Vector3 normal = c.Options.SmoothNormals ? CornerNormal(c.Doc, cx, cz, c.Plane) : Vector3.UnitY;
        Vector4 color = flat ?? CornerColor(c.Doc, c.Catalogs, cx, cz, c.Plane, c.Options);
        return new LatticePoint(position, normal, color, new Vector2(dx, dz));
    }

    /// <summary>The mid-edge point between two corners: position, colour and uv averaged, and the normal averaged
    /// then renormalised, so a cut edge lights as the whole tile's surface does there.</summary>
    static LatticePoint Midpoint(in LatticePoint a, in LatticePoint b)
    {
        Vector3 normal = (a.Normal + b.Normal) * 0.5f;
        return new LatticePoint(
            (a.Position + b.Position) * 0.5f,
            normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitY,
            (a.Color + b.Color) * 0.5f,
            (a.Uv + b.Uv) * 0.5f);
    }

    /// <summary>What one lattice point of a tile carries: one of its corners, or a mid-edge point averaged from
    /// two of them.</summary>
    internal readonly struct LatticePoint
    {
        internal LatticePoint(Vector3 position, Vector3 normal, Vector4 color, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            Color = color;
            Uv = uv;
        }

        /// <summary>Region-local position (z negative, see <see cref="TileWorldSpace"/>), absolute Y in metres.</summary>
        public Vector3 Position { get; }
        /// <summary>The lattice normal here.</summary>
        public Vector3 Normal { get; }
        /// <summary>The colour this point carries unless the triangle using it is painted with an overlay.</summary>
        public Vector4 Color { get; }
        /// <summary>Tile-local uv, 0 to 1 on each axis.</summary>
        public Vector2 Uv { get; }

        /// <summary>This point as a vertex, painted with <paramref name="overlay"/> when one is given.</summary>
        public ModelVertex ToVertex(Vector4? overlay) => new(Position, Normal, overlay ?? Color, Uv);
    }
}
