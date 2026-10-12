using System.Collections.Generic;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

public enum MapPrefabAttachmentKind { Support, Light }
public sealed record MapPrefabAttachedChild(MapPrefabChildKey LocalChild, string WorldId, MapPatchKey? Anchor,
    MapEffectiveTransform OwnerLocalPose, string SourceDigest);
public sealed record MapPrefabAttachmentTransfer(string PlacementId, string ResourceId, string ResourceSha256,
    MapPrefabAttachmentKind Kind, IReadOnlyList<MapPrefabAttachedChild> Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Retired);
