using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Diagnostics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The basis read behind both heads' teleport-epoch stamp: the epoch a live player currently carries, which
/// <c>SetPlayerState</c> increments to mark a teleport. Reading it is the one place a stamp built on NO basis at all
/// could come from, so the miss is reported rather than swallowed. What this announces is a MISSING basis and never a
/// stale one: a restore that re-points ownership at a stale copy whose <see cref="MovementState"/> is old but present
/// stamps backwards with this guard silent, tracked as #653.
/// <para>
/// <b>Neither miss is reachable today (#637), and this exists because that is an invariant rather than a
/// coincidence.</b> On the single-<see cref="World"/> head, <c>entityBySlot</c> / <c>stateBySlot</c> / <c>netIdBySlot</c>
/// are written together in <c>OnJoin</c> and removed together in <c>OnLeave</c> and nowhere else, so the guard that
/// found the entity has already proved the state is there. On the sharded head, <c>OnJoin</c> sets
/// <see cref="MovementState"/> on the entity BEFORE it publishes the slot's NetId, a built-in component id is forced
/// to <c>ReplicationChannels.Default</c> so the component always follows a cell handoff, a cell blob never carries a
/// player (the snapshot excludes their NetIds), a cell owning a joined player is never evictable, and a ghosted or
/// mid-handoff copy is excluded from the ownership lookup entirely, so a frozen entity resolves to no owner and the
/// write is a no-op instead of a stamp.
/// </para>
/// <para>
/// What a miss WOULD cost, and why it earns a report rather than a silent zero: the client holds the epoch as a
/// high-water mark and cuts only on an advance past it (#409), so an epoch stamped back down to 1 against a client
/// watermark of 7 does not fire spuriously, it goes SILENT. Every teleport until the counter climbs back past the
/// watermark lands with no cut, no camera warp and no transition, and the positional gates only catch the long ones.
/// A future path that reaches either miss therefore has no symptom of its own worth trusting, which is exactly the
/// failure this announces by name.
/// </para>
/// <para>
/// <b>What the announcement IS, per build.</b> The error log always runs, through whatever sinks the host configured,
/// so a Release build (which is what every consumer's packed nupkg is) gets that line and nothing more: the miss is
/// recorded and the live server keeps running. A Debug build escalates straight after it, because
/// <see cref="Debug.Assert(bool, string)"/> is compiled out of Release entirely and, in a Debug build with no
/// debugger, ends the process through <c>Environment.FailFast</c> (a test host intercepts it instead and fails the
/// one test). Loud where loud is free, a recorded line where a live server needs to stay up.
/// </para>
/// </summary>
internal static class TeleportEpochGuard
{
    private static readonly ILogger Log = Diagnostics.Log.Get("TeleportEpoch");

    /// <summary>The single-<see cref="World"/> head's basis: the epoch on the slot's authoritative state.</summary>
    internal static uint BaseEpoch(Dictionary<int, PlayerMoveState> stateBySlot, int slot) =>
        stateBySlot.TryGetValue(slot, out PlayerMoveState cur) ? cur.TeleportEpoch : NoBasis("WorldServer", slot);

    /// <summary>The sharded head's basis: the epoch on the owning cell's <see cref="MovementState"/>.</summary>
    internal static uint BaseEpoch(World world, Entity entity, int slot) =>
        world.TryGet(entity, out MovementState prev) ? prev.TeleportEpoch : NoBasis("ShardedWorldServer", slot);

    // Zero keeps the pre-#637 behaviour exactly (nothing observable changes on a path that cannot run), and the
    // report is the whole point: a stamp built on this basis moves the authoritative epoch DOWN, and a client that
    // holds a watermark answers that by going quiet rather than by misbehaving visibly.
    //
    // THE ORDER IS LOAD-BEARING. A Debug.Assert with no debugger attached calls Environment.FailFast, which cannot be
    // caught and writes to stderr alone, so an assert placed first would take the process down before the configured
    // sinks ever saw the line. Logging first puts the miss through the session log on every build, and leaves the
    // assert as the Debug-only escalation on top of it.
    private static uint NoBasis(string head, int slot)
    {
        string message = $"{head}: slot {slot} has a live player entity with no teleport-epoch basis, so the next "
                       + "teleport stamps from 0 and moves the authoritative epoch backwards. The client holds the "
                       + "epoch as a high-water mark, so it will SWALLOW every teleport until the counter climbs "
                       + "back past its watermark. See KhaozEngine #637 for the invariants this breaks.";
        Log.Error(message);
        Debug.Assert(false, message);
        return 0u;
    }
}
