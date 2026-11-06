using System;
using System.Collections;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    internal sealed class RepairingVertices(bool failRead = false) : IReadOnlyList<MapLatticeAddress>
    {
        readonly MapLatticeAddress[] valid = [MapLatticeAddress.Corner(14, 0), MapLatticeAddress.Corner(15, 0), MapLatticeAddress.Corner(14, 1)];
        bool refused;

        public int Count => valid.Length;
        public MapLatticeAddress this[int index]
        {
            get
            {
                if (index != 0 || refused) return valid[index];
                refused = true;
                if (failRead) throw new MapDocumentException(MapDocumentRefusal.AcquisitionIncomplete, "vertex read refused")
                { FieldPath = "Read" };
                return default;
            }
        }
        public IEnumerator<MapLatticeAddress> GetEnumerator() => ((IEnumerable<MapLatticeAddress>)valid).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
