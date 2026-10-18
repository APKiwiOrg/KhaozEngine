using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Markers;

internal static partial class MarkerFixtures
{
    internal static MarkerFixture LegacyLists()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("adoption-floor", 1, MapSurfaceRole.SupportFloor));
        foreach (int x in new[] { -1, 0 })
            foreach (int z in new[] { -1, 0 })
            {
                var patch = MixedResolutionFixtures.Patch(new("adoption-floor", x, z), 2, 2, (_, _) => 2000);
                patch.CellMinX = x < 0 ? 62 : 0;
                patch.CellMinZ = z < 0 ? 62 : 0;
                set.Patches.Add(patch.Key, patch);
            }
        MapDocument document = MixedResolutionFixtures.Document(set);
        var assets = Create().Assets;
        document.NativeAssets = assets.Roots.ToList();
        document.Spawns.Add(new() { Id = "old", ArchetypeId = "kit.actor", X = -0.25f, Z = -1.5f, Enabled = false, Tags = ["second", "first"] });
        document.PlayerSpawns.Add(new() { Id = "old", X = -0.25f, Z = -1.5f, Yaw = 0.371f, Tags = ["second", "first"] });
        return new(document, assets);
    }

    internal static IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> Choices() =>
        new Dictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>
        {
            [new(MapLegacySpawnKind.Spawn, "old")] = new("actor-new", "actor-home", "actor-role", new(12.25f, null)),
            [new(MapLegacySpawnKind.PlayerSpawn, "old")] = new("start-new", "player-home", "start-role", new(null, SurfaceBinding("adoption-floor"))),
        };
}
