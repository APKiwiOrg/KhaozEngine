using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Optional capability for a stable, synchronous read interval over a live physics owner.</summary>
public interface IPhysicsQueryLeaseSource
{
    /// <summary>Acquires an exclusive, thread-affine read lease. Queries on the acquiring thread remain
    /// available. Mutations on that thread and nested leases are refused until disposal.</summary>
    IPhysicsQueryLease AcquireQueryReadLease();
}

/// <summary>A live read interval. Dispose on the acquiring thread after publishing pure query results,
/// before any resulting physics mutation. Captured metadata is not a replacement for AssertCurrent.</summary>
public interface IPhysicsQueryLease : IDisposable
{
    /// <summary>The exact logical owner, including when acquisition was through a restricted view.</summary>
    IPhysicsWorld SourceWorld { get; }

    /// <summary>The owner's captured coordinate origin, in world metres.</summary>
    Vector3 Origin { get; }

    /// <summary>The captured process-local mutation generation, not a portable world or bake identity.</summary>
    long GeometryGeneration { get; }

    /// <summary>Refuses an expired or wrong-thread lease and any changed owner generation or origin.</summary>
    void AssertCurrent();
}
