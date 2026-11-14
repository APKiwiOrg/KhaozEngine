using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The closed member shapes of prefab children and overrides. The child and override converters check, read
/// and construct through these tables, and <see cref="MapPrefabMissingMembers"/> walks the same tables. A member the
/// converter reads is therefore the member the walk checks, read as the same type, so the two cannot disagree.</summary>
internal static class MapPrefabPayloadShapes
{
    /// <summary>A named member and the type the codec reads it as.</summary>
    internal abstract class Member(string name)
    {
        internal string Name { get; } = name;
        internal abstract Type Type { get; }
    }

    /// <summary>A member read through the prefab codec as <typeparamref name="T"/>.</summary>
    internal sealed class Member<T>(string name) : Member(name)
    {
        internal override Type Type => typeof(T);
        internal T Read(JsonElement root) => MapPrefabJson.Read<T>(root.GetProperty(Name));
        // An absent or null optional member reads as the default, as the released converter did.
        internal T? Optional(JsonElement root) =>
            root.TryGetProperty(Name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? MapPrefabJson.Read<T>(value) : default;
    }

    /// <summary>A child type's payload members and how the converter builds the child from them.</summary>
    internal sealed record ChildShape(Member[] Payload, Func<MapPrefabChildKey, JsonElement, MapPrefabChild> Create);

    /// <summary>An override type's own members beside <c>type</c> and <c>child</c>, and how the converter builds it.</summary>
    internal sealed record OverrideShape(Member[] Required, Member[] Optional, Func<MapPrefabChildKey, JsonElement, MapPrefabOverride>? Create)
    {
        internal string[] RequiredNames { get; } = [TypeName.Name, OverrideChild.Name, .. Required.Select(m => m.Name)];
        internal string[] OptionalNames { get; } = Optional.Select(m => m.Name).ToArray();
        internal IEnumerable<Member> Admitted => new Member[] { OverrideChild }.Concat(Required).Concat(Optional);
    }

    internal static readonly Member<string> TypeName = new("type");
    internal static readonly Member<MapPrefabChildKey> Key = new("key");
    internal static readonly Member<MapTransform> LocalTransform = new("localTransform");
    internal static readonly Member<MapPrefabReferenceSlot[]> References = new("references");
    internal static readonly Member[] ChildOptional = [LocalTransform, References];
    internal static readonly string[] ChildOptionalNames = ChildOptional.Select(m => m.Name).ToArray();
    /// <summary>The members a placement child's value must carry. Its DTO admits more, so the converter checks these.</summary>
    internal static readonly string[] PlacementRequired = ["id", "kind", "assetId", "x", "z", "yaw", "scale", "tags"];
    // An unsupported child type still requires a value, then is refused by kind.
    internal static readonly Member[] UnknownChildPayload = [new Member<object>("value")];

    static readonly Member<MapPlacement> PlacementValue = new("value");
    static readonly Member<MapSurfaceRef> Surface = new("surface");
    static readonly Member<MapSurfacePatch[]> Patches = new("patches");
    static readonly Member<MapTopologyRecord> RecordValue = new("value");
    static readonly Member<MapPatchKey> Anchor = new("anchor");
    static readonly Member<string> StoreyName = new("name");
    static readonly Member<MapWaterMediumDoc> MediumValue = new("value");
    static readonly Member<MapWaterBodyDoc> WaterValue = new("value");
    static readonly Member<MapLocalLightDoc> LightValue = new("value");
    static readonly Member<MapLocalMarkerDoc> MarkerValue = new("value");
    static readonly Member<MapRoofLinkDoc> RoofValue = new("value");
    static readonly Member<MapRoofGroupDoc> RoofGroupValue = new("value");

    internal static readonly IReadOnlyDictionary<string, ChildShape> Children = new Dictionary<string, ChildShape>(StringComparer.Ordinal)
    {
        ["Placement"] = new([PlacementValue], (key, root) => new MapPrefabPlacementChild(key, PlacementValue.Read(root))),
        ["Surface"] = new([Surface, Patches], (key, root) => new MapPrefabSurfaceChild(key, Surface.Read(root), Patches.Read(root))),
        ["Record"] = new([RecordValue, Anchor], (key, root) => new MapPrefabRecordChild(key, RecordValue.Read(root), Anchor.Read(root))),
        ["Storey"] = new([StoreyName], (key, root) => new MapPrefabStoreyChild(key, root.GetProperty(StoreyName.Name).GetString()!)),
        ["WaterMedium"] = new([MediumValue], (key, root) => new MapPrefabMediumChild(key, MediumValue.Read(root))),
        ["WaterBody"] = new([WaterValue], (key, root) => new MapPrefabWaterChild(key, WaterValue.Read(root))),
        ["Light"] = new([LightValue], (key, root) => new MapPrefabLightChild(key, LightValue.Read(root))),
        ["Marker"] = new([MarkerValue], (key, root) => new MapPrefabMarkerChild(key, MarkerValue.Read(root))),
        ["RoofLink"] = new([RoofValue], (key, root) => new MapPrefabRoofChild(key, RoofValue.Read(root))),
        ["RoofGroup"] = new([RoofGroupValue], (key, root) => new MapPrefabRoofGroupChild(key, RoofGroupValue.Read(root))),
    };

    internal static readonly Member<MapPrefabChildKey> OverrideChild = new("child");
    static readonly Member<MapPrefabChild> OverrideValue = new("value");
    static readonly Member<MapTransform> OverrideTransform = new("transform");
    static readonly Member<MapPrefabOverride> Previous = new("previous");
    static readonly Member<MapTransform?> PlacementTransform = new("transform");
    static readonly Member<string> AssetId = new("assetId");
    static readonly Member<string[]> Tags = new("tags");

    internal static readonly IReadOnlyDictionary<string, OverrideShape> Overrides = new Dictionary<string, OverrideShape>(StringComparer.Ordinal)
    {
        ["Remove"] = new([], [Previous], (child, root) => new MapPrefabRemoveOverride(child, Previous.Optional(root))),
        ["Replace"] = new([OverrideValue], [], (child, root) => new MapPrefabReplaceOverride(child, OverrideValue.Read(root))),
        ["Add"] = new([OverrideValue], [], (child, root) => new MapPrefabAddOverride(child, OverrideValue.Read(root))),
        ["Transform"] = new([OverrideTransform], [], (child, root) => new MapPrefabTransformOverride(child, OverrideTransform.Read(root))),
        ["Placement"] = new([], [PlacementTransform, AssetId, Tags], (child, root) =>
            new MapPrefabPlacementOverride(child, PlacementTransform.Optional(root), AssetId.Optional(root), Tags.Optional(root))),
    };
    // An unsupported override type requires its type and child, then is refused by kind.
    internal static readonly OverrideShape UnknownOverride = new([], [], null);

    internal static Member[] ChildPayload(string type) => Children.TryGetValue(type, out ChildShape? shape) ? shape.Payload : UnknownChildPayload;

    /// <summary>The member names a child of this type requires, in the order the converter checks them.</summary>
    internal static string[] ChildRequired(string type) => [TypeName.Name, Key.Name, .. ChildPayload(type).Select(m => m.Name)];

    internal static OverrideShape Override(string type) => Overrides.GetValueOrDefault(type, UnknownOverride);
}
