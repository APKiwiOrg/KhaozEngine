namespace KhaozEngine.Physics;

/// <summary>Optional capability declaring that this exact receiver supports simultaneous reader intervals
/// and simultaneous query calls from different threads. It adds no members and leaves
/// <see cref="IPhysicsQueryLeaseSource"/> unchanged, so an undeclared receiver stays supported serially.
/// <para>A declaring receiver keeps these rules. Read leases remain thread-affine: a second lease on the
/// same thread throws and leaves the first intact, and wrong-thread use or disposal is rejected before any
/// wait. The outermost read on a thread admits one reader interval, and every nested read on that thread,
/// including callbacks raised during a query, reuses it without waiting, even behind a queued writer.
/// Mutations and disposal are exclusive writers that run in arrival order once every admitted reader
/// interval has ended, and new readers wait behind a queued writer. A mutation or disposal on a thread that
/// holds a read interval throws before the geometry generation changes. A lease requested inside a writer
/// interval throws.</para>
/// <para>A read callback must not synchronously wait on another thread entering the same receiver or on a
/// writer completing, exactly as an exclusive receiver already forbids. Readers share no mutable state:
/// each interval borrows private query storage.</para>
/// <para>A forwarding wrapper declares this only after auditing its own state. A receiver's declaration
/// never covers a wrapper over it, and a wrapper's never covers the receiver it forwards to.</para></summary>
public interface IPhysicsConcurrentReadSource
{
}
