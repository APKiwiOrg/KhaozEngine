using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Invented marker fixtures sized against the default query profile. Nothing here reads a committed file.</summary>
internal static partial class MarkerFixtures
{
    /// <summary>Stacked with nine flat full floor-0 patches east of the caves, at slots 1 to 9, which one open exterior
    /// space covers with one whole-lattice footprint each. Every footprint is anchored on the cave patch, so a support scope over the caves owns all
    /// nine and inspects 36,864 bound slots, more than the 32,769 metadata checks the default 32 page reads allow and
    /// fewer than 64 page reads allow. The far patches stay outside every marker scope, so discovery keeps its 32 page
    /// reads. An empty prefab section routes every resolver-2 entry point through effective expansion.</summary>
    internal static MarkerFixture WideBounds()
    {
        MarkerFixture stacked = Stacked();
        MapDocument document = stacked.Document;
        MapSurfacePatch anchor = document.Surfaces.Patches[new("floor-0", 0, 0)];
        int[] cells = Enumerable.Range(0, MapPatchKey.SlotCells * MapPatchKey.SlotCells).ToArray();
        anchor.Records.Add(new MapSpaceDoc("wide", MapSpaceKind.Exterior, null, null, [], [], [], []));
        for (int i = 1; i <= 9; i++)
        {
            var key = new MapPatchKey("floor-0", i, 0);
            document.Surfaces.Patches.Add(key, MixedResolutionFixtures.Patch(key, MapPatchKey.SlotCells, MapPatchKey.SlotCells, (_, _) => 0));
            anchor.Records.Add(new MapSpaceFootprint("wide-" + i + "-cells", new("wide", anchor.Key), key, cells,
                new(MapBoundKind.SupportFloor, "floor-0", null), new(MapBoundKind.OpenTop, null, null)));
        }
        document.Bounds = new() { MinX = -64, MinZ = -64, MaxX = 640, MaxZ = 64 };
        document.PlayableBounds = new() { MinX = -64, MinZ = -64, MaxX = 640, MaxZ = 64 };
        document.Prefabs = new(1, 1, [], []);
        return stacked;
    }
}
