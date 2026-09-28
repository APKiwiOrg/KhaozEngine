namespace KhaozEngine.TileWorld.Netcode;

internal readonly record struct TilePreparationChunkHeader(long ServerTick, ushort ChunkIndex, ushort ChunkCount);

internal enum TileCombatTerminalKind : byte
{
    Resolved = 1,
    Cancelled = 2
}

internal readonly record struct TileCombatTerminal(
    long AttackerNetId, long TargetNetId, ulong AttackId, uint Revision,
    uint PresentationKey, long ImpactTick, TileCombatTerminalKind Kind,
    TileCombatPreparationEndReason Reason, ushort Amount, byte HitKind, byte Flags);

/// <summary>A complete state set with its own record array, never an assembler's mutable scratch storage.</summary>
internal sealed record TilePreparationStateFrame(long ServerTick, TileCombatPreparation[] Records);

/// <summary>A complete terminal set in transition order, with an owned record array.</summary>
internal sealed record TilePreparationTerminalFrame(long ServerTick, TileCombatTerminal[] Records);
