using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

// The join-spawn seam: where a joining player's entity is BUILT, which is also the position its first snapshot
// carries. A rejoining player is placed from the resume hint (see ResumePositionCache) when one is known, so the
// client's resume snapshot is already the truth and a persistence restore landing afterwards has nothing left to
// move. Held in its own partial so both heads carry the identical seam and ShardedWorldServer.cs stays inside the
// file-size ratchet. See ShardedWorldServer.ResumeSpawn.cs for the multi-cell twin.
public sealed partial class WorldServer
{
    private ResumePositionProvider? resumePosition;

    /// <inheritdoc/>
    public void SetResumePositionProvider(ResumePositionProvider? provider) => resumePosition = provider;

    // The ABSOLUTE spawn position for a joining slot: the resume hint for this account when one is known, else the
    // configured spawn, else the per-slot default spread. The caller ground-clamps it, so a hint from a record
    // written on other terrain still settles onto this server's ground rather than being taken literally.
    private Vector3 JoinSpawn(int slot, string accountId) =>
        resumePosition is not null && resumePosition(accountId, out Vector3 resumed)
            ? resumed
            : ConfiguredSpawn(slot);

    // The hint-free half of JoinSpawn: the configured spawn, else the per-slot default spread. Kept separate so the
    // reset TryGetConfiguredSpawn hands back cannot itself be the rejected hint.
    private Vector3 ConfiguredSpawn(int slot) => config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);

    /// <inheritdoc/>
    public bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn)
    {
        if (!entityBySlot.ContainsKey(slot)) { spawn = default; return false; }
        // The same idle step OnJoin runs, so the reset settles onto the ground exactly as a fresh join does. The
        // simulator speaks the island's frame and the caller wants absolute, so it converts in and back out.
        spawn = ToAbsolute(simulator.Step(
            ToIsland(new PlayerMoveState { Position = ConfiguredSpawn(slot) }), MoveCommand.Idle, config.TickSeconds));
        return true;
    }
}
