using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// DECISION M-N5 AS A TYPE: an autorelease pool with a scope, so the rule "every public entry point which can
    /// create an autoreleased object wraps its body" is something a call site SPELLS rather than something a
    /// reviewer remembers.
    /// <para>
    /// WHY IT IS A RULE AND NOT A HABIT. Metal's factory methods return autoreleased objects: <c>-commandBuffer</c>,
    /// <c>-renderCommandEncoderWithDescriptor:</c>, <c>-name</c>, every descriptor. Off the main thread, or on any
    /// thread whose implicit pool drains when the run loop next turns, "next drain" under a frame loop means
    /// never. The incumbent Veldrid Metal backend wrapped FOUR sites and did not wrap others, which is exactly the
    /// shape that accumulates, and that is the observation this decision is made against.
    /// </para>
    /// <para>
    /// IT IS A <c>ref struct</c>, DELIBERATELY. A pool is a thread-local stack discipline: the pop must happen on
    /// the same thread as the push and in the reverse order of nesting. A <c>ref struct</c> cannot be boxed,
    /// cannot be captured by a lambda, cannot be stored in a field and cannot cross an <c>await</c>, so the four
    /// ways a scope could outlive its own frame are compile errors rather than a leak or a pop against another
    /// thread's stack.
    /// </para>
    /// <para>
    /// THE ARCHITECTURE TEST READS THIS TYPE BY NAME. <c>MetalAutoreleaseArchitectureTests</c> walks the IL of
    /// every entry point in the package and requires that no path from one reaches an <c>objc_msgSend</c> without
    /// passing through a method that calls <see cref="Enter"/>. That is why the pool is a named type and not an
    /// inline push and pop pair: a walk can see a call, and it cannot see a discipline.
    /// </para>
    /// </summary>
    internal readonly ref struct ObjCAutoreleasePool
    {
        readonly IntPtr _pool;

        ObjCAutoreleasePool(IntPtr pool) => _pool = pool;

        /// <summary>
        /// Push a pool and hand back the scope that pops it. Always used as
        /// <c>using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();</c> at the TOP of the body, so the pop
        /// happens on every exit including a throw.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ObjCAutoreleasePool Enter() => new(ObjCRuntime.AutoreleasePoolPush());

        /// <summary>
        /// Pop the pool, releasing everything autoreleased inside the scope.
        /// <para>
        /// Popping a pool pops every pool pushed after it too, which is the runtime's own behaviour and is why
        /// the <c>ref struct</c> above matters: a scope that escaped its frame and popped late would drain
        /// objects a caller further up is still using.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose() => ObjCRuntime.AutoreleasePoolPop(_pool);
    }
}
