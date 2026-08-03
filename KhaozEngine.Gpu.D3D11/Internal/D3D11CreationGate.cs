using System;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION W4's CREATION CLAUSE: resource creation is free-threaded, serialized behind a short creation
    /// lock when the driver reports <c>DriverConcurrentCreates</c> false. This is that lock, and the decision of
    /// whether there is one at all.
    /// <para>
    /// THE SECOND LOCK IN THE BACKEND, AND THE ONLY ONE BESIDES THE SUBMIT LOCK. Everything else in the package
    /// runs on the device's single <c>_submitLock</c>, so the ordering rule below is short enough to hold in
    /// one's head, which is the point of there being exactly two.
    /// </para>
    /// <para>
    /// THE ORDERING RULE. The submit lock is the OUTER lock and this gate is the INNER one, and the gate is a
    /// STRICT LEAF: nothing is acquired while it is held, and nothing here waits on anything. Entering the gate
    /// while holding the submit lock is therefore legal (it is the one legal nesting, and no shipped path does
    /// it today), and taking the submit lock while inside the gate is NOT. The day a creation path needs the
    /// immediate context (an initial-data upload, a ring mapped at creation), it takes the submit lock BEFORE
    /// entering the gate rather than inside it. Written down because the two-lock inversion is the classic way a
    /// backend acquires a deadlock that only reproduces on someone else's machine, and because a leaf lock is
    /// the one shape that cannot be part of a cycle at all.
    /// </para>
    /// <para>
    /// AN UNKNOWN ANSWER SERIALIZES. <see cref="For"/> reads the threading probe's
    /// <see cref="GpuThreadingCaps.DriverConcurrentCreates"/>, and null caps (the probe did not run, or could not
    /// answer) take the serialized path. Unknown is not the same as yes: the probe is a diagnostic that degrades
    /// to "unknown" on every failure, so treating its silence as a licence to create on four threads at once is
    /// betting a driver's stability on a log line. The cost of being wrong in the safe direction is one
    /// uncontended monitor per resource creation, on a path that runs at load time.
    /// </para>
    /// <para>
    /// WHAT IS AND IS NOT GATED is the creating caller's decision rather than this type's, and
    /// <see cref="D3D11ResourceFactory"/> gates exactly the members that make a native creation call. A framebuffer,
    /// a resource layout, a resource set and a command list create no native object at all, so gating them would
    /// serialize pure engine work against a driver limitation that has nothing to do with it.
    /// </para>
    /// </summary>
    internal sealed class D3D11CreationGate
    {
        // Null IS the free-threaded answer, rather than a flag beside a lock that is then never taken. One field
        // means there is no state in which the two could disagree, and the null check is the same branch the
        // enter would need anyway.
        readonly object? _lock;

        /// <summary>
        /// Build the gate for a driver that does or does not create concurrently.
        /// </summary>
        /// <param name="driverConcurrentCreates"><c>D3D11_FEATURE_DATA_THREADING.DriverConcurrentCreates</c>.
        /// True leaves creation genuinely free-threaded with no lock anywhere.</param>
        internal D3D11CreationGate(bool driverConcurrentCreates)
            => _lock = driverConcurrentCreates ? null : new object();

        /// <summary>
        /// The gate for whatever the threading probe answered, including its silence. Null
        /// <paramref name="caps"/> serializes, for the reason on the type.
        /// </summary>
        internal static D3D11CreationGate For(GpuThreadingCaps? caps)
            => new(caps?.DriverConcurrentCreates ?? false);

        /// <summary>Whether creation is serialized on this driver. For the session log and for the tests that pin
        /// the two arms, never needed to decide anything: <see cref="Enter"/> answers it itself.</summary>
        internal bool Serializes => _lock is not null;

        /// <summary>Whether THIS thread is inside the gate right now. The assertion a test makes about a creation
        /// call, and always false on a free-threaded driver, where there is no lock to be inside of.</summary>
        internal bool IsEnteredByCurrentThread => _lock is not null && Monitor.IsEntered(_lock);

        /// <summary>
        /// Enter the gate for one creation. A no-op on a free-threaded driver, and a plain monitor otherwise.
        /// <para>
        /// The scope is a <c>ref struct</c>, so a <c>using</c> around a creation costs no allocation and the
        /// release cannot be forgotten or deferred to a finalizer. Hold it across ONE creation. It is re-entrant
        /// because a monitor is, which is what keeps a creation path that calls another one from deadlocking on
        /// itself.
        /// </para>
        /// </summary>
        internal Scope Enter() => new(_lock);

        /// <summary>One creation's turn at the gate, released by <c>using</c>. See <see cref="Enter"/>.</summary>
        internal readonly ref struct Scope
        {
            readonly object? _lock;

            internal Scope(object? gate)
            {
                _lock = gate;
                if (gate is not null) Monitor.Enter(gate);
            }

            /// <summary>Release the gate.</summary>
            public void Dispose()
            {
                if (_lock is not null) Monitor.Exit(_lock);
            }
        }
    }
}
