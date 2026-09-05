using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The object-state half of <see cref="TileWorldClient"/>: the mirror of every
/// <see cref="TileObjectState"/> this client currently holds, keyed by the OBJECT rather than by the entity,
/// plus the two events a renderer reacts to instead of polling.
/// <para>Keyed by object id because that is the only key a head can do anything with: a net id names an entity
/// nothing draws, while an object id names a placement in the document the head already has. The mirror is a
/// dictionary rather than a per-frame walk of the world for the same reason
/// <c>TileWorldClient.CaptureLatestTiles</c> keeps one: the snapshot apply is the ONE instant the world holds
/// the server's own answer, and a pass taken anywhere else reads the delayed presentation timeline back.</para>
/// </summary>
public sealed partial class TileWorldClient
{
    // The mirror: object id to state, refreshed whole on every applied snapshot. A full-state snapshot is what
    // makes one pass do both jobs, CaptureLatestTiles' reasoning: anything the pass does not see is gone, either
    // because the server cleared it or because it left this viewer's area of interest.
    readonly Dictionary<long, int> objectStates = new();
    readonly HashSet<long> liveObjectStates = new();
    readonly List<long> goneObjectStates = new();
    // The changes one refresh found, collected inside the ECS pass and raised outside it: a handler may spawn or
    // despawn, and the world must not be mutated mid iteration. SampleRemote's rule.
    readonly List<(long ObjectId, int State)> changedObjectStates = new();
    RefAction<NetId>? captureObjectStates;

    /// <summary>Raised with an object's id and its new state the first time this client sees the object in that
    /// state: it entered the area of interest carrying one, the server set one, or the server changed the one it
    /// had. Once per change, never once per snapshot, so a head may swap a mesh straight out of it.</summary>
    public event Action<long, int>? ObjectStateChanged;

    /// <summary>Raised with an object's id when this client stops holding a state for it, so the object is back
    /// to the form the document authored. Three things reach it: the server cleared the state, the state's clock
    /// ran out, or the object left this viewer's area of interest.
    /// <para>That last one is deliberate rather than a leak. The engine cannot tell a head about an object it is
    /// not being served, so a head that kept drawing the last state it heard would be drawing a guess with no
    /// expiry. Redrawing the authored form is the only answer that stays true, and the state comes back through
    /// <see cref="ObjectStateChanged"/> the moment the object is in interest again. The interest radius is
    /// measured in CELLS, and a cell is a whole region, so the boundary is far outside the tiles a head is
    /// drawing detail at.</para></summary>
    public event Action<long>? ObjectStateCleared;

    /// <summary>How many objects this client currently holds a state for.</summary>
    public int ObjectStateCount => objectStates.Count;

    /// <summary>The state an object is in, as this client last heard it.</summary>
    /// <param name="objectId">The authored object's id.</param>
    /// <param name="state">The state, when the answer is true.</param>
    /// <returns>False when this client holds no state for the object, which is both "the object is as it was
    /// authored" and "the object is not being served to me".</returns>
    public bool TryGetObjectState(long objectId, out int state) => objectStates.TryGetValue(objectId, out state);

    /// <summary>Fills a caller's buffer with every object state this client currently holds. Cleared first,
    /// unsorted, complete: a state the server cleared is simply absent on the next call, so a game enumerates
    /// per frame and holds no lifecycle of its own.
    /// <para>Allocation-free once the buffer has grown to fit, which is what makes it safe to call every frame:
    /// the mirror is walked through the dictionary's own struct enumerator and the entries are value tuples, so
    /// a warm call allocates nothing at all. A head that would rather not poll subscribes to
    /// <see cref="ObjectStateChanged"/> and <see cref="ObjectStateCleared"/> instead, which is the cheaper shape
    /// for a renderer and the reason both exist.</para></summary>
    /// <param name="into">The buffer to fill. Reused by the caller, allocated by nobody here.</param>
    public void CollectObjectStates(List<(long ObjectId, int State)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        foreach (KeyValuePair<long, int> pair in objectStates) into.Add((pair.Key, pair.Value));
    }

    // Rebuilds the mirror off the snapshot Apply has just written, then raises what changed. Called from
    // OnSnapshot beside CaptureLatestTiles, and for the same reason: this is the one instant the world holds the
    // server's own answer rather than the delayed presentation timeline's.
    void RefreshObjectStates()
    {
        captureObjectStates ??= CaptureObjectState;
        liveObjectStates.Clear();
        changedObjectStates.Clear();
        World.ForEach(captureObjectStates);

        goneObjectStates.Clear();
        // Every live state was just written into the mirror, so equal counts means nothing went stale and the
        // prune can be skipped, which is the ordinary snapshot. CaptureLatestTiles' shortcut.
        if (objectStates.Count != liveObjectStates.Count)
        {
            foreach (long objectId in objectStates.Keys)
                if (!liveObjectStates.Contains(objectId)) goneObjectStates.Add(objectId);
            for (int i = 0; i < goneObjectStates.Count; i++) objectStates.Remove(goneObjectStates[i]);
        }

        // Raised AFTER the pass and after the prune, so a handler sees a settled mirror and may touch the world.
        // Changed before cleared, because a head keying a mesh swap on the pair wants the arrival of a state
        // ordered after the departure it replaces, and an object never holds two states at once.
        //
        // COPIED OUT for the reason RaiseCombat is: both lists are the client's, so a handler that re-enters Poll
        // refills them from a fresh snapshot and the changes still to come in the outer walk are simply dropped.
        // The copy is only paid on a snapshot that actually changed something, which is not the ordinary one, and
        // not at all by a head with no subscriber.
        if (ObjectStateChanged is not null && changedObjectStates.Count > 0)
        {
            (long ObjectId, int State)[] changed = changedObjectStates.ToArray();
            for (int i = 0; i < changed.Length; i++)
                ObjectStateChanged?.Invoke(changed[i].ObjectId, changed[i].State);
        }
        if (ObjectStateCleared is not null && goneObjectStates.Count > 0)
        {
            long[] gone = goneObjectStates.ToArray();
            for (int i = 0; i < gone.Length; i++) ObjectStateCleared?.Invoke(gone[i]);
        }
    }

    // One state entity, one snapshot. The net id is not read at all: an object state is keyed by the object it
    // belongs to, and the entity behind it is an implementation detail of how the server replicates one.
    void CaptureObjectState(Entity e, ref NetId id)
    {
        if (!World.TryGet(e, out TileObjectState state)) return;
        liveObjectStates.Add(state.ObjectId);
        // COLLECTED rather than raised here, SampleRemote's rule: this runs inside an ECS ForEach, and a handler
        // that spawned or despawned anything would be mutating the world mid iteration. An unchanged state is not
        // a change, which is what keeps the event once per change rather than once per snapshot.
        if (objectStates.TryGetValue(state.ObjectId, out int previous) && previous == state.State) return;
        objectStates[state.ObjectId] = state.State;
        changedObjectStates.Add((state.ObjectId, state.State));
    }
}
