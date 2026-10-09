using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocCompatibility;

internal sealed record LegacyOracleCase(string Name, int WorldX, int WorldZ, int Plane);

internal sealed class LegacyOracleWorld
{
    internal TileWorldDocument Document { get; }
    internal IReadOnlyList<LegacyOracleCase> Cases { get; }

    LegacyOracleWorld(TileWorldDocument document, List<LegacyOracleCase> cases)
    {
        Document = document;
        Cases = cases.AsReadOnly();
    }

    internal static LegacyOracleWorld Create()
    {
        var document = new TileWorldDocument { Id = "public-legacy-oracle", PlaneHeight = 4.5f };
        document.GetOrCreateRegion(new RegionCoord(-1, 0));
        document.GetOrCreateRegion(new RegionCoord(0, 0));
        var cases = new List<LegacyOracleCase>();

        void Add(string name, int x, int z, int plane = 0, ushort underlay = 1)
        {
            document.SetUnderlay(x, z, plane, underlay);
            cases.Add(new(name, x, z, plane));
        }

        void Corners(int x, int z, int plane, short sw, short se, short nw, short ne)
        {
            document.SetCornerHeightCm(x, z, plane, sw);
            document.SetCornerHeightCm(x + 1, z, plane, se);
            document.SetCornerHeightCm(x, z + 1, plane, nw);
            document.SetCornerHeightCm(x + 1, z + 1, plane, ne);
        }

        int index = 0;
        foreach (TileOverlayShape shape in Enum.GetValues<TileOverlayShape>())
            for (int rotation = 0; rotation < 4; rotation++)
            {
                int x = -32 + 2 * index++;
                Add($"cut-{shape}-{rotation}", x, 4);
                document.SetOverlay(x, 4, 0, 5);
                document.SetOverlayShape(x, 4, 0, shape);
                document.SetOverlayRotation(x, 4, 0, rotation);
                Corners(x, 4, 0, 12, 137, -41, 206);
            }

        for (int rotation = 0; rotation < 4; rotation++)
        {
            int x = 2 + 2 * rotation;
            Add($"diagonal-no-overlay-{rotation}", x, 4);
            document.SetOverlay(x, 4, 0, 0);
            document.SetOverlayShape(x, 4, 0, TileOverlayShape.DiagonalHalf);
            document.SetOverlayRotation(x, 4, 0, rotation);
        }

        foreach (var (name, flag, x) in new[]
        {
            ("blocked", TileSettings.Blocked, 12), ("indoors", TileSettings.Indoors, 14),
            ("bridge", TileSettings.Bridge, 16), ("nodraw", TileSettings.NoDraw, 18),
            ("featheroverlay", TileSettings.FeatherOverlay, 20),
        })
        {
            Add("flag-" + name, x, 4);
            document.SetSettings(x, 4, 0, flag);
            if (flag == TileSettings.FeatherOverlay) document.SetOverlay(x, 4, 0, 5);
        }

        Add("void", 22, 4, underlay: 0);
        Corners(24, 4, 0, 100, 200, 300, 400);
        Add("derived-plane-1", 24, 4, plane: 1);
        Add("override-plane-2", 26, 4, plane: 2);
        Corners(26, 4, 2, 1050, 1100, 1150, 1200);
        Add("seam-west", -1, 12);
        Add("seam-east", 0, 12);
        Corners(-1, 12, 0, 100, 200, 300, 400);
        Corners(0, 12, 0, 200, 500, 400, 600);
        Add("extreme", 30, 4);
        Corners(30, 4, 0, short.MaxValue, short.MinValue, 0, 1);
        return new(document, cases);
    }
}
