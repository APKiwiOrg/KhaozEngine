using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

public sealed class MapPrefabSet(int PayloadVersion, int ExpansionVersion,
    IReadOnlyList<MapPrefabInstance> Instances, IReadOnlyList<MapPrefabBindingTransfer> Transfers)
{
    public int PayloadVersion { get; init; } = PayloadVersion;
    public int ExpansionVersion { get; init; } = ExpansionVersion;
    public IReadOnlyList<MapPrefabInstance> Instances { get; init; } = Instances;
    public IReadOnlyList<MapPrefabBindingTransfer> Transfers { get; init; } = Transfers;
    public IReadOnlyList<MapPrefabAttachmentTransfer> Attachments { get; init; } = Array.Empty<MapPrefabAttachmentTransfer>();
    public MapPrefabSet Clone() => MapPrefabJson.Copy(this);
    public string Digest() => MapPrefabJson.Digest("kemap/prefab-set/1", this);
}

public sealed record MapPrefabInstance(string Id, string ResourceId, string ResourceSha256, MapTransform Transform,
    MapRecordRef? ParentSpace, MapPrefabBindings Bindings, IReadOnlyList<MapPrefabOverride> Overrides)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Removed { get; init; }
    /// <summary>Distinguishes a complete transferred-child inventory from an ordinary deleted instance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unpacked { get; init; }
}

public sealed record MapPrefabAsset(int PayloadVersion, string Id, IReadOnlyList<MapPrefabChild> Children)
{
    public IReadOnlyList<MapPrefabReference> Roofs { get; init; } = Array.Empty<MapPrefabReference>();
    /// <summary>Asset-local attachment dispositions. Each row suppresses one implicit (placement, kind, resource)
    /// edge and names captured explicit children. Omitted when empty.</summary>
    public IReadOnlyList<MapPrefabLocalMaterialization> MaterializedAttachments { get; init; } = Array.Empty<MapPrefabLocalMaterialization>();
}

public enum MapPrefabChildKind { Placement, Surface, Record, Storey, WaterMedium, WaterBody, Light, Marker, RoofLink, RoofGroup }
public readonly record struct MapPrefabChildKey(MapPrefabChildKind Kind, string Key);
public enum MapPrefabReferenceScope { Local, World }
public sealed record MapPrefabReference(MapPrefabReferenceScope Scope, MapPrefabChildKind Kind, string Id);
public sealed record MapPrefabReferenceSlot(string Path, MapPrefabReference Target);

public sealed record MapPrefabBinding(MapPrefabChildKey Child, string WorldId,
    [property: JsonConverter(typeof(MapNullableNumericIdJsonConverter))] long? NumericId, bool Removed);
public sealed record MapPrefabBindings(IReadOnlyList<MapPrefabBinding> Entries);
public sealed record MapPrefabBindingTransfer(string InstanceId, MapPrefabBinding Binding)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Retired { get; init; }
}

public sealed record MapLocalLightDoc(int PayloadVersion, string Id, MapPrefabReference? Owner,
    Vector3 Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows)
{
    /// <summary>Absent retains the native owner-bounds exclusion. An explicit None disables that fallback.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapLightShadowExclusion? ShadowExclusion { get; init; }
}
/// <summary>An authored local marker. Payload 1 and payload-2 Explicit carry the authored height in Position.Y.
/// Payload-2 Support carries Position.Y exactly +0 with no height meaning. Its resolved height is on
/// MapEffectiveMarker.Position. The payload-2 members are omitted when null, so payload-1 bytes are unchanged.</summary>
public sealed record MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position,
    float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags)
{
    /// <summary>Required by payload 2 and absent from payload 1.</summary>
    public Markers.MapMarkerHeightMode? HeightMode { get; init; }
    /// <summary>Required by a Support height and absent otherwise. Surface names only a surface, Space names a
    /// space, reference Y and both search extents.</summary>
    public MapSupportBinding? SupportBinding { get; init; }
    /// <summary>An optional nonempty payload-2 archetype.</summary>
    public string? ArchetypeId { get; init; }
}
public sealed record MapRoofLinkDoc(string Id, MapPrefabReference Roof, IReadOnlyList<MapPrefabReference> Rooms,
    IReadOnlyList<MapPrefabReference> Groups, float AboveY);
public sealed record MapRoofGroupDoc(string Id, IReadOnlyList<MapPrefabReference> Rooms);

public sealed class MapBuildingIntent(int PayloadVersion, IReadOnlyList<MapRoofLinkDoc> RoofLinks,
    IReadOnlyList<MapRoofGroupDoc> RoofGroups, IReadOnlyList<MapLocalLightDoc> Lights, IReadOnlyList<MapLocalMarkerDoc> Markers)
{
    public int PayloadVersion { get; init; } = PayloadVersion;
    public IReadOnlyList<MapRoofLinkDoc> RoofLinks { get; init; } = RoofLinks;
    public IReadOnlyList<MapRoofGroupDoc> RoofGroups { get; init; } = RoofGroups;
    public IReadOnlyList<MapLocalLightDoc> Lights { get; init; } = Lights;
    public IReadOnlyList<MapLocalMarkerDoc> Markers { get; init; } = Markers;
    public IReadOnlyList<MapPrefabReference> Roofs { get; init; } = Array.Empty<MapPrefabReference>();
    public IReadOnlyList<MapStoreyDoc> Storeys { get; init; } = Array.Empty<MapStoreyDoc>();
    public MapBuildingIntent Clone() => MapBuildingIntentPreparation.Clone(this);
    public string Digest() => MapPrefabJson.Digest("kemap/building-intent/1", this);
}

/// <summary>Finite codec ceilings. Effective per-instance expansion charges are a separate preflight.</summary>
/// <param name="MaxInstances">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxChildren">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxReferenceEdges">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPatches">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxRecords">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxGeometryElements">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxBindings">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxOverrides">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxTransfers">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPayloadBytes">Demand sites: MapPrefabPreflight. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapPrefabExpansionLimits(int MaxInstances = 65536, int MaxChildren = 1048576,
    int MaxReferenceEdges = 262144, int MaxPatches = 1048576, int MaxRecords = 4194304,
    int MaxGeometryElements = 16777216, int MaxBindings = 2097152, int MaxOverrides = 1048576,
    int MaxTransfers = 1048576, long MaxPayloadBytes = 268435456);
