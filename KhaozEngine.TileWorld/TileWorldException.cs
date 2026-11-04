using System;
using System.Collections.Generic;
using KhaozEngine.Content;

namespace KhaozEngine.TileWorld;

/// <summary>Any tile-world document, file, catalog or prefab failure. Tile worlds are dev-authored content,
/// so a bad world fails loudly (a boot, a save, a tool call) instead of being quarantined. Inspect
/// <see cref="Reason"/> and the applicable identifiers rather than parsing the developer-facing message.
/// <para>Regions are planar and identified by <see cref="Region"/>. A region lookup that received tile
/// coordinates also carries <see cref="TileX"/> and <see cref="TileZ"/>. These identifiers do not imply a
/// plane, and a guard that received only a region coordinate leaves both tile coordinates null.</para></summary>
public sealed class TileWorldException : Exception
{
    IReadOnlyList<TileWorldIssue>? _issues;
    IReadOnlyList<string>? _schemaFields;
    IReadOnlyList<JsonSchemaFinding>? _schemaFindings;

    public TileWorldException(string message) : base(message) { }
    public TileWorldException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Creates a refusal with its stable reason and developer-facing message.</summary>
    public TileWorldException(TileWorldRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a refusal retaining the original cause, its stable reason and developer-facing message.</summary>
    public TileWorldException(TileWorldRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The stable reason for the refusal. Null only for an instance built through a message-only
    /// constructor by code outside the engine.</summary>
    public TileWorldRefusal? Reason { get; }

    /// <summary>The region refused, when the guard knows its coordinate.</summary>
    public RegionCoord? Region { get; init; }

    /// <summary>The requested world tile's X coordinate, when the guard received a tile. Planar, with no
    /// implied plane. Null when only a region coordinate was supplied.</summary>
    public int? TileX { get; init; }

    /// <summary>The requested world tile's Z coordinate, when the guard received a tile. Planar, with no
    /// implied plane. Null when only a region coordinate was supplied.</summary>
    public int? TileZ { get; init; }

    /// <summary>The source file or content name, when the refusal has one.</summary>
    public string? SourceName { get; init; }

    /// <summary>The region hash recorded in the manifest for <see cref="TileWorldRefusal.RegionHashMismatch"/>.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>The hash of the region bytes read for <see cref="TileWorldRefusal.RegionHashMismatch"/>.</summary>
    public string? ActualHash { get; init; }

    /// <summary>The plane count carried by the refused region or prefab.</summary>
    public int? ActualPlaneCount { get; init; }

    /// <summary>The document's required region plane count, or the prefab's declared plane count.</summary>
    public int? ExpectedPlaneCount { get; init; }

    /// <summary>The refused object's document-unique id.</summary>
    public long? ObjectId { get; init; }

    /// <summary>The refused or required catalog archetype id, as supplied.</summary>
    public string? ArchetypeId { get; init; }

    /// <summary>The refused object's or marker's authored tile address, including its known plane.
    /// Prefab refusals use prefab-relative coordinates.</summary>
    public TileCoord? Tile { get; init; }

    /// <summary>The object's or marker's refused plane, or the plane containing a refused dense layer.</summary>
    public int? Plane { get; init; }

    /// <summary>The document's or prefab's plane count for an object or marker plane refusal, or the invalid
    /// plane count refused by manifest or prefab-file admission.</summary>
    public int? PlaneCount { get; init; }

    /// <summary>The refused marker's name.</summary>
    public string? MarkerName { get; init; }

    /// <summary>The coordinate stored in a region file that disagrees with <see cref="Region"/>.</summary>
    public RegionCoord? ActualRegion { get; init; }

    /// <summary>The refused catalog or manifest field, or the parser's JSON path for malformed region,
    /// manifest, prefab or catalog data, when available.</summary>
    public string? Field { get; init; }

    /// <summary>The non-positive tile size refused by manifest admission.</summary>
    public float? TileSize { get; init; }

    /// <summary>The manifest's original format version for an unsupported version or missing migration.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The engine's supported manifest version for an unsupported-version refusal.</summary>
    public int? SupportedFormatVersion { get; init; }

    /// <summary>The source version of the missing or null-returning manifest migration step.</summary>
    public int? MigrationFrom { get; init; }

    /// <summary>The target version of the missing or null-returning manifest migration step.</summary>
    public int? MigrationTo { get; init; }

    /// <summary>The dense layer refused, such as heights, underlay or settings.</summary>
    public string? LayerName { get; init; }

    /// <summary>The decoded byte count for <see cref="TileWorldRefusal.LayerByteCountMismatch"/>.</summary>
    public int? ActualByteCount { get; init; }

    /// <summary>The required byte count for <see cref="TileWorldRefusal.LayerByteCountMismatch"/>.</summary>
    public int? ExpectedByteCount { get; init; }

    /// <summary>The embedded schema resource name absent from the assembly.</summary>
    public string? ResourceName { get; init; }

    /// <summary>The document id for <see cref="TileWorldRefusal.WorldValidationFailed"/>.</summary>
    public string? WorldId { get; init; }

    /// <summary>The total number of semantic validation issues, including those beyond the message's summary.</summary>
    public int? IssueCount { get; init; }

    /// <summary>All semantic validation issues. Setting this property copies the supplied collection into an
    /// owned read-only snapshot, so neither the source nor a cast of the exposed list can change it.</summary>
    public IReadOnlyList<TileWorldIssue>? Issues
    {
        get => _issues;
        init => _issues = value is null ? null : Array.AsReadOnly<TileWorldIssue>([.. value]);
    }

    /// <summary>The refused prefab's authored name, when admission has a prefab value.</summary>
    public string? PrefabName { get; init; }

    /// <summary>The refused prefab's declared footprint width in tiles.</summary>
    public int? Width { get; init; }

    /// <summary>The refused prefab's declared footprint height in tiles.</summary>
    public int? Height { get; init; }

    /// <summary>The entry count carried by a refused prefab layer.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The required entry count for a refused prefab layer.</summary>
    public int? ExpectedCount { get; init; }

    /// <summary>The catalog source that first defined a duplicate material or archetype id.</summary>
    public string? FirstSourceName { get; init; }

    /// <summary>The refused ground material's catalog id.</summary>
    public ushort? MaterialId { get; init; }

    /// <summary>The refused ground material's authoring name.</summary>
    public string? MaterialName { get; init; }

    /// <summary>The zero-based index of the refused walk surface.</summary>
    public int? SurfaceIndex { get; init; }

    /// <summary>The refused collision height or non-finite walk-surface value.</summary>
    public float? Value { get; init; }

    /// <summary>The lower-bound field of an unordered walk-surface extent.</summary>
    public string? MinField { get; init; }

    /// <summary>The upper-bound field of an unordered walk-surface extent.</summary>
    public string? MaxField { get; init; }

    /// <summary>The lower-bound value of an unordered walk-surface extent.</summary>
    public float? Minimum { get; init; }

    /// <summary>The upper-bound value of an unordered walk-surface extent.</summary>
    public float? Maximum { get; init; }

    /// <summary>The failing instance locations as JSON Pointers for a catalog schema mismatch.
    /// Setting this property copies the collection into an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? SchemaFields
    {
        get => _schemaFields;
        init => _schemaFields = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }

    /// <summary>The structured findings for a catalog schema mismatch. Setting this property copies the
    /// collection into an owned read-only snapshot. Each finding is immutable and engine-owned.</summary>
    public IReadOnlyList<JsonSchemaFinding>? SchemaFindings
    {
        get => _schemaFindings;
        init => _schemaFindings = value is null ? null : Array.AsReadOnly<JsonSchemaFinding>([.. value]);
    }
}
