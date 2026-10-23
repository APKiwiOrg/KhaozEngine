using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Scene;

internal static class MapSceneQueryFixtures
{
    internal static void AddNativeChain(MapDocument document)
    {
        document.Surfaces.Refs.Clear();
        document.Surfaces.Patches.Clear();
        var surface = new MapSurfaceRef("chain", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        for (int i = 0; i < 40; i++)
        {
            var patch = new MapSurfacePatch
            {
                Key = new("chain", i, 0),
                Width = 1,
                Depth = 1,
                Heights = new int[4],
                Cells = new MapSurfaceCell[1],
                Presence = [1UL],
            };
            patch.Records.Add(new MapSpaceDoc("space-" + i, MapSpaceKind.Cave,
                i == 39 ? null : new("space-" + (i + 1), new("chain", i + 1, 0)), null, [], [], [], []));
            document.Surfaces.Patches.Add(patch.Key, patch);
        }
        document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, document.Surfaces.Patches.Select(p =>
                KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
        });
    }
}
