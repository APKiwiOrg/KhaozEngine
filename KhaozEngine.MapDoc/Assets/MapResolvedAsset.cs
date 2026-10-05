using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>An immutable asset descriptor retaining all authoring metadata and ordered resource IDs.</summary>
public sealed class MapResolvedAsset
{
    public string Id { get; }
    public string MeshResourceId { get; }
    public string? CollisionResourceId { get; }
    public string? SelectionResourceId { get; }
    public IReadOnlyList<string> SupportResourceIds { get; }
    public IReadOnlyList<string> MaterialResourceIds { get; }
    public IReadOnlyList<string> LodResourceIds { get; }
    public IReadOnlyList<string> LightResourceIds { get; }
    public float SourceUnitsToMetres { get; }
    public MapLocalBounds RenderBounds { get; }
    public MapLocalBounds? LodBounds { get; }
    public MapLocalBounds? LightBounds { get; }
    public string Source { get; }
    public string License { get; }
    public string? Category { get; }
    public bool Textured { get; }

    internal MapResolvedAsset(MapAssetDoc doc)
    {
        Id = doc.Id;
        MeshResourceId = doc.MeshResourceId;
        CollisionResourceId = doc.CollisionResourceId;
        SelectionResourceId = doc.SelectionResourceId;
        SupportResourceIds = Array.AsReadOnly(doc.SupportResourceIds.ToArray());
        MaterialResourceIds = Array.AsReadOnly(doc.MaterialResourceIds.ToArray());
        LodResourceIds = Array.AsReadOnly(doc.LodResourceIds.ToArray());
        LightResourceIds = Array.AsReadOnly(doc.LightResourceIds.ToArray());
        SourceUnitsToMetres = doc.SourceUnitsToMetres;
        RenderBounds = doc.RenderBounds;
        LodBounds = doc.LodBounds;
        LightBounds = doc.LightBounds;
        Source = doc.Source;
        License = doc.License;
        Category = doc.Category;
        Textured = doc.Textured;
    }
}

/// <summary>A resource snapshot. Bytes returns an independent copy, including when extracted with MemoryMarshal.</summary>
public sealed class MapResolvedResource
{
    readonly byte[] _bytes;
    public MapAssetRef Reference { get; }
    public MapResourceKind Kind { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();

    public MapResolvedResource(MapAssetRef reference, MapResourceKind kind, IReadOnlyList<string> dependencies, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(dependencies);
        Reference = reference;
        Kind = kind;
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        _bytes = bytes.ToArray();
    }
}
