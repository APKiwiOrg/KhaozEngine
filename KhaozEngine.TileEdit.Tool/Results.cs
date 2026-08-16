using System.Collections.Generic;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>A tile rect flattened for the wire: the same four numbers <see cref="TileRect"/> carries, without
/// its derived members, so a result serializes as exactly the fields a client needs to read back.</summary>
public sealed record RectInfo(int X, int Z, int Width, int Height)
{
    /// <summary>The wire form of a document rect.</summary>
    public static RectInfo Of(TileRect rect) => new(rect.X, rect.Z, rect.Width, rect.Height);

    /// <summary>The rect as the document's own type, for a caller that wants to keep working with it.</summary>
    public TileRect ToRect() => new(X, Z, Width, Height);
}

/// <summary>One rect a mutation touched, on one plane: what a renderer would have to rebuild.</summary>
public sealed record DirtyRectInfo(RectInfo Rect, int Plane);

/// <summary>Result of opening or creating a world: the directory, its identity, and a full summary.</summary>
public sealed record OpenResult(string Path, string Id, string DisplayName, WorldSummary Summary);

/// <summary>Result of a save: the directory written and the world hash of what landed there.</summary>
public sealed record SaveResult(string Path, string WorldHash);

/// <summary>A flat snapshot of the open world: identity, geometry, counts, hash, and the editing state (dirty
/// flag plus undo and redo depth and labels). Kept flat so it serializes cleanly to the MCP client.</summary>
public sealed record WorldSummary(string Id, string DisplayName, string Path,
    int PlaneCount, float TileSize, int RegionCount, int ObjectCount, int MarkerCount,
    string WorldHash, bool Dirty, int UndoDepth, int RedoDepth, string? UndoLabel, string? RedoLabel,
    IReadOnlyList<string> CatalogPaths);

/// <summary>Result of validation: whether the world is valid, and every issue as <c>[code] message</c>.</summary>
public sealed record ValidateResult(bool Valid, IReadOnlyList<string> Issues);

/// <summary>Result of one command through the session: the command's undo label, the editing state after it,
/// the new world hash, and the rects it touched. Not sealed, because the verbs that learn something extra from
/// the command they built (an allocated object id, a written corner count) return a record derived from this
/// one rather than a parallel shape a client would have to read differently.</summary>
public record MutationResult(string Label, bool Dirty, int UndoDepth, string WorldHash,
    IReadOnlyList<DirtyRectInfo> DirtyRects);

/// <summary>A mutation that placed one object, carrying the id the document allocated for it.</summary>
public sealed record ObjectPlaceResult(string Label, bool Dirty, int UndoDepth, string WorldHash,
    IReadOnlyList<DirtyRectInfo> DirtyRects, long ObjectId)
    : MutationResult(Label, Dirty, UndoDepth, WorldHash, DirtyRects)
{
    /// <summary>Wraps the session's result with the id the placement allocated.</summary>
    public ObjectPlaceResult(MutationResult inner, long objectId)
        : this(inner.Label, inner.Dirty, inner.UndoDepth, inner.WorldHash, inner.DirtyRects, objectId) { }
}

/// <summary>A mutation that wrote corner heights: how many corners the rect covered and how many actually
/// landed, which differ when the rect reached space no region holds.</summary>
public sealed record HeightResult(string Label, bool Dirty, int UndoDepth, string WorldHash,
    IReadOnlyList<DirtyRectInfo> DirtyRects, int WrittenCount, int CornerCount)
    : MutationResult(Label, Dirty, UndoDepth, WorldHash, DirtyRects)
{
    /// <summary>Wraps the session's result with the corner counts the height command reported.</summary>
    public HeightResult(MutationResult inner, int writtenCount, int cornerCount)
        : this(inner.Label, inner.Dirty, inner.UndoDepth, inner.WorldHash, inner.DirtyRects, writtenCount, cornerCount) { }
}

/// <summary>A mutation that placed a batch of objects (a line, a scatter): how many landed and their ids, so a
/// client can tag or remove exactly what it just made.</summary>
public sealed record PlacementBatchResult(string Label, bool Dirty, int UndoDepth, string WorldHash,
    IReadOnlyList<DirtyRectInfo> DirtyRects, int Count, IReadOnlyList<long> ObjectIds)
    : MutationResult(Label, Dirty, UndoDepth, WorldHash, DirtyRects)
{
    /// <summary>Wraps the session's result with the ids the batch allocated.</summary>
    public PlacementBatchResult(MutationResult inner, IReadOnlyList<long> objectIds)
        : this(inner.Label, inner.Dirty, inner.UndoDepth, inner.WorldHash, inner.DirtyRects, objectIds.Count, objectIds) { }
}

/// <summary>Result of an undo or a redo run: how many steps actually moved (below the number asked for when the
/// stack ran out), the editing state after it, and the labels now on top of each stack.</summary>
public sealed record UndoResult(int Steps, bool Dirty, int UndoDepth, int RedoDepth,
    string? UndoLabel, string? RedoLabel, string WorldHash);

/// <summary>Result of writing a prefab file: where it landed and what it carries.</summary>
public sealed record PrefabSaveResult(string Path, string Name, int Width, int Height, int PlaneCount,
    int ObjectCount, int MarkerCount, long SizeBytes);

/// <summary>Result of a render: the PNG bytes, a one-line description of how the shot was framed, the image
/// size, and the file it was also written to when the caller asked for one.</summary>
public sealed record RenderResult(byte[] Png, string Framing, int Width, int Height, string? SavedPath);
