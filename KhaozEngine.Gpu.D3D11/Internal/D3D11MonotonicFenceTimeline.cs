using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE PRIMARY COMPLETION TIMELINE (decision C5): one device-wide <c>ID3D11Fence</c>, created through
    /// <c>ID3D11Device5.CreateFence</c> and advanced with <c>ID3D11DeviceContext4.Signal</c>. This is the whole
    /// mechanism, and it is a direct fit rather than an emulation: the Direct3D 11.4 fence IS a monotonic counter
    /// the GPU raises, and <c>GetCompletedValue</c> IS the non-blocking read the seam asks for.
    /// <para>
    /// AVAILABILITY. <c>ID3D11Device5</c> arrived in Windows 10 1703, so a machine older than that gets
    /// <see cref="D3D11EventQueryTimeline"/> instead and everything above the timeline is unchanged.
    /// <see cref="D3D11FenceTimelines"/> takes that decision once, at device creation.
    /// </para>
    /// <para>
    /// OWNERSHIP. This type owns both objects it is handed and releases them in <see cref="Dispose"/>. The
    /// context is an <c>ID3D11DeviceContext4</c> wrapper obtained by querying the device's immediate context,
    /// which is a separate reference on the same underlying object rather than a second context, so releasing it
    /// here does not touch the device's own.
    /// </para>
    /// <para>
    /// THIS IS THE MECHANISM THAT MAKES THE DRAIN CHEAP. A Direct3D 11.4 fence carries a real blocking wait
    /// (<c>SetEventOnCompletion</c> plus a wait handle), so a drain on this path sleeps in the kernel until the
    /// GPU raises the counter and wakes with no granularity cost at all. The fallback has nothing of the sort and
    /// spins. Both are correct, and this one is what the M2 per-frame budget was written against.
    /// </para>
    /// <para>The counter increment below is unsynchronised, which is correct because <see cref="Signal"/> is
    /// called under the device's submit lock (decision W4). The two members that are NOT called under it,
    /// <see cref="CompletedValue"/> and <see cref="TryWaitForValue"/>, touch only the fence object, whose own
    /// members are free-threaded in the same way the device's are. The context is the thing that is not, and
    /// neither of those two touches it.</para>
    /// </summary>
    internal sealed class D3D11MonotonicFenceTimeline : ID3D11FenceTimeline
    {
        readonly Vortice.Direct3D11.ID3D11Fence _fence;
        readonly Vortice.Direct3D11.ID3D11DeviceContext4 _context;

        // ONE event for the life of the timeline, not one per wait. A registration made by SetEventOnCompletion
        // outlives a wait that timed out, so closing the handle per wait would leave the runtime holding a handle
        // it may still set, and Windows recycles handle values. The cost of sharing it is at most a spurious
        // wakeup when two threads drain at once, which the drain loop absorbs because it re-polls after every
        // wakeup and never trusts the wait's own answer.
        readonly ManualResetEvent _reached = new(false);

        // The last value handed out. The GPU-side counter starts at 0 and the first signal is 1, so a fence
        // holding a target of 0 could never be satisfied by accident, which is what makes 0 usable as "no target".
        ulong _issued;

        /// <summary>Build a timeline over a fence created at initial value 0 and the context that will signal it.
        /// Both are taken over, not borrowed.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal D3D11MonotonicFenceTimeline(
            Vortice.Direct3D11.ID3D11Fence fence, Vortice.Direct3D11.ID3D11DeviceContext4 context)
        {
            _fence = fence ?? throw new ArgumentNullException(nameof(fence));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <inheritdoc/>
        public D3D11FenceMechanism Mechanism => D3D11FenceMechanism.MonotonicFence;

        /// <inheritdoc/>
        /// <remarks>True. <c>GetCompletedValue</c> is a read on the fence object and never touches the immediate
        /// context, so a poll on this mechanism does not have to be serialised with submission.</remarks>
        public bool PollIsFreeThreaded => true;

        /// <inheritdoc/>
        public ulong Signal()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

            ulong value = _issued + 1;
            SignalWindows(value);
            // Advanced only after the native call, so a signal that threw leaves the counter where it was and the
            // next one reuses the value instead of stranding it. A stranded value is not fatal (nothing waits on
            // a value that was never signalled unless a fence was armed with it), but a fence armed with a value
            // the GPU will never reach reads unsignalled forever, which presents as a resource pool that stops
            // freeing rather than as an error.
            _issued = value;
            return value;
        }

        /// <inheritdoc/>
        public void Flush()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

            FlushWindows();
        }

        /// <inheritdoc/>
        public ulong CompletedValue
        {
            get
            {
                if (!KhaozEngineD3D11.IsPlatformSupported)
                    throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

                return CompletedWindows();
            }
        }

        /// <inheritdoc/>
        public bool TryWaitForValue(ulong value, int timeoutMilliseconds)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

            WaitWindows(value, timeoutMilliseconds);
            return true;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) DisposeWindows();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void SignalWindows(ulong value) => _context.Signal(_fence, value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        ulong CompletedWindows() => _fence.CompletedValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void FlushWindows() => _context.Flush();

        // The blocking wait Direct3D 11.4 provides, and the reason the drain has no sleep in it: the runtime sets
        // the event the moment the fence reaches the value, so the wakeup costs a kernel transition rather than a
        // scheduler quantum. Arming AFTER the caller has already polled is not a lost wakeup, because a fence
        // that is already at the value sets the event immediately.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void WaitWindows(ulong value, int timeoutMilliseconds)
        {
            _reached.Reset();
            _fence.SetEventOnCompletion(value, _reached.SafeWaitHandle.DangerousGetHandle());
            _reached.WaitOne(timeoutMilliseconds);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void DisposeWindows()
        {
            _context.Dispose();
            // The fence is released BEFORE the event, deliberately. Releasing it drops any wait registration
            // still outstanding on it, so nothing can set the handle after it has been closed.
            _fence.Dispose();
            _reached.Dispose();
        }
    }
}
