using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

public abstract record MapPrefabChild(MapPrefabChildKey Key)
{
    public MapTransform LocalTransform { get; init; } = new(Vector3.Zero, 0, 1);
    public IReadOnlyList<MapPrefabReferenceSlot> References { get; init; } = Array.Empty<MapPrefabReferenceSlot>();
}
public sealed record MapPrefabPlacementChild(MapPrefabChildKey Key, MapPlacement Value) : MapPrefabChild(Key);
public sealed record MapPrefabSurfaceChild(MapPrefabChildKey Key, MapSurfaceRef Surface,
    IReadOnlyList<MapSurfacePatch> Patches) : MapPrefabChild(Key);
public sealed record MapPrefabRecordChild(MapPrefabChildKey Key, MapTopologyRecord Value, MapPatchKey Anchor) : MapPrefabChild(Key);
public sealed record MapPrefabStoreyChild(MapPrefabChildKey Key, string Name) : MapPrefabChild(Key);
public sealed record MapPrefabMediumChild(MapPrefabChildKey Key, MapWaterMediumDoc Value) : MapPrefabChild(Key);
public sealed record MapPrefabWaterChild(MapPrefabChildKey Key, MapWaterBodyDoc Value) : MapPrefabChild(Key);
public sealed record MapPrefabLightChild(MapPrefabChildKey Key, MapLocalLightDoc Value) : MapPrefabChild(Key);
public sealed record MapPrefabMarkerChild(MapPrefabChildKey Key, MapLocalMarkerDoc Value) : MapPrefabChild(Key);
public sealed record MapPrefabRoofChild(MapPrefabChildKey Key, MapRoofLinkDoc Value) : MapPrefabChild(Key);
public sealed record MapPrefabRoofGroupChild(MapPrefabChildKey Key, MapRoofGroupDoc Value) : MapPrefabChild(Key);
