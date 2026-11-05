using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Support;

namespace KhaozEngine.MapDoc;

/// <summary>Thrown when a map document fails to load, validate, migrate, or save. Map documents are
/// dev-authored content, so failures are loud (a server refuses to boot on a bad document) rather than
/// quarantined like runtime player-state blobs.</summary>
public sealed class MapDocumentException : Exception
{
    IReadOnlyList<MapDocumentFinding>? _findings;
    IReadOnlyList<int>? _supportedVersions;
    IReadOnlyList<string>? _resourceIds;
    IReadOnlyList<string>? _sourceIds;
    IReadOnlyList<string>? _markerIds;

    public MapDocumentException(string message) : base(message) { }
    public MapDocumentException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates a refusal with its stable reason and developer-facing message.</summary>
    public MapDocumentException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a refusal retaining its original cause and stable reason.</summary>
    public MapDocumentException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The stable refusal reason. Null only for an instance built through a message-only constructor
    /// by consumer code outside the engine.</summary>
    public MapDocumentRefusal? Reason { get; }

    /// <summary>The source file or content name, when known.</summary>
    public string? SourceName { get; init; }
    /// <summary>The source file path, when known separately from the source name.</summary>
    public string? Path { get; init; }
    /// <summary>The resource being decoded, when known.</summary>
    public string? ResourceId { get; init; }
    /// <summary>The refused patch width, when checked.</summary>
    public int? Width { get; init; }
    /// <summary>The refused patch depth, when checked.</summary>
    public int? Depth { get; init; }
    /// <summary>The numerator of the refused authored lattice unit.</summary>
    public int? Numerator { get; init; }
    /// <summary>The denominator of the refused authored lattice unit.</summary>
    public int? Denominator { get; init; }
    /// <summary>The submitted patch-local cell or corner X coordinate.</summary>
    public int? CellX { get; init; }
    /// <summary>The submitted patch-local cell or corner Z coordinate.</summary>
    public int? CellZ { get; init; }
    /// <summary>The document tile being read, when known.</summary>
    public MapTileCoord? Tile { get; init; }
    /// <summary>The zero-based JSON line number exposed by the reader, when available.</summary>
    public long? LineNumber { get; init; }
    /// <summary>The zero-based UTF-8 byte position within the JSON line, when exposed by the reader.</summary>
    public long? BytePositionInLine { get; init; }
    /// <summary>The resolver identity's declared payload version, when present.</summary>
    public int? PayloadVersion { get; init; }
    /// <summary>The document's declared resolver version, when present.</summary>
    public int? ResolverVersion { get; init; }
    /// <summary>The resolver version supplied in the caller's options, when checked.</summary>
    public int? OptionsResolverVersion { get; init; }
    /// <summary>The resolver version required by this operation, when one version is required.</summary>
    public int? ExpectedResolverVersion { get; init; }
    /// <summary>The document's declared support recipe, when known.</summary>
    public MapSupportRecipe? SupportRecipe { get; init; }
    /// <summary>The support recipe required by this operation, when one recipe is required.</summary>
    public MapSupportRecipe? ExpectedSupportRecipe { get; init; }
    /// <summary>The document or options field whose contract was refused. JSON fields use
    /// <see cref="System.Text.Json.JsonException.Path"/> format: <c>$</c> for the root, <c>.name</c> for
    /// a plain member, <c>['name']</c> for a member containing special characters, and <c>[index]</c>
    /// for an array element.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The canonical JSON pointer of a prefab reference occurrence or slot, when known.</summary>
    public string? ReferencePath { get; init; }
    /// <summary>The kind of document content or identity whose rule was refused.</summary>
    public string? SubjectKind { get; init; }
    /// <summary>The API operation whose entry-point contract was refused.</summary>
    public string? Operation { get; init; }

    /// <summary>The foliage, scatter or companion layer identity, when known.</summary>
    public string? LayerId { get; init; }
    /// <summary>The zero-based position of the containing or refused JSON array item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The foliage chunk coordinates, after both coordinates have been read.</summary>
    public MapFoliageChunkKey? FoliageChunk { get; init; }
    /// <summary>The measured or decoded byte count, when the whole value can be measured.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The required decoded byte count, when checked.</summary>
    public long? ExpectedByteCount { get; init; }
    /// <summary>The work or metadata budget whose checked capacity was exceeded.</summary>
    public string? Budget { get; init; }
    /// <summary>The inclusive byte, work or whole-metre world coordinate limit, when checked.</summary>
    public long? Limit { get; init; }
    /// <summary>The refused payload version, when read as an integer.</summary>
    public int? ActualVersion { get; init; }
    /// <summary>The refused numeric value, when representable as a double.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The refused numeric value, when representable as a signed 64-bit integer.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum integer accepted by the checked field.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum integer accepted by the checked field.</summary>
    public long? MaximumInteger { get; init; }
    /// <summary>The refused identity, when known.</summary>
    public string? EntityId { get; init; }
    /// <summary>The static owner whose world position was checked, such as a placement or terrain chunk.</summary>
    public string? OwnerId { get; init; }
    /// <summary>The checked absolute world X coordinate in metres, before any frame projection.</summary>
    public double? PositionX { get; init; }
    /// <summary>The checked absolute world Y coordinate in metres, before any frame projection.</summary>
    public double? PositionY { get; init; }
    /// <summary>The checked absolute world Z coordinate in metres, before any frame projection.</summary>
    public double? PositionZ { get; init; }

    /// <summary>The asset descriptor whose admission failed, when known.</summary>
    public string? AssetId { get; init; }
    /// <summary>The declared surface whose metadata failed validation, when known.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The legacy spawn source whose adoption choice was refused, when known.</summary>
    public string? SourceId { get; init; }
    /// <summary>The repeated effective or destination marker name, when checked.</summary>
    public string? MarkerName { get; init; }
    /// <summary>The marker whose authoring or identity rule was refused, when known.</summary>
    public string? MarkerId { get; init; }
    /// <summary>The placement whose admission failed, when known.</summary>
    public string? PlacementId { get; init; }
    /// <summary>The placement supplying support whose explicit height is missing.</summary>
    public string? ProviderPlacementId { get; init; }
    /// <summary>The support query status that prevented marker or placement seating.</summary>
    public MapSupportStatus? SupportStatus { get; init; }
    /// <summary>The referenced resource kind required by the asset.</summary>
    public MapResourceKind? ExpectedResourceKind { get; init; }
    /// <summary>The declared resource kind, when a resource is available.</summary>
    public MapResourceKind? ActualResourceKind { get; init; }
    /// <summary>The refused numeric identity, when present.</summary>
    public long? NumericId { get; init; }
    /// <summary>The reserved numeric identity high-water mark checked by the guard.</summary>
    public long? HighWaterMark { get; init; }
    /// <summary>The minimum of the checked numeric domain, or the submitted lower endpoint when bounds are invalid.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The maximum of the checked numeric domain, or the submitted upper endpoint when bounds are invalid.</summary>
    public double? MaximumNumber { get; init; }

    /// <summary>The document containing the refused reference or edit, when known.</summary>
    public string? DocumentId { get; init; }
    /// <summary>The world identity in the namespace identified by ChildKind, when known.</summary>
    public string? WorldId { get; init; }
    /// <summary>The prefab source or submitted owner instance, when known.</summary>
    public string? InstanceId { get; init; }
    /// <summary>The typed world identity namespace checked by the guard.</summary>
    public MapPrefabChildKind? ChildKind { get; init; }
    /// <summary>The child kind required by the refused reference or binding role.</summary>
    public MapPrefabChildKind? ExpectedChildKind { get; init; }
    /// <summary>The submitted reference namespace, when the guard knows it.</summary>
    public MapPrefabReferenceScope? ReferenceScope { get; init; }
    /// <summary>The reference namespace required by the refused role.</summary>
    public MapPrefabReferenceScope? ExpectedReferenceScope { get; init; }
    /// <summary>The prefab-local child key associated with the world identity, when known.</summary>
    public MapPrefabChildKey? Child { get; init; }
    /// <summary>The local child key required by a previous, replacement or added override.</summary>
    public MapPrefabChildKey? ExpectedChild { get; init; }
    /// <summary>The authored record whose admission failed, when known.</summary>
    public string? RecordId { get; init; }
    /// <summary>The space referenced by the refused authored record, when known.</summary>
    public string? SpaceId { get; init; }
    /// <summary>The submitted or referenced authored patch, when known.</summary>
    public MapPatchKey? Patch { get; init; }
    /// <summary>The actual standalone record anchor found by the guard, when known.</summary>
    public MapPatchKey? ActualAnchor { get; init; }
    /// <summary>The captured mathematical frame submitted by a materialized row.</summary>
    public MapEffectiveTransform? ActualTransform { get; init; }
    /// <summary>The captured mathematical frame required by an earlier row for the same placement.</summary>
    public MapEffectiveTransform? ExpectedTransform { get; init; }
    /// <summary>The count measured by the guard.</summary>
    public long? ActualCount { get; init; }
    /// <summary>The exact count required by the guard.</summary>
    public long? ExpectedCount { get; init; }
    /// <summary>The transferred numeric identity required of its standalone placement, when present.</summary>
    public long? ExpectedNumericId { get; init; }
    /// <summary>The resource, descriptor or surface semantic digest required by its pin, when known.</summary>
    public string? ExpectedHash { get; init; }
    /// <summary>The compared digest, or the submitted digest when its representation is malformed.</summary>
    public string? ActualHash { get; init; }

    /// <summary>The digest domain checked by the guard, such as resource bytes.</summary>
    public string? DigestKind { get; init; }
    /// <summary>The missing resource required by the resource identified by ResourceId.</summary>
    public string? DependencyId { get; init; }
    /// <summary>The required in-band typed resource schema discriminant.</summary>
    public string? ExpectedSchema { get; init; }
    /// <summary>The map storage file or directory whose writer owns the refused path.</summary>
    public string? StoragePath { get; init; }
    /// <summary>The explicit storage form whose writer owns the refused path.</summary>
    public MapDocumentForm? DocumentForm { get; init; }

    /// <summary>The attachment namespace whose child or identity rule was refused.</summary>
    public MapPrefabAttachmentKind? AttachmentKind { get; init; }

    /// <summary>The enumeration type whose submitted numeric value is undefined.</summary>
    public Type? EnumType { get; init; }

    /// <summary>The external URI refused by a self-contained mesh resource.</summary>
    public string? Uri { get; init; }
    /// <summary>The collision shape type whose point set is empty.</summary>
    public Type? ShapeType { get; init; }
    /// <summary>The resource root used by complete document validation, when known.</summary>
    public string? ResourceRoot { get; init; }

    /// <summary>The resource identities left unresolved by a dependency cycle, in ordinal order,
    /// as an owned read-only snapshot. This includes resources blocked behind the cycle.</summary>
    public IReadOnlyList<string>? ResourceIds
    {
        get => _resourceIds;
        init => _resourceIds = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }

    /// <summary>The required legacy spawn source IDs, in Spawn then PlayerSpawn order, as an owned read-only
    /// snapshot. Equal source IDs in different lists remain separate entries.</summary>
    public IReadOnlyList<string>? SourceIds
    {
        get => _sourceIds;
        init => _sourceIds = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }

    /// <summary>The colliding marker IDs in ordinal order, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? MarkerIds
    {
        get => _markerIds;
        init => _markerIds = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }

    /// <summary>The supported payload versions, as an owned read-only snapshot.</summary>
    public IReadOnlyList<int>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : Array.AsReadOnly<int>([.. value]);
    }

    /// <summary>The complete ordered findings from a typed validator. Setting this property takes an owned,
    /// read-only snapshot, so changing the input collection or casting the exposed list cannot mutate it.</summary>
    public IReadOnlyList<MapDocumentFinding>? Findings
    {
        get => _findings;
        init => _findings = value is null ? null : Array.AsReadOnly<MapDocumentFinding>([.. value]);
    }

    /// <summary>Adds source context while preserving the reason, every identifier and this refusal as the cause.
    /// A supplied wrapper message retains the caller's released wording. Existing tile and path identifiers
    /// take precedence over additional source context. A null source name keeps the existing source,
    /// and existing resource, asset and resource-root identifiers take precedence over additional context.</summary>
    internal MapDocumentException WithSourceName(string? sourceName, string? message = null,
        MapTileCoord? tile = null, string? path = null, string? resourceId = null, string? resourceRoot = null, string? assetId = null) =>
        new(this, sourceName, message, tile, path, resourceId, resourceRoot, assetId);

    MapDocumentException(MapDocumentException inner, string? sourceName, string? message,
        MapTileCoord? tile, string? path, string? resourceId, string? resourceRoot, string? assetId) : base(message ?? $"{sourceName}: {inner.Message}", inner)
    {
        Reason = inner.Reason;
        SourceName = sourceName ?? inner.SourceName;
        Path = inner.Path ?? path;
        ResourceId = inner.ResourceId ?? resourceId;
        Width = inner.Width;
        Depth = inner.Depth;
        Numerator = inner.Numerator;
        Denominator = inner.Denominator;
        CellX = inner.CellX;
        CellZ = inner.CellZ;
        Tile = inner.Tile ?? tile;
        LineNumber = inner.LineNumber;
        BytePositionInLine = inner.BytePositionInLine;
        PayloadVersion = inner.PayloadVersion;
        ResolverVersion = inner.ResolverVersion;
        OptionsResolverVersion = inner.OptionsResolverVersion;
        ExpectedResolverVersion = inner.ExpectedResolverVersion;
        SupportRecipe = inner.SupportRecipe;
        ExpectedSupportRecipe = inner.ExpectedSupportRecipe;
        FieldPath = inner.FieldPath;
        ReferencePath = inner.ReferencePath;
        SubjectKind = inner.SubjectKind;
        Operation = inner.Operation;
        LayerId = inner.LayerId;
        Index = inner.Index;
        FoliageChunk = inner.FoliageChunk;
        ActualByteCount = inner.ActualByteCount;
        ExpectedByteCount = inner.ExpectedByteCount;
        Budget = inner.Budget;
        Limit = inner.Limit;
        ActualVersion = inner.ActualVersion;
        ActualNumber = inner.ActualNumber;
        ActualInteger = inner.ActualInteger;
        MinimumInteger = inner.MinimumInteger;
        MaximumInteger = inner.MaximumInteger;
        EntityId = inner.EntityId;
        OwnerId = inner.OwnerId;
        PositionX = inner.PositionX;
        PositionY = inner.PositionY;
        PositionZ = inner.PositionZ;
        AssetId = inner.AssetId ?? assetId;
        SurfaceId = inner.SurfaceId;
        MarkerId = inner.MarkerId;
        MarkerName = inner.MarkerName;
        SourceId = inner.SourceId;
        PlacementId = inner.PlacementId;
        ProviderPlacementId = inner.ProviderPlacementId;
        SupportStatus = inner.SupportStatus;
        ExpectedResourceKind = inner.ExpectedResourceKind;
        ActualResourceKind = inner.ActualResourceKind;
        NumericId = inner.NumericId;
        HighWaterMark = inner.HighWaterMark;
        MinimumNumber = inner.MinimumNumber;
        MaximumNumber = inner.MaximumNumber;
        DocumentId = inner.DocumentId;
        WorldId = inner.WorldId;
        InstanceId = inner.InstanceId;
        ChildKind = inner.ChildKind;
        ExpectedChildKind = inner.ExpectedChildKind;
        ReferenceScope = inner.ReferenceScope;
        ExpectedReferenceScope = inner.ExpectedReferenceScope;
        ExpectedChild = inner.ExpectedChild;
        Child = inner.Child;
        RecordId = inner.RecordId;
        SpaceId = inner.SpaceId;
        Patch = inner.Patch;
        ActualAnchor = inner.ActualAnchor;
        ActualTransform = inner.ActualTransform;
        ExpectedTransform = inner.ExpectedTransform;
        ActualCount = inner.ActualCount;
        ExpectedCount = inner.ExpectedCount;
        ExpectedNumericId = inner.ExpectedNumericId;
        ExpectedHash = inner.ExpectedHash;
        ActualHash = inner.ActualHash;
        DigestKind = inner.DigestKind;
        DependencyId = inner.DependencyId;
        ExpectedSchema = inner.ExpectedSchema;
        StoragePath = inner.StoragePath;
        DocumentForm = inner.DocumentForm;
        AttachmentKind = inner.AttachmentKind;
        EnumType = inner.EnumType;
        Uri = inner.Uri;
        ShapeType = inner.ShapeType;
        ResourceRoot = inner.ResourceRoot ?? resourceRoot;
        _resourceIds = inner._resourceIds;
        _sourceIds = inner._sourceIds;
        _markerIds = inner._markerIds;
        _supportedVersions = inner._supportedVersions;
        // Findings already owns a read-only snapshot whose elements are immutable records.
        _findings = inner._findings;
    }
}
