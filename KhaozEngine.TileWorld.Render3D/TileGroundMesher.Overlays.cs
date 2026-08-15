using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// The shaped half of the ground mesher: a tile whose overlay cuts it into an overlay part and an underlay part.
// Both parts are built from the same points, so the cut edge carries one position and one normal on each side and
// the two parts meet without a crack or a lighting seam.
public static partial class TileGroundMesher
{
    /// <summary>Emits the tile as a cut pair of parts and returns true, or returns false for a shape this mesher
    /// does not cut, which leaves the caller to draw the plain full tile.</summary>
    static bool TryAddShapedTile(
        MeshAccumulator mesh,
        in TileMeshContext c,
        int lx,
        int lz,
        TileOverlayShape shape,
        int rotation,
        Vector4 overlayColor,
        bool splitSwNe)
    {
        TilePoint sw = Corner(c, lx, lz, 0, 0);
        TilePoint se = Corner(c, lx, lz, 1, 0);
        TilePoint nw = Corner(c, lx, lz, 0, 1);
        TilePoint ne = Corner(c, lx, lz, 1, 1);

        switch (shape)
        {
            case TileOverlayShape.DiagonalHalf:
                AddDiagonalHalf(mesh, c, sw, se, nw, ne, rotation, overlayColor, splitSwNe);
                return true;
            case TileOverlayShape.CornerQuarter:
                AddCornerCut(mesh, c, sw, se, nw, ne, rotation, overlayColor, threeQuarter: false);
                return true;
            case TileOverlayShape.CornerThreeQuarter:
                AddCornerCut(mesh, c, sw, se, nw, ne, rotation, overlayColor, threeQuarter: true);
                return true;
            default:
                // Full, or a shape id from a newer file than this build. Both draw as a full tile, which is visible
                // and wrong rather than invisible.
                return false;
        }
    }

    /// <summary>Paints one of the tile's two halves with the overlay. The rotation forces the diagonal through
    /// <see cref="TileTriangulation.SplitSwNe"/>, so these are exactly the two triangles the full-tile path would
    /// emit, one of them painted.</summary>
    static void AddDiagonalHalf(
        MeshAccumulator mesh,
        in TileMeshContext c,
        in TilePoint sw,
        in TilePoint se,
        in TilePoint nw,
        in TilePoint ne,
        int rotation,
        Vector4 overlayColor,
        bool splitSwNe)
    {
        if (splitSwNe)
        {
            // Split SW to NE, so the halves are the south east (SW, SE, NE) and the north west (SW, NE, NW).
            // Rotation 0 paints the north west, rotation 2 the south east. Odd rotations do not reach this branch.
            bool southEast = rotation == 2;
            AddPart(mesh, c, sw, se, ne, southEast ? overlayColor : null);
            AddPart(mesh, c, sw, ne, nw, southEast ? null : overlayColor);
        }
        else
        {
            // Split NW to SE: the south west half (SW, SE, NW) and the north east half (SE, NE, NW). Rotation 1
            // paints the north east, rotation 3 the south west.
            bool southWest = rotation == 3;
            AddPart(mesh, c, sw, se, nw, southWest ? overlayColor : null);
            AddPart(mesh, c, se, ne, nw, southWest ? null : overlayColor);
        }
    }

    /// <summary>Cuts the tile across the two mid-edge points beside one corner: four triangles, the small corner
    /// triangle plus the remaining pentagon fanned from the mid-edge point along x. The overlay takes the small
    /// triangle for a quarter and the pentagon for a three quarter.</summary>
    static void AddCornerCut(
        MeshAccumulator mesh,
        in TileMeshContext c,
        in TilePoint sw,
        in TilePoint se,
        in TilePoint nw,
        in TilePoint ne,
        int rotation,
        Vector4 overlayColor,
        bool threeQuarter)
    {
        // Rotation picks the cut corner clockwise from the south west: 0 SW, 1 NW, 2 NE, 3 SE. Beside it sit the
        // corner adjacent along x, the corner adjacent along z, and the opposite corner.
        TilePoint corner;
        TilePoint alongX;
        TilePoint alongZ;
        TilePoint opposite;
        switch (rotation & 3)
        {
            case 1:
                corner = nw;
                alongX = ne;
                alongZ = sw;
                opposite = se;
                break;
            case 2:
                corner = ne;
                alongX = nw;
                alongZ = se;
                opposite = sw;
                break;
            case 3:
                corner = se;
                alongX = sw;
                alongZ = ne;
                opposite = nw;
                break;
            default:
                corner = sw;
                alongX = se;
                alongZ = nw;
                opposite = ne;
                break;
        }

        TilePoint midX = Midpoint(corner, alongX);
        TilePoint midZ = Midpoint(corner, alongZ);
        Vector4? small = threeQuarter ? null : overlayColor;
        Vector4? pentagon = threeQuarter ? overlayColor : null;

        AddPart(mesh, c, corner, midX, midZ, small);
        AddPart(mesh, c, midX, alongX, opposite, pentagon);
        AddPart(mesh, c, midX, opposite, alongZ, pentagon);
        AddPart(mesh, c, midX, alongZ, midZ, pentagon);
    }

    /// <summary>One triangle of a cut tile, painted with the overlay colour when one is given and left to the
    /// blended underlay otherwise.</summary>
    static void AddPart(
        MeshAccumulator mesh,
        in TileMeshContext c,
        in TilePoint a,
        in TilePoint b,
        in TilePoint d,
        Vector4? overlay) =>
        AddTriangle(mesh, c, a.ToVertex(overlay), b.ToVertex(overlay), d.ToVertex(overlay));

    /// <summary>One corner of the tile at region-local (lx, lz), offset by a 0 or 1 corner step on each axis. A
    /// non-null <paramref name="flat"/> is the overlay colour, which replaces the blended underlay.</summary>
    static TilePoint Corner(in TileMeshContext c, int lx, int lz, int dx, int dz, Vector4? flat = null)
    {
        int cx = c.OriginX + lx + dx;
        int cz = c.OriginZ + lz + dz;
        var position = new Vector3(
            (lx + dx) * c.TileSize,
            c.Doc.CornerHeightCm(cx, cz, c.Plane) * 0.01f,
            (lz + dz) * c.TileSize);
        Vector3 normal = c.Options.SmoothNormals ? CornerNormal(c.Doc, cx, cz, c.Plane) : Vector3.UnitY;
        Vector4 color = flat ?? CornerColor(c.Doc, c.Catalogs, cx, cz, c.Plane, c.Options);
        return new TilePoint(position, normal, color, new Vector2(dx, dz));
    }

    /// <summary>The mid-edge point between two corners: position, colour and uv averaged, and the normal averaged
    /// then renormalised, so a cut edge lights as the whole tile's surface does there.</summary>
    static TilePoint Midpoint(in TilePoint a, in TilePoint b)
    {
        Vector3 normal = (a.Normal + b.Normal) * 0.5f;
        return new TilePoint(
            (a.Position + b.Position) * 0.5f,
            normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitY,
            (a.Color + b.Color) * 0.5f,
            (a.Uv + b.Uv) * 0.5f);
    }

    /// <summary>A point the tile's triangles are built from: one of its corners, or a mid-edge point averaged from
    /// two of them.</summary>
    internal readonly struct TilePoint
    {
        internal TilePoint(Vector3 position, Vector3 normal, Vector4 color, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            Color = color;
            Uv = uv;
        }

        /// <summary>Region-local position, with absolute Y in metres.</summary>
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
