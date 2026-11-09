using System;
using KhaozEngine.MapDoc.Editing;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc;

/// <summary>An immutable validation finding. Message is its prose projection, while Reason and the applicable
/// identifiers describe the refused rule. Identifiers not known by the producing guard remain null.</summary>
/// <param name="Reason">The rule refused by the validator.</param>
/// <param name="Message">The developer-facing explanation, not a classification token.</param>
public sealed record MapDocumentFinding(MapDocumentRefusal Reason, string Message)
{
    IReadOnlyList<int>? _supportedVersions;
    IReadOnlyList<string>? _resourceIds;
    IReadOnlyList<string>? _expectedResourceIds;
    IReadOnlyList<string>? _sourceIds;
    IReadOnlyList<string>? _spaceIds;
    IReadOnlyList<string>? _markerIds;

    /// <summary>The version required by an operation that accepts one version.</summary>
    public int? ExpectedVersion { get; init; }
    /// <summary>The numeric value required by an equality guard.</summary>
    public double? ExpectedNumber { get; init; }
    /// <summary>The document storage form required by the refused operation.</summary>
    public MapDocumentForm? ExpectedDocumentForm { get; init; }
    /// <summary>The required file name, when the storage contract names one.</summary>
    public string? FileName { get; init; }

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
    /// <summary>The patch-local edge whose metadata rule was refused.</summary>
    public MapCellEdge? Edge { get; init; }
    /// <summary>The document tile being read, when known.</summary>
    public MapTileCoord? Tile { get; init; }
    /// <summary>The preceding tile in a refused canonical world-hash ordering.</summary>
    public MapTileCoord? PreviousTile { get; init; }
    /// <summary>The authored sculpt tile whose identity or extent failed validation.</summary>
    public MapTileCoord? SculptTile { get; init; }
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
    /// <summary>The canonical JSON pointer of a prefab reference occurrence or slot, when known.</summary>
    public string? ReferencePath { get; init; }
    /// <summary>The kind of document content or identity whose rule was refused.</summary>
    public string? SubjectKind { get; init; }
    /// <summary>The API operation whose entry-point contract was refused.</summary>
    public string? Operation { get; init; }

    internal MapResolveStatus? ResolutionStatus { get; init; }
    internal bool InvalidPrefixSuppressed { get; init; }
    internal MapRefinementStatus? RefinementStatus { get; init; }

    /// <summary>The foliage layer identity, after it has been read.</summary>
    public string? LayerId { get; init; }
    /// <summary>The foliage sampling policy identity checked by the guard.</summary>
    public string? PolicyId { get; init; }
    /// <summary>The foliage sampling policy identity required by the guard.</summary>
    public string? ExpectedPolicyId { get; init; }
    /// <summary>The zero-based position of the containing or refused JSON array item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The other zero-based region or page index participating in an overlap.</summary>
    public int? OtherIndex { get; init; }
    /// <summary>The authored cell classification requiring explicit conversion acceptance.</summary>
    public MapCellConversionClass? ConversionClass { get; init; }
    /// <summary>The source surface presence and arithmetic policy checked by conversion.</summary>
    public MapPresencePolicy? PresencePolicy { get; init; }

    /// <summary>The foliage chunk coordinates, after both coordinates have been read.</summary>
    public MapFoliageChunkKey? FoliageChunk { get; init; }
    /// <summary>The foliage differential status that prevented a policy preview from issuing a digest.</summary>
    public MapFoliageStatus? FoliageStatus { get; init; }
    /// <summary>The advertised foliage chunk key required by the compared resident row.</summary>
    public MapFoliageChunkKey? ExpectedFoliageChunk { get; init; }
    /// <summary>The pinned foliage pack read status that prevented the operation.</summary>
    public MapFoliageReadStatus? FoliageReadStatus { get; init; }
    /// <summary>The measured or decoded byte count, when the whole value can be measured.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The required decoded byte count, when checked.</summary>
    public long? ExpectedByteCount { get; init; }
    /// <summary>The producer's declared patch status, when acquisition admission checks it.</summary>
    public KhaozEngine.MapDoc.Storage.MapPatchStatus? PatchStatus { get; init; }
    /// <summary>The captured acquisition status that did not satisfy the operation's requirements.</summary>
    public KhaozEngine.MapDoc.Storage.MapAcquireStatus? AcquireStatus { get; init; }
    /// <summary>The pinned acquisition snapshot identity required by the producer.</summary>
    public string? ExpectedSnapshot { get; init; }
    /// <summary>The acquisition snapshot identity returned by the producer.</summary>
    public string? ActualSnapshot { get; init; }
    /// <summary>The runtime source type lacking required acquisition support.</summary>
    public Type? SourceType { get; init; }
    /// <summary>The work or metadata budget whose checked capacity was exceeded.</summary>
    public string? Budget { get; init; }
    /// <summary>The inclusive byte, work or whole-metre world coordinate limit, when checked.</summary>
    public long? Limit { get; init; }
    /// <summary>The refused payload version, when read as an integer.</summary>
    public int? ActualVersion { get; init; }
    /// <summary>The next format version required by a missing document migration.</summary>
    public int? TargetVersion { get; init; }
    /// <summary>The world or grid axis checked by an index refusal.</summary>
    public string? Axis { get; init; }
    /// <summary>The first computed index of the refused range, or its single candidate index.</summary>
    public double? FirstIndex { get; init; }
    /// <summary>The last computed index of the refused range, or its single candidate index.</summary>
    public double? LastIndex { get; init; }
    /// <summary>The grid origin on the checked axis.</summary>
    public double? AxisOrigin { get; init; }
    /// <summary>The navigation, server or storage grid whose rule failed.</summary>
    public string? GridKind { get; init; }
    /// <summary>The storage tile edge in metres at the refused grid boundary.</summary>
    public float? StorageTileSize { get; init; }
    /// <summary>The submitted storage tile component whose block index was refused.</summary>
    public int? TileIndex { get; init; }
    /// <summary>The number of storage tiles along one block edge.</summary>
    public long? StorageTiles { get; init; }
    /// <summary>The refused numeric value, when representable as a double.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The refused numeric value, when representable as a signed 64-bit integer.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The preceding sequence value, when canonical ordering is checked.</summary>
    public long? PreviousInteger { get; init; }
    /// <summary>The submitted text or record discriminant, when checked directly.</summary>
    public string? ActualText { get; init; }
    /// <summary>The inclusive minimum integer accepted by the checked field.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum integer accepted by the checked field.</summary>
    public long? MaximumInteger { get; init; }
    /// <summary>The refused identity, when known.</summary>
    public string? EntityId { get; init; }
    /// <summary>The removed or unavailable target of a retained reference.</summary>
    public string? TargetId { get; init; }
    /// <summary>The runtime command type refused by native transaction admission.</summary>
    public Type? CommandType { get; init; }
    /// <summary>The runtime edit type refused by an operation that supports only selected edits.</summary>
    public Type? EditType { get; init; }
    /// <summary>The owner of the refused operation or geometry, such as a placement, terrain chunk or water body.</summary>
    public string? OwnerId { get; init; }
    /// <summary>The compiled water body whose world or residency admission failed.</summary>
    public string? BodyId { get; init; }
    /// <summary>The checked X coordinate in metres, absolute world or local to the refused frame operation.</summary>
    public double? PositionX { get; init; }
    /// <summary>The checked Y coordinate in metres, absolute world or local to the refused frame operation.</summary>
    public double? PositionY { get; init; }
    /// <summary>The checked Z coordinate in metres, absolute world or local to the refused frame operation.</summary>
    public double? PositionZ { get; init; }

    /// <summary>The asset descriptor whose admission failed, when known.</summary>
    public string? AssetId { get; init; }
    /// <summary>The other asset sharing the refused collider resource.</summary>
    public string? OtherAssetId { get; init; }
    /// <summary>The declared surface whose metadata failed validation, when known.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The other paint surface in a priority conflict or the required physical paint target.</summary>
    public string? OtherSurfaceId { get; init; }
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
    /// <summary>The related authored record required by the refused rule, such as a prism parent.</summary>
    public string? RelatedRecordId { get; init; }
    /// <summary>The space referenced by the refused authored record, when known.</summary>
    public string? SpaceId { get; init; }
    /// <summary>The submitted or referenced authored patch, when known.</summary>
    public MapPatchKey? Patch { get; init; }
    /// <summary>The lattice address whose shared exact vertex height disagreed.</summary>
    public MapLatticeAddress? Address { get; init; }
    /// <summary>The selected zero-based cell within its 64 by 64 patch slot.</summary>
    public int? SlotCell { get; init; }
    /// <summary>The zero-based cell of RelatedRecordId in a second footprint involved in an overlap.</summary>
    public int? RelatedSlotCell { get; init; }
    /// <summary>The patch key required by a payload request or declared corner owner.</summary>
    public MapPatchKey? ExpectedPatch { get; init; }
    /// <summary>The submitted runtime topology record type, when unsupported.</summary>
    public Type? RecordType { get; init; }
    /// <summary>The topology record type required by the refused lookup.</summary>
    public Type? ExpectedRecordType { get; init; }
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
    /// <summary>The physics shape type whose data or supported representation was refused.</summary>
    public Type? ShapeType { get; init; }
    /// <summary>The resource root used by complete document validation, when known.</summary>
    public string? ResourceRoot { get; init; }

    /// <summary>The resource identities associated with the refusal, as an owned read-only snapshot in producer order.</summary>
    public IReadOnlyList<string>? ResourceIds
    {
        get => _resourceIds;
        init => _resourceIds = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The expected verified root resource identities, as an owned read-only snapshot in closure order.</summary>
    public IReadOnlyList<string>? ExpectedResourceIds
    {
        get => _expectedResourceIds;
        init => _expectedResourceIds = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The source identities associated with the refusal, as an owned read-only snapshot in producer
    /// order. Legacy spawn sources retain Spawn then PlayerSpawn order, including equal IDs in different lists.</summary>
    public IReadOnlyList<string>? SourceIds
    {
        get => _sourceIds;
        init => _sourceIds = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The authored parent space identities, copied as an owned read-only snapshot in discovery order.</summary>
    public IReadOnlyList<string>? SpaceIds
    {
        get => _spaceIds;
        init => _spaceIds = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The colliding marker IDs in ordinal order, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? MarkerIds
    {
        get => _markerIds;
        init => _markerIds = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The supported payload versions, as an owned read-only snapshot.</summary>
    public IReadOnlyList<int>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>Projects an atomic typed refusal without inventing a reason for a legacy failure.</summary>
    internal static MapDocumentFinding? FromException(MapDocumentException error) => error.Reason is { } reason
        ? new(reason, error.Message)
        {
            ExpectedVersion = error.ExpectedVersion,
            ExpectedNumber = error.ExpectedNumber,
            ExpectedDocumentForm = error.ExpectedDocumentForm,
            FileName = error.FileName,
            SourceName = error.SourceName,
            Path = error.Path,
            ResourceId = error.ResourceId,
            Width = error.Width,
            Depth = error.Depth,
            Numerator = error.Numerator,
            Denominator = error.Denominator,
            CellX = error.CellX,
            CellZ = error.CellZ,
            Edge = error.Edge,
            Tile = error.Tile,
            PreviousTile = error.PreviousTile,
            SculptTile = error.SculptTile,
            LineNumber = error.LineNumber,
            BytePositionInLine = error.BytePositionInLine,
            PayloadVersion = error.PayloadVersion,
            ResolverVersion = error.ResolverVersion,
            OptionsResolverVersion = error.OptionsResolverVersion,
            ExpectedResolverVersion = error.ExpectedResolverVersion,
            SupportRecipe = error.SupportRecipe,
            ExpectedSupportRecipe = error.ExpectedSupportRecipe,
            FieldPath = error.FieldPath,
            ReferencePath = error.ReferencePath,
            SubjectKind = error.SubjectKind,
            Operation = error.Operation,
            ResolutionStatus = error.ResolutionStatus,
            InvalidPrefixSuppressed = error.InvalidPrefixSuppressed,
            RefinementStatus = error.RefinementStatus,
            LayerId = error.LayerId,
            PolicyId = error.PolicyId,
            ExpectedPolicyId = error.ExpectedPolicyId,
            Index = error.Index,
            OtherIndex = error.OtherIndex,
            ConversionClass = error.ConversionClass,
            PresencePolicy = error.PresencePolicy,
            FoliageChunk = error.FoliageChunk,
            FoliageStatus = error.FoliageStatus,
            ExpectedFoliageChunk = error.ExpectedFoliageChunk,
            FoliageReadStatus = error.FoliageReadStatus,
            ActualByteCount = error.ActualByteCount,
            ExpectedByteCount = error.ExpectedByteCount,
            PatchStatus = error.PatchStatus,
            AcquireStatus = error.AcquireStatus,
            SourceType = error.SourceType,
            ExpectedSnapshot = error.ExpectedSnapshot,
            ActualSnapshot = error.ActualSnapshot,
            Budget = error.Budget,
            Limit = error.Limit,
            ActualVersion = error.ActualVersion,
            TargetVersion = error.TargetVersion,
            Axis = error.Axis,
            FirstIndex = error.FirstIndex,
            LastIndex = error.LastIndex,
            AxisOrigin = error.AxisOrigin,
            GridKind = error.GridKind,
            StorageTileSize = error.StorageTileSize,
            TileIndex = error.TileIndex,
            StorageTiles = error.StorageTiles,
            ActualNumber = error.ActualNumber,
            ActualInteger = error.ActualInteger,
            PreviousInteger = error.PreviousInteger,
            ActualText = error.ActualText,
            MinimumInteger = error.MinimumInteger,
            MaximumInteger = error.MaximumInteger,
            EntityId = error.EntityId,
            TargetId = error.TargetId,
            CommandType = error.CommandType,
            EditType = error.EditType,
            OwnerId = error.OwnerId,
            BodyId = error.BodyId,
            PositionX = error.PositionX,
            PositionY = error.PositionY,
            PositionZ = error.PositionZ,
            AssetId = error.AssetId,
            OtherAssetId = error.OtherAssetId,
            SurfaceId = error.SurfaceId,
            OtherSurfaceId = error.OtherSurfaceId,
            MarkerId = error.MarkerId,
            MarkerName = error.MarkerName,
            SourceId = error.SourceId,
            PlacementId = error.PlacementId,
            ProviderPlacementId = error.ProviderPlacementId,
            SupportStatus = error.SupportStatus,
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
            ExpectedChildKind = error.ExpectedChildKind,
            ReferenceScope = error.ReferenceScope,
            ExpectedReferenceScope = error.ExpectedReferenceScope,
            ExpectedChild = error.ExpectedChild,
            Child = error.Child,
            RecordId = error.RecordId,
            RelatedRecordId = error.RelatedRecordId,
            SpaceId = error.SpaceId,
            Patch = error.Patch,
            Address = error.Address,
            SlotCell = error.SlotCell,
            RelatedSlotCell = error.RelatedSlotCell,
            ExpectedPatch = error.ExpectedPatch,
            RecordType = error.RecordType,
            ExpectedRecordType = error.ExpectedRecordType,
            ActualAnchor = error.ActualAnchor,
            ActualTransform = error.ActualTransform,
            ExpectedTransform = error.ExpectedTransform,
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
            ExpectedResourceIds = error.ExpectedResourceIds,
            SourceIds = error.SourceIds,
            SpaceIds = error.SpaceIds,
            MarkerIds = error.MarkerIds,
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
