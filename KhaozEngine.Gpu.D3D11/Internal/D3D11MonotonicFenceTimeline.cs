using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

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
    /// <para>Not thread-safe. Every member is called under the device's submit lock (decision W4), which is what
    /// makes the unsynchronised counter increment below correct.</para>
    /// </summary>
    internal sealed class D3D11MonotonicFenceTimeline : ID3D11FenceTimeline
    {
        readonly Vortice.Direct3D11.ID3D11Fence _fence;
        readonly Vortice.Direct3D11.ID3D11DeviceContext4 _context;

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
        void DisposeWindows()
        {
            _context.Dispose();
            _fence.Dispose();
        }
    }
}
