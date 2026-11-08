namespace KhaozEngine.Dungeon;

/// <summary>Rules <see cref="DungeonKitMap.Require"/> refuses, carried as <see cref="DungeonKitException.Reason"/>.</summary>
public enum DungeonKitRefusal
{
    /// <summary>Refuses a <see cref="DungeonPiece"/> the kit map has no kit id for.</summary>
    PieceUnmapped,
}
