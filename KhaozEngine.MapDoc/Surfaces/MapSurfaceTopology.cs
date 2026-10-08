using System;

namespace KhaozEngine.MapDoc.Surfaces;

public enum MapLatticePoint : byte { Sw, Se, Nw, Ne, MidS, MidE, MidN, MidW }
public readonly record struct MapLatticeTriangle(MapLatticePoint A, MapLatticePoint B, MapLatticePoint C, bool Overlay);

/// <summary>The canonical cell table, with counter-clockwise winding in lattice XZ.</summary>
public static class MapSurfaceTopology
{
    public static bool SplitSwNe(int sw, int se, int nw, int ne, MapOverlayCut cut,
        byte rotation, MapCellTopology topology)
    {
        if (rotation > 3 || cut > MapOverlayCut.CornerThreeQuarter || topology > MapCellTopology.ForceNwSe)
            throw new MapDocumentException("invalid cell topology, cut or rotation");
        if (topology != MapCellTopology.Auto)
        {
            bool forced = topology == MapCellTopology.ForceSwNe;
            if (cut == MapOverlayCut.DiagonalHalf && forced != (rotation % 2 == 0))
                throw new MapDocumentException("forced topology contradicts DiagonalHalf rotation");
            return forced;
        }
        return cut == MapOverlayCut.DiagonalHalf ? rotation % 2 == 0
            : Math.Abs((long)sw - ne) <= Math.Abs((long)se - nw);
    }

    public static int Triangulate(MapOverlayCut cut, byte rotation, bool splitSwNe, Span<MapLatticeTriangle> into)
    {
        if (into.Length < 4) throw new ArgumentException("Needs room for 4 triangles.", nameof(into));
        int count;
        if (cut is MapOverlayCut.CornerQuarter or MapOverlayCut.CornerThreeQuarter)
        {
            CornerCut(rotation, cut == MapOverlayCut.CornerThreeQuarter, into);
            count = 4;
        }
        else
        {
            bool firstOverlay = cut != MapOverlayCut.DiagonalHalf || rotation == (splitSwNe ? 2 : 3);
            bool secondOverlay = cut != MapOverlayCut.DiagonalHalf || !firstOverlay;
            into[0] = new(MapLatticePoint.Sw, MapLatticePoint.Se,
                splitSwNe ? MapLatticePoint.Ne : MapLatticePoint.Nw, firstOverlay);
            into[1] = splitSwNe
                ? new(MapLatticePoint.Sw, MapLatticePoint.Ne, MapLatticePoint.Nw, secondOverlay)
                : new(MapLatticePoint.Se, MapLatticePoint.Ne, MapLatticePoint.Nw, secondOverlay);
            count = 2;
        }
        for (int i = 0; i < count; i++)
        {
            MapLatticeTriangle t = into[i];
            (int ax, int az) = LocalTwice(t.A);
            (int bx, int bz) = LocalTwice(t.B);
            (int cx, int cz) = LocalTwice(t.C);
            if ((bx - ax) * (cz - az) - (cx - ax) * (bz - az) < 0)
                into[i] = new(t.A, t.C, t.B, t.Overlay);
        }
        return count;
    }

    static void CornerCut(byte rotation, bool threeQuarter, Span<MapLatticeTriangle> into)
    {
        (MapLatticePoint corner, MapLatticePoint alongX, MapLatticePoint alongZ,
            MapLatticePoint opposite, MapLatticePoint midX, MapLatticePoint midZ) = (rotation & 3) switch
            {
                1 => (MapLatticePoint.Nw, MapLatticePoint.Ne, MapLatticePoint.Sw,
                    MapLatticePoint.Se, MapLatticePoint.MidN, MapLatticePoint.MidW),
                2 => (MapLatticePoint.Ne, MapLatticePoint.Nw, MapLatticePoint.Se,
                    MapLatticePoint.Sw, MapLatticePoint.MidN, MapLatticePoint.MidE),
                3 => (MapLatticePoint.Se, MapLatticePoint.Sw, MapLatticePoint.Ne,
                    MapLatticePoint.Nw, MapLatticePoint.MidS, MapLatticePoint.MidE),
                _ => (MapLatticePoint.Sw, MapLatticePoint.Se, MapLatticePoint.Nw,
                    MapLatticePoint.Ne, MapLatticePoint.MidS, MapLatticePoint.MidW),
            };
        into[0] = new(corner, midX, midZ, !threeQuarter);
        into[1] = new(midX, alongX, opposite, threeQuarter);
        into[2] = new(midX, opposite, alongZ, threeQuarter);
        into[3] = new(midX, alongZ, midZ, threeQuarter);
    }

    internal static (int X, int Z) LocalTwice(MapLatticePoint point) => point switch
    {
        MapLatticePoint.Sw => (0, 0),
        MapLatticePoint.Se => (2, 0),
        MapLatticePoint.Nw => (0, 2),
        MapLatticePoint.Ne => (2, 2),
        MapLatticePoint.MidS => (1, 0),
        MapLatticePoint.MidE => (2, 1),
        MapLatticePoint.MidN => (1, 2),
        MapLatticePoint.MidW => (0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(point)),
    };

    internal static (MapLatticePoint First, MapLatticePoint Second) Ends(MapLatticePoint point) => point switch
    {
        MapLatticePoint.MidS => (MapLatticePoint.Sw, MapLatticePoint.Se),
        MapLatticePoint.MidE => (MapLatticePoint.Se, MapLatticePoint.Ne),
        MapLatticePoint.MidN => (MapLatticePoint.Nw, MapLatticePoint.Ne),
        MapLatticePoint.MidW => (MapLatticePoint.Sw, MapLatticePoint.Nw),
        _ => (point, point),
    };
}
