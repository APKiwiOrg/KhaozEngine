using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc;

    /// <summary>An immutable validation finding. Message is its prose projection, while Reason and the applicable
    /// identifiers describe the refused rule. Identifiers not known by the producing guard remain null.</summary>
    /// <param name="Reason">The rule refused by the validator.</param>
    /// <param name="Message">The developer-facing explanation, not a classification token.</param>
public sealed record MapDocumentFinding(MapDocumentRefusal Reason, string Message)
{
    IReadOnlyList<int>? _supportedVersions;
    IReadOnlyList<string>? _resourceIds;

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
    /// <summary>The document or options field whose contract was refused.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The kind of document content or identity whose rule was refused.</summary>
    public string? SubjectKind { get; init; }

    /// <summary>The foliage layer identity, after it has been read.</summary>
    public string? LayerId { get; init; }
    /// <summary>The zero-based position of the containing or refused JSON array item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The foliage chunk coordinates, after both coordinates have been read.</summary>
    public MapFoliageChunkKey? FoliageChunk { get; init; }
    /// <summary>The measured or decoded byte count, when the whole value can be measured.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The required decoded byte count, when checked.</summary>
    public long? ExpectedByteCount { get; init; }
    /// <summary>The inclusive byte or work limit, when checked.</summary>
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
    /// <summary>The refused identity, such as a repeated JSON member name, when known.</summary>
    public string? EntityId { get; init; }

    /// <summary>The asset descriptor whose admission failed, when known.</summary>
    public string? AssetId { get; init; }
    /// <summary>The declared surface whose metadata failed validation, when known.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The placement whose admission failed, when known.</summary>
    public string? PlacementId { get; init; }
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
    /// <summary>The prefab-local child key associated with the world identity, when known.</summary>
    public MapPrefabChildKey? Child { get; init; }
    /// <summary>The authored record whose admission failed, when known.</summary>
    public string? RecordId { get; init; }
    /// <summary>The space referenced by the refused authored record, when known.</summary>
    public string? SpaceId { get; init; }
    /// <summary>The submitted or referenced authored patch, when known.</summary>
    public MapPatchKey? Patch { get; init; }
    /// <summary>The actual standalone record anchor found by the guard, when known.</summary>
    public MapPatchKey? ActualAnchor { get; init; }
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

    /// <summary>The unresolved resource identities, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? ResourceIds
    {
        get => _resourceIds;
        init => _resourceIds = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }

    /// <summary>The supported payload versions, as an owned read-only snapshot.</summary>
    public IReadOnlyList<int>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : Array.AsReadOnly<int>([.. value]);
    }

    /// <summary>Projects an atomic typed refusal without inventing a reason for a legacy failure.</summary>
    internal static MapDocumentFinding? FromException(MapDocumentException error) => error.Reason is { } reason
        ? new(reason, error.Message)
        {
            SourceName = error.SourceName,
            Path = error.Path,
            ResourceId = error.ResourceId,
            Width = error.Width,
            Depth = error.Depth,
            Tile = error.Tile,
            LineNumber = error.LineNumber,
            BytePositionInLine = error.BytePositionInLine,
            PayloadVersion = error.PayloadVersion,
            ResolverVersion = error.ResolverVersion,
            OptionsResolverVersion = error.OptionsResolverVersion,
            ExpectedResolverVersion = error.ExpectedResolverVersion,
            SupportRecipe = error.SupportRecipe,
            ExpectedSupportRecipe = error.ExpectedSupportRecipe,
            FieldPath = error.FieldPath,
            SubjectKind = error.SubjectKind,
            LayerId = error.LayerId,
            Index = error.Index,
            FoliageChunk = error.FoliageChunk,
            ActualByteCount = error.ActualByteCount,
            ExpectedByteCount = error.ExpectedByteCount,
            Limit = error.Limit,
            ActualVersion = error.ActualVersion,
            ActualNumber = error.ActualNumber,
            ActualInteger = error.ActualInteger,
            MinimumInteger = error.MinimumInteger,
            MaximumInteger = error.MaximumInteger,
            EntityId = error.EntityId,
            AssetId = error.AssetId,
            SurfaceId = error.SurfaceId,
            PlacementId = error.PlacementId,
            ExpectedResourceKind = error.ExpectedResourceKind,
            ActualResourceKind = error.ActualResourceKind,
            NumericId = error.NumericId,
            HighWaterMark = error.HighWaterMark,
            MinimumNumber = error.MinimumNumber,
            MaximumNumber = error.MaximumNumber,
            DocumentId = error.DocumentId,
            WorldId = error.WorldId,
            InstanceId = error.InstanceId,
            ChildKind = error.ChildKind,
            Child = error.Child,
            RecordId = error.RecordId,
            SpaceId = error.SpaceId,
            Patch = error.Patch,
            ActualAnchor = error.ActualAnchor,
            ActualCount = error.ActualCount,
            ExpectedCount = error.ExpectedCount,
            ExpectedNumericId = error.ExpectedNumericId,
            ExpectedHash = error.ExpectedHash,
            ActualHash = error.ActualHash,
            DigestKind = error.DigestKind,
            DependencyId = error.DependencyId,
            ExpectedSchema = error.ExpectedSchema,
            StoragePath = error.StoragePath,
            DocumentForm = error.DocumentForm,
            AttachmentKind = error.AttachmentKind,
            EnumType = error.EnumType,
            Uri = error.Uri,
            ShapeType = error.ShapeType,
            ResourceRoot = error.ResourceRoot,
            ResourceIds = error.ResourceIds,
            SupportedVersions = error.SupportedVersions,
        } : null;

    internal static MapDocumentFinding ResolverContract(MapDocument document, string message, string fieldPath,
        string subjectKind, int? expectedResolverVersion = null, MapSupportRecipe? expectedSupportRecipe = null) =>
        new(MapDocumentRefusal.ResolverContractMismatch, message)
        {
            PayloadVersion = document.ResolverIdentity?.PayloadVersion,
            ResolverVersion = document.ResolverIdentity?.ResolverVersion,
            SupportRecipe = document.SupportRecipe,
            FieldPath = fieldPath,
            SubjectKind = subjectKind,
            ExpectedResolverVersion = expectedResolverVersion,
            ExpectedSupportRecipe = expectedSupportRecipe,
        };
}
