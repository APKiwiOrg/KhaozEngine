using System;

namespace KhaozEngine.Dungeon;

/// <summary>A structured refusal of a kit lookup. Existing <see cref="InvalidOperationException"/> catches remain
/// valid.</summary>
public sealed class DungeonKitException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the lookup.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public DungeonKitException(DungeonKitRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the lookup.</summary>
    public DungeonKitRefusal Reason { get; }

    /// <summary>The piece with no kit id, for <see cref="DungeonKitRefusal.PieceUnmapped"/>.</summary>
    public DungeonPiece? Piece { get; init; }
}
