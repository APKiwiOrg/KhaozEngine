using System;

namespace KhaozEngine.TileWorld;

/// <summary>Any tile-world document, file, catalog or prefab failure. Tile worlds are dev-authored content,
/// so a bad world fails loudly (a boot, a save, a tool call) with the region, file or field named in the
/// message, instead of being quarantined.</summary>
public sealed class TileWorldException : Exception
{
    public TileWorldException(string message) : base(message) { }
    public TileWorldException(string message, Exception? inner) : base(message, inner) { }
}
