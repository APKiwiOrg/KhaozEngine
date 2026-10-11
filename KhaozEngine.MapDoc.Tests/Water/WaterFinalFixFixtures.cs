using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    internal static MapDocument TangentialCeiling(bool vertexOnly)
    {
        MapDocument doc = OceanOverCaves();
        SetHeights(doc, "lake-roof", (x, row) =>
            -3400 + 100 * (int)(Math.Abs(x - 3) + (vertexOnly ? Math.Abs(row - 2) : 0)));
        return doc;
    }

    internal static MapDocument FineMissingCeiling()
    {
        var set = new MapSurfaceSet();
        Surface(set, "floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 1, 1, (_, _) => -4000);
        var fine = Frame with { CellUnitMetres = new(1, 128) };
        set.Refs.Add(new("roof", fine, MapSurfaceRole.Ceiling, MapPresencePolicy.Native, null, null, ""));
        Footprints(set, "cave", MapSpaceKind.Cave, Frame, 0, 0, 1, 1, Floor("floor"), Ceiling("roof"), Slot0("floor"));
        var water = new MapWaterSet();
        water.Media.Add(new(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(Body("lake", -34f, Rect(0, 0, 1, 1), Cover("cave", "floor")));
        return Document(set, water);
    }
}
