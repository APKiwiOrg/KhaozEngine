using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>One of the eight lattice points a tile's triangles are built from: its four corners and the four
/// mid-edge points between them. A mid-edge point's height, normal and colour are whatever the caller makes of
/// the two corners it lies between, see <see cref="TileTriangulation.Ends"/>.</summary>
public enum TilePoint : byte
{
    /// <summary>The south west corner.</summary>
    Sw = 0,
    /// <summary>The south east corner.</summary>
    Se = 1,
    /// <summary>The north west corner.</summary>
    Nw = 2,
    /// <summary>The north east corner.</summary>
    Ne = 3,
    /// <summary>Midway along the south edge, between SW and SE.</summary>
    MidS = 4,
    /// <summary>Midway along the east edge, between SE and NE.</summary>
    MidE = 5,
    /// <summary>Midway along the north edge, between NW and NE.</summary>
    MidN = 6,
    /// <summary>Midway along the west edge, between SW and NW.</summary>
    MidW = 7,
}

/// <summary>One triangle of a triangulated tile.</summary>
/// <param name="A">Its first lattice point.</param>
/// <param name="B">Its second lattice point.</param>
/// <param name="C">Its third lattice point.</param>
/// <param name="Overlay">True when the tile's overlay material paints it rather than the ground underneath.</param>
public readonly record struct TileTriangle(TilePoint A, TilePoint B, TilePoint C, bool Overlay);

/// <summary>The one tile triangulation, shared by the raycast here and the ground mesher in
/// <c>KhaozEngine.TileWorld.Render3D</c>, so a click lands on the triangle that is drawn. Corners: h00 SW,
/// h10 SE, h01 NW, h11 NE.</summary>
public static class TileTriangulation
{
    /// <summary>The most triangles any shape cuts a tile into, and so the room
    /// <see cref="Triangulate"/> always needs.</summary>
    public const int MaxTriangles = 4;

    /// <summary>True when the tile splits SW to NE (triangles SW-SE-NE and SW-NE-NW), false when NW to SE
    /// (triangles SW-SE-NW and SE-NE-NW). A <see cref="TileOverlayShape.DiagonalHalf"/> overlay forces the
    /// split (even rotation SW-NE, odd NW-SE), otherwise the diagonal whose corners differ least in height
    /// wins, which removes saddle artifacts and is deterministic.</summary>
    public static bool SplitSwNe(short h00, short h10, short h01, short h11, TileOverlayShape shape, int overlayRotation)
    {
        if (shape == TileOverlayShape.DiagonalHalf) return (overlayRotation & 1) == 0;
        return Math.Abs(h00 - h11) <= Math.Abs(h10 - h01);
    }

    /// <summary>Writes the tile's triangles into <paramref name="into"/> and returns how many: two for a full
    /// tile or a diagonal half, four for a corner cut. Pass the shape the tile actually draws with, so a shape
    /// with no overlay material is passed as <see cref="TileOverlayShape.Full"/> and an unknown shape id draws
    /// as one too. Every triangle comes back wound the same way (counter-clockwise on x and z), so a pass that
    /// culls a face direction keeps or drops all of them together rather than half of them.</summary>
    public static int Triangulate(TileOverlayShape shape, int rotation, bool splitSwNe, Span<TileTriangle> into)
    {
        if (into.Length < MaxTriangles)
            throw new ArgumentException($"Needs room for {MaxTriangles} triangles.", nameof(into));

        int count = shape switch
        {
            TileOverlayShape.DiagonalHalf => DiagonalHalf(rotation, splitSwNe, into),
            TileOverlayShape.CornerQuarter => CornerCut(rotation, into, threeQuarter: false),
            TileOverlayShape.CornerThreeQuarter => CornerCut(rotation, into, threeQuarter: true),
            _ => Full(splitSwNe, into),
        };
        for (int i = 0; i < count; i++) into[i] = Wound(into[i]);
        return count;
    }

    /// <summary>Where the point sits in tile-local terms, 0 to 1 east on x and 0 to 1 north on z.</summary>
    public static Vector2 Local(TilePoint point) => point switch
    {
        TilePoint.Se => new Vector2(1f, 0f),
        TilePoint.Nw => new Vector2(0f, 1f),
        TilePoint.Ne => new Vector2(1f, 1f),
        TilePoint.MidS => new Vector2(0.5f, 0f),
        TilePoint.MidE => new Vector2(1f, 0.5f),
        TilePoint.MidN => new Vector2(0.5f, 1f),
        TilePoint.MidW => new Vector2(0f, 0.5f),
        _ => Vector2.Zero,
    };

    /// <summary>The two corners a lattice point averages. A corner is its own pair, so a caller maps every point
    /// the same way instead of treating mid-edge points as a special case.</summary>
    public static void Ends(TilePoint point, out TilePoint first, out TilePoint second)
    {
        switch (point)
        {
            case TilePoint.MidS: first = TilePoint.Sw; second = TilePoint.Se; break;
            case TilePoint.MidE: first = TilePoint.Se; second = TilePoint.Ne; break;
            case TilePoint.MidN: first = TilePoint.Nw; second = TilePoint.Ne; break;
            case TilePoint.MidW: first = TilePoint.Sw; second = TilePoint.Nw; break;
            default: first = point; second = point; break;
        }
    }

    // The plain pair along whichever diagonal the split rule picked. Both halves are flagged as overlay, because
    // a full overlay covers the whole tile and a tile with no overlay ignores the flag.
    static int Full(bool splitSwNe, Span<TileTriangle> into)
    {
        if (splitSwNe)
        {
            into[0] = new TileTriangle(TilePoint.Sw, TilePoint.Se, TilePoint.Ne, true);
            into[1] = new TileTriangle(TilePoint.Sw, TilePoint.Ne, TilePoint.Nw, true);
        }
        else
        {
            into[0] = new TileTriangle(TilePoint.Sw, TilePoint.Se, TilePoint.Nw, true);
            into[1] = new TileTriangle(TilePoint.Se, TilePoint.Ne, TilePoint.Nw, true);
        }
        return 2;
    }

    // The same pair, with the overlay painting one half of it. The rotation forced the diagonal through
    // SplitSwNe, so an even rotation only ever reaches the first branch and an odd one the second.
    static int DiagonalHalf(int rotation, bool splitSwNe, Span<TileTriangle> into)
    {
        if (splitSwNe)
        {
            // Split SW to NE: the south east half (SW, SE, NE) and the north west half (SW, NE, NW). Rotation 2
            // paints the south east one, rotation 0 the north west one.
            bool southEast = (rotation & 3) == 2;
            into[0] = new TileTriangle(TilePoint.Sw, TilePoint.Se, TilePoint.Ne, southEast);
            into[1] = new TileTriangle(TilePoint.Sw, TilePoint.Ne, TilePoint.Nw, !southEast);
        }
        else
        {
            // Split NW to SE: the south west half (SW, SE, NW) and the north east half (SE, NE, NW). Rotation 3
            // paints the south west one, rotation 1 the north east one.
            bool southWest = (rotation & 3) == 3;
            into[0] = new TileTriangle(TilePoint.Sw, TilePoint.Se, TilePoint.Nw, southWest);
            into[1] = new TileTriangle(TilePoint.Se, TilePoint.Ne, TilePoint.Nw, !southWest);
        }
        return 2;
    }

    // The cut across the two mid-edge points beside one corner: the small triangle there plus the remaining
    // pentagon fanned from the mid-edge point along x. A quarter paints the small triangle, a three quarter the
    // fan.
    static int CornerCut(int rotation, Span<TileTriangle> into, bool threeQuarter)
    {
        // Rotation picks the cut corner clockwise from the south west: 0 SW, 1 NW, 2 NE, 3 SE. Beside it sit the
        // corner adjacent along x, the corner adjacent along z, the opposite corner, and the two mid-edge points.
        TilePoint corner;
        TilePoint alongX;
        TilePoint alongZ;
        TilePoint opposite;
        TilePoint midX;
        TilePoint midZ;
        switch (rotation & 3)
        {
            case 1:
                corner = TilePoint.Nw;
                alongX = TilePoint.Ne;
                alongZ = TilePoint.Sw;
                opposite = TilePoint.Se;
                midX = TilePoint.MidN;
                midZ = TilePoint.MidW;
                break;
            case 2:
                corner = TilePoint.Ne;
                alongX = TilePoint.Nw;
                alongZ = TilePoint.Se;
                opposite = TilePoint.Sw;
                midX = TilePoint.MidN;
                midZ = TilePoint.MidE;
                break;
            case 3:
                corner = TilePoint.Se;
                alongX = TilePoint.Sw;
                alongZ = TilePoint.Ne;
                opposite = TilePoint.Nw;
                midX = TilePoint.MidS;
                midZ = TilePoint.MidE;
                break;
            default:
                corner = TilePoint.Sw;
                alongX = TilePoint.Se;
                alongZ = TilePoint.Nw;
                opposite = TilePoint.Ne;
                midX = TilePoint.MidS;
                midZ = TilePoint.MidW;
                break;
        }

        into[0] = new TileTriangle(corner, midX, midZ, !threeQuarter);
        into[1] = new TileTriangle(midX, alongX, opposite, threeQuarter);
        into[2] = new TileTriangle(midX, opposite, alongZ, threeQuarter);
        into[3] = new TileTriangle(midX, alongZ, midZ, threeQuarter);
        return 4;
    }

    // The corner fan is mirrored at an odd rotation, which would otherwise come out wound the other way round
    // from every other triangle. That is inert where nothing culls, but a pass that culls one face direction
    // would drop exactly those triangles, so the winding is normalised here rather than at each call site.
    static TileTriangle Wound(in TileTriangle t) =>
        SignedArea(t) < 0f ? new TileTriangle(t.A, t.C, t.B, t.Overlay) : t;

    // Twice the triangle's signed area in the ground plane, positive when it winds counter-clockwise on x and z.
    static float SignedArea(in TileTriangle t)
    {
        Vector2 a = Local(t.A);
        Vector2 b = Local(t.B);
        Vector2 c = Local(t.C);
        return (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
    }
}
