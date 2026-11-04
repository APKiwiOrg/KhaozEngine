using System;
using System.Collections.Generic;

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

    /// <summary>The plane count stored in the refused region.</summary>
    public int? ActualPlaneCount { get; init; }

    /// <summary>The document's required plane count for a refused region.</summary>
    public int? ExpectedPlaneCount { get; init; }

    /// <summary>The refused object's document-unique id.</summary>
    public long? ObjectId { get; init; }

    /// <summary>The refused object's catalog archetype id.</summary>
    public string? ArchetypeId { get; init; }

    /// <summary>The refused object's or marker's authored tile address, including its known plane.</summary>
    public TileCoord? Tile { get; init; }

    /// <summary>The object's or marker's refused plane, or the plane containing a refused dense layer.</summary>
    public int? Plane { get; init; }

    /// <summary>The document's plane count for an object or marker plane refusal.</summary>
    public int? PlaneCount { get; init; }

    /// <summary>The refused marker's name.</summary>
    public string? MarkerName { get; init; }

    /// <summary>The coordinate stored in a region file that disagrees with <see cref="Region"/>.</summary>
    public RegionCoord? ActualRegion { get; init; }

    /// <summary>The JSON path reported by the parser for malformed region data, when available.</summary>
    public string? Field { get; init; }

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
}
