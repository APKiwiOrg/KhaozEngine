using System;

namespace KhaozEngine.TileWorld;

/// <summary>Any tile-world document, file, catalog or prefab failure. Tile worlds are dev-authored content,
/// so a bad world fails loudly (a boot, a save, a tool call) instead of being quarantined. Inspect
/// <see cref="Reason"/> and the applicable identifiers rather than parsing the developer-facing message.
/// <para>Regions are planar and identified by <see cref="Region"/>. A region lookup that received tile
/// coordinates also carries <see cref="TileX"/> and <see cref="TileZ"/>. These identifiers do not imply a
/// plane, and a guard that received only a region coordinate leaves both tile coordinates null.</para></summary>
public sealed class TileWorldException : Exception
{
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
}
