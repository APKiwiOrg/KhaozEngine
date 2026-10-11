using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    internal static MapTerrainEdit LowerSeabedAt(float x, float z, float metres)
    {
        long cellX = (long)Math.Floor(x), cellZ = (long)Math.Floor(-z);
        MapPatchKey key = MapPatchKey.ForCell("seabed", cellX, cellZ);
        int cornerX = checked((int)(cellX - key.SlotX * MapPatchKey.SlotCells));
        int cornerZ = checked((int)(cellZ - key.SlotZ * MapPatchKey.SlotCells));
        int delta = checked((int)Math.Round(metres * 100));
        int left = SeabedHeight(cellX) - delta, right = SeabedHeight(cellX + 1) - delta;
        return new MapSetCornerHeights(key, cornerX, cornerZ, 2, 2, new[] { left, right, left, right });
    }

    internal static MapWaterBodyDoc SecondLakeOverlappingCaveLake() =>
        Broken("overlapping-bodies").Water!.Bodies.Single(b => b.Id == "second-lake");

    internal static MapWaterEditResult ApplyEdit(string variant)
    {
        MapDocument doc = OceanOverCaves();
        MapWaterEdit edit;
        switch (variant)
        {
            case "remove-used-medium": edit = new MapRemoveWaterMedium("sea-water"); break;
            case "legacy-with-bodies": edit = new MapSetBoundedWater(false); break;
            case "remove-unknown-body": edit = new MapRemoveWaterBody("nowhere"); break;
            case "upsert-without-section":
                edit = new MapUpsertWaterBody(doc.Water!.Bodies[0]);
                doc.Water = null;
                break;
            case "bounded-on-resolver-one":
                doc.Water = null;
                doc.ResolverIdentity = new(1, 1);
                edit = new MapSetBoundedWater(true);
                break;
            default: throw new ArgumentException("unknown water edit fixture", nameof(variant));
        }
        return MapWaterEdits.Prepare(doc, edit);
    }
}
