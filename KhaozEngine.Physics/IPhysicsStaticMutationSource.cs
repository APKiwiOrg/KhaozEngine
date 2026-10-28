namespace KhaozEngine.Physics;

/// <summary>Optional exclusive static-edit intervals over a complete physics owner.</summary>
public interface IPhysicsStaticMutationSource : IPhysicsStaticGeometrySource
{
    /// <summary>Acquires a synchronous, thread-affine writer interval. Other readers and
    /// writers wait until disposal. Acquisition from a read interval, or a nested
    /// static-mutation lease, throws before any write. Acquiring the interval alone
    /// does not advance either geometry generation.</summary>
    IPhysicsStaticMutationLease AcquireStaticMutationLease();
}

/// <summary>An exclusive interval for composing source static edits and their metadata
/// publication. Source AddStatic and RemoveStatic calls on this thread remain available.
/// Explicit query read leases cannot begin inside the interval. This is an exclusion
/// guarantee, not automatic transaction rollback.</summary>
public interface IPhysicsStaticMutationLease : System.IDisposable
{
    /// <summary>The exact complete owner whose writer interval is held.</summary>
    IPhysicsWorld SourceWorld { get; }

    /// <summary>The physics origin captured when the interval was acquired.</summary>
    System.Numerics.Vector3 Origin { get; }

    /// <summary>The owner's current static revision inside this writer interval.
    /// Successful adds and removals of live statics each advance it exactly once.</summary>
    long StaticGeometryGeneration { get; }

    /// <summary>Refuses wrong-thread, disposed, no-longer-held or changed-origin use.
    /// Does not compare static revision with its acquisition value, since this
    /// interval deliberately admits static writes.</summary>
    void AssertCurrent();
}
