namespace KhaozEngine.Sharding;

/// <summary>
/// Which consumer is asking <see cref="CellSim.SnapshotOwned(System.Collections.Generic.IReadOnlySet{long}, SnapshotPurpose)"/>
/// for a cell capture. The two consumers want the same codec and the same channel and differ in exactly one thing:
/// how far a <see cref="Transient"/> mark reaches (see <see cref="TransientScope"/>).
/// </summary>
public enum SnapshotPurpose
{
    /// <summary>
    /// The save. Bytes handed to an <c>IWorldStore</c> for a later process to read back, so anything in them
    /// outlives this process. Every <see cref="Transient"/> entity is left out whatever its
    /// <see cref="TransientScope"/>. The default, and what <see cref="CellSim.SnapshotOwned(System.Collections.Generic.IReadOnlySet{long})"/>
    /// captures.
    /// </summary>
    Durable = 0,

    /// <summary>
    /// The in-memory freeze <c>KhaozEngine.NetWorld.CellEvictor</c> holds while a cell is unloaded, so a coordinate
    /// that is re-entered restores inside the create call. A faithful freeze rather than a persistence decision, so
    /// only <see cref="TransientScope.Always"/> entities are left out and a
    /// <see cref="TransientScope.DurableOnly"/> one is captured and handed back on the route in.
    /// </summary>
    Eviction = 1,
}
