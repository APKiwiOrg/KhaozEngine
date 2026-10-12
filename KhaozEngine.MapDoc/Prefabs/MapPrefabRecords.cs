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
}

public sealed record MapPrefabAsset(int PayloadVersion, string Id, IReadOnlyList<MapPrefabChild> Children)
{
    public IReadOnlyList<MapPrefabReference> Roofs { get; init; } = Array.Empty<MapPrefabReference>();
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
    Vector3 Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows);
public sealed record MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position,
    float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags);
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
    public MapBuildingIntent Clone() => MapPrefabJson.Copy(this);
    public string Digest() => MapPrefabJson.Digest("kemap/building-intent/1", this);
}

/// <summary>Finite codec ceilings. Effective per-instance expansion charges are a separate preflight.</summary>
public sealed record MapPrefabExpansionLimits(int MaxInstances = 65536, int MaxChildren = 1048576,
    int MaxReferenceEdges = 262144, int MaxPatches = 1048576, int MaxRecords = 4194304,
    int MaxGeometryElements = 16777216, int MaxBindings = 2097152, int MaxOverrides = 1048576,
    int MaxTransfers = 1048576, long MaxPayloadBytes = 268435456);
