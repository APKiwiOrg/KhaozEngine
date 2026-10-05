using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>Versioned, render-free asset declarations and their complete resource graph.</summary>
public sealed class MapAssetManifestDoc
{
    [JsonRequired] public int PayloadVersion { get; set; } = 1;
    [JsonRequired] public List<MapAssetDoc> Assets { get; set; } = new();
    [JsonRequired] public List<MapResourceDoc> Resources { get; set; } = new();
}

/// <summary>A digest-bearing resource and its ordered dependencies. Payload version 1 is supported.</summary>
public sealed class MapResourceDoc
{
    [JsonRequired] public MapAssetRef Reference { get; set; } = null!;
    [JsonRequired] public MapResourceKind Kind { get; set; }
    [JsonRequired] public List<string> Dependencies { get; set; } = new();
}

/// <summary>Authoring metadata. Resource IDs refer to declarations in the complete manifest closure.</summary>
public sealed class MapAssetDoc
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string MeshResourceId { get; set; } = "";
    public string? CollisionResourceId { get; set; }
    public string? SelectionResourceId { get; set; }
    [JsonRequired] public IReadOnlyList<string> SupportResourceIds { get; set; } = Array.Empty<string>();
    [JsonRequired] public IReadOnlyList<string> MaterialResourceIds { get; set; } = Array.Empty<string>();
    [JsonRequired] public IReadOnlyList<string> LodResourceIds { get; set; } = Array.Empty<string>();
    [JsonRequired] public IReadOnlyList<string> LightResourceIds { get; set; } = Array.Empty<string>();
    [JsonRequired] public float SourceUnitsToMetres { get; set; }
    [JsonRequired] public MapLocalBounds RenderBounds { get; set; }
    public MapLocalBounds? LodBounds { get; set; }
    public MapLocalBounds? LightBounds { get; set; }
    [JsonRequired] public string Source { get; set; } = "";
    [JsonRequired] public string License { get; set; } = "";
    public string? Category { get; set; }
    [JsonRequired] public bool Textured { get; set; }
}

/// <summary>Finite local-space bounds, before source-unit and placement transforms.</summary>
public readonly record struct MapLocalBounds(
    [property: JsonRequired] Vector3 Min,
    [property: JsonRequired] Vector3 Max);

/// <summary>Closed resource roles. Prefab payloads are reserved and refused until supported.</summary>
public enum MapResourceKind
{
    Manifest,
    Mesh,
    Material,
    Collider,
    Selection,
    Surface,
    Light,
    Lod,
    Prefab
}
