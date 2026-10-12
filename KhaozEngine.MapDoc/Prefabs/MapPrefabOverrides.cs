using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Prefabs;

public abstract record MapPrefabOverride(MapPrefabChildKey Child);
public sealed record MapPrefabRemoveOverride(MapPrefabChildKey Child, MapPrefabOverride? Previous = null) : MapPrefabOverride(Child);
public sealed record MapPrefabReplaceOverride(MapPrefabChildKey Child, MapPrefabChild Value) : MapPrefabOverride(Child);
public sealed record MapPrefabPlacementOverride(MapPrefabChildKey Child, MapTransform? Transform,
    string? AssetId, IReadOnlyList<string>? Tags) : MapPrefabOverride(Child);
public sealed record MapPrefabTransformOverride(MapPrefabChildKey Child, MapTransform Transform) : MapPrefabOverride(Child);
public sealed record MapPrefabAddOverride(MapPrefabChildKey Child, MapPrefabChild Value) : MapPrefabOverride(Child);
