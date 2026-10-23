using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static class PrefabTiledSnapshotFixtures
{
    internal static PrefabFixture Create(int patches)
    {
        var large = PrefabFixtures.Create("roomless");
        for (int i = 0; i < patches; i++)
        {
            string id = "empty-" + i;
            var patch = PrefabFixtures.Patch(id, 1, 1, 0);
            patch.Presence[0] = 0;
            var surface = PrefabFixtures.Surface(id, MapSurfaceRole.SupportFloor);
            large.Document.Surfaces.Patches.Add(patch.Key, patch);
            large.Document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                    [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
            });
        }
        return large;
    }
}
