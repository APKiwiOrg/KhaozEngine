namespace KhaozEngine.Tests.MapDoc;

internal static class NativeFixtures
{
    internal static string AnalyticV3Json() => """
        {
          "formatVersion": 3,
          "id": "legacy",
          "bounds": { "minX": -100, "minZ": -100, "maxX": 100, "maxZ": 100 },
          "tileSize": 64,
          "terrain": {
            "seed": 7, "waterLevel": -0.5, "gentleAmplitude": 0,
            "biomes": [{ "biome": "Meadow", "baseHeight": 1.5, "hillAmplitude": 0 }]
          },
          "placements": [{
            "id": "old-inn", "kind": "building_inn", "x": -30, "z": 20, "y": 1.5,
            "yaw": 0.371, "scale": 1.137, "tags": ["first", "second"]
          }]
        }
        """;
}
