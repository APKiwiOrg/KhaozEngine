using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// Picks the completion timeline for a live device, ONCE, at device creation: the monotonic
    /// <c>ID3D11Fence</c> when the runtime offers <c>ID3D11Device5</c> and <c>ID3D11DeviceContext4</c>, and the
    /// event-query pool when it does not.
    /// <para>
    /// The decision is taken here and nowhere else, and nothing above the timeline may re-take it. Both
    /// mechanisms answer the same questions with the same guarantees, so
    /// <see cref="GpuCapabilities.SupportsCompletionFences"/> is true either way (decision C5) and a caller that
    /// branched on which one it got would be building a second, quieter fallback on top of the one that already
    /// works.
    /// </para>
    /// <para>
    /// WHERE THEY DIFFER, THEY SAY SO THEMSELVES. The drain is faster on the primary path, because a Direct3D
    /// 11.4 fence has a real blocking wait and an event query does not, and a fence poll takes no lock there,
    /// because <c>GetCompletedValue</c> is free-threaded and an immediate-context poll is not. Both differences
    /// are read from <see cref="ID3D11FenceTimeline.PollIsFreeThreaded"/> and
    /// <see cref="ID3D11FenceTimeline.TryWaitForValue"/>, which is the distinction that matters: a caller asks
    /// what this timeline CAN do, never which of the two it is. Nothing branches on
    /// <see cref="ID3D11FenceTimeline.Mechanism"/>, which stays a name for the session log.
    /// </para>
    /// <para>
    /// A FAILED QUERY IS NOT AN ERROR HERE. Asking a Direct3D 11.0 runtime for <c>ID3D11Device5</c> is a question
    /// with a legitimate no, so the fallback is taken silently and the mechanism reaches the session log through
    /// <see cref="ID3D11FenceTimeline.Mechanism"/> rather than through a warning about a machine that is behaving
    /// correctly.
    /// </para>
    /// </summary>
    internal static class D3D11FenceTimelines
    {
        /// <summary>
        /// The timeline for <paramref name="device"/> and its immediate context. Never null and never throws for
        /// a missing feature: the event-query fallback works on every device this backend can run on at all,
        /// because an event query is Direct3D 11.0 surface.
        /// <para>
        /// Both arguments are BORROWED. The returned timeline may take over other objects it obtains from them
        /// (the monotonic one does), but never these two.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static ID3D11FenceTimeline CreateWindows(
            Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11DeviceContext immediateContext)
        {
            if (device is null) throw new ArgumentNullException(nameof(device));
            if (immediateContext is null) throw new ArgumentNullException(nameof(immediateContext));

            ID3D11FenceTimeline? monotonic = TryCreateMonotonicWindows(device, immediateContext);
            return monotonic ?? new D3D11EventQueryTimeline(device, immediateContext);
        }

        // The 11.4 path, or null when this runtime does not have it. Every object obtained here is released on
        // the way out unless it is handed to the timeline, because a failed attempt that leaked an interface
        // reference would keep the device alive past its own disposal and the leak would present as a
        // DEVICE_REMOVED far from here (the exact shape of failure decision G3 exists to stop).
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static ID3D11FenceTimeline? TryCreateMonotonicWindows(
            Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11DeviceContext immediateContext)
        {
            Vortice.Direct3D11.ID3D11Device5? device5 = device.QueryInterfaceOrNull<Vortice.Direct3D11.ID3D11Device5>();
            Vortice.Direct3D11.ID3D11DeviceContext4? context4 =
                immediateContext.QueryInterfaceOrNull<Vortice.Direct3D11.ID3D11DeviceContext4>();

            try
            {
                if (device5 is null || context4 is null) return null;

                Vortice.Direct3D11.ID3D11Fence fence = device5.CreateFence<Vortice.Direct3D11.ID3D11Fence>(
                    0UL, Vortice.Direct3D11.FenceFlags.None);

                // The timeline owns the fence and the context reference from here on, so neither is released
                // below. Nulling the local is what keeps the finally block honest about that.
                var timeline = new D3D11MonotonicFenceTimeline(fence, context4);
                context4 = null;
                return timeline;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // CreateFence can refuse on a runtime that reports ID3D11Device5 without usable fence support
                // (WARP on some older Windows 10 builds is the documented case). That is exactly the situation
                // the fallback exists for, so it is a null rather than a throw.
                return null;
            }
            finally
            {
                device5?.Dispose();
                context4?.Dispose();
            }
        }
    }
}
