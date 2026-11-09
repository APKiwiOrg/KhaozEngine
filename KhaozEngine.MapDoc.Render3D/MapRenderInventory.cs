using System.Collections.Generic;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapRenderInventory(MapSceneWitness Witness, MapTerrainRenderBatch Terrain,
    MapPlacementRenderBatch Placements, IReadOnlyList<MapEffectiveLight> Lights, MapWaterRenderBatch Water,
    MapNativeFoliageBatch Foliage, IReadOnlyList<MapEffectiveMarker> Markers);
