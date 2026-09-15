using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The durable half of <see cref="InstanceIdAllocator"/>, taken as a CONSTRUCTOR SEAM rather than an
/// ambient static. The host owns the implementation because a real one persists into the journal store,
/// and the engine ships the seam and the arithmetic and no provider, which is what keeps
/// <c>KhaozEngine.ItemInstances</c> in Foundation with no reference to a server package.
/// </summary>
/// <remarks>
/// Neither member is async. The allocator persists once per 4,096 ids on the thread that asked for one,
/// and a host whose store is async owns that bridge, because making the seam async would push an await
/// into every call site that wants an id.
/// </remarks>
public interface IInstanceIdStore
{
    /// <summary>What a boot reads: the packed high-water mark, the node id, the store epoch that mark was
    /// persisted under, and the retired node list. A store that has never been written answers
    /// <c>default</c>, which the allocator reads as node 0, counter 1, nothing retired and NO epoch to
    /// compare, because the epoch binds a mark and there is no mark yet.</summary>
    InstanceIdState Read();

    /// <summary>Writes the state. Called BEFORE any id in the newly reserved block is issued, which is the
    /// contract rather than the batch size (contracts 6.2): persisting AFTER issuing leaves a window in
    /// which a crash hands the next boot an id it has already put on an item.</summary>
    /// <param name="state">The state to make durable. The call returns only once it is.</param>
    void Persist(in InstanceIdState state);
}

/// <summary>
/// The allocator's whole durable record, spec 3.6. All four fields travel together, which is the point:
/// a point-in-time restore rolls the high-water mark, the node id AND the retired list back as one, so
/// only <see cref="StoreEpoch"/> can tell a restore apart from an ordinary boot.
/// </summary>
/// <param name="PackedHighWater">The packed id one past the highest RESERVED counter, so a boot resumes
/// at it and the unissued remainder of the previous block is skipped rather than reissued.</param>
/// <param name="NodeId">The node the high-water mark belongs to. Every id this allocator hands out
/// carries it in the high 16 bits.</param>
/// <param name="StoreEpoch">The journal store epoch the mark was persisted under. The allocator refuses
/// to issue when the live epoch differs.</param>
/// <param name="RetiredNodes">Every node id this store has already used and rotated away from. A boot on
/// one of them throws.</param>
public readonly record struct InstanceIdState(
    long PackedHighWater,
    ushort NodeId,
    long StoreEpoch,
    ReadOnlyMemory<ushort> RetiredNodes);
