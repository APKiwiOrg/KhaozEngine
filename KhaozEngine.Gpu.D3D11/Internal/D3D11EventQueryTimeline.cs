using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE FALLBACK COMPLETION TIMELINE (decision C5), for a runtime with no <c>ID3D11Device5</c>, which means
    /// anything older than Windows 10 1703. An <c>ID3D11Query</c> of type <c>Event</c> is Direct3D 11's original
    /// "has the GPU got here yet" object: end one at a point in the stream, then ask for its data, and the answer
    /// is false until the GPU has drained past that point.
    /// <para>
    /// TURNING ONE-SHOT MARKERS INTO A COUNTER. The seam wants one monotonic number, and an event query is a
    /// boolean about a single point, so this type keeps the in-flight markers in issue order and advances the
    /// counter as the oldest one completes. All of that ordering lives in
    /// <see cref="D3D11EventTimelineQueue"/>, which is device-free and tested on every operating system. What is
    /// here is the four native calls it cannot make.
    /// </para>
    /// <para>
    /// <c>DO_NOT_FLUSH</c> IS THE WHOLE POINT of the poll. Without it, asking a query for data flushes the
    /// context, which turns a poll into a submission and makes the non-blocking read the seam demands into
    /// something with a cost that grows with how often you look. With it the call reads the state and returns.
    /// </para>
    /// <para>
    /// <see cref="Signal"/> POLLS BEFORE IT ISSUES, which is what bounds the query pool. Retiring on the way in
    /// means a steady-state session holds about as many query objects as it has submissions in flight, without
    /// needing anyone to poll it on a schedule. A consumer that only ever signals and never reads
    /// <see cref="CompletedValue"/> is the case that would otherwise grow forever.
    /// </para>
    /// <para>
    /// OWNERSHIP. The device and the immediate context are BORROWED (the device owns them and outlives this), and
    /// the query objects are owned here. That is the opposite of
    /// <see cref="D3D11MonotonicFenceTimeline"/>, which owns the context wrapper it was handed, because that one
    /// is a separate reference obtained by a query rather than the device's own.
    /// </para>
    /// <para>Not thread-safe, and the poll below is on the IMMEDIATE context, so this one could not be made so by
    /// adding a lock inside it. Every member is called under the device's submit lock (decision W4).</para>
    /// </summary>
    internal sealed class D3D11EventQueryTimeline : ID3D11FenceTimeline
    {
        readonly Vortice.Direct3D11.ID3D11Device _device;
        readonly Vortice.Direct3D11.ID3D11DeviceContext _context;
        readonly D3D11EventTimelineQueue _queue = new();

        /// <summary>Build a timeline over a borrowed device and its borrowed immediate context. Neither is
        /// released by <see cref="Dispose"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal D3D11EventQueryTimeline(
            Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11DeviceContext immediateContext)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _context = immediateContext ?? throw new ArgumentNullException(nameof(immediateContext));
        }

        /// <inheritdoc/>
        public D3D11FenceMechanism Mechanism => D3D11FenceMechanism.EventQuery;

        /// <summary>How many query objects are in flight. Test and diagnostic surface for the pool bound the
        /// class note describes.</summary>
        internal int PendingCount => _queue.PendingCount;

        /// <inheritdoc/>
        public ulong Signal()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

            PollWindows();
            object marker = _queue.Rent() ?? CreateMarkerWindows();
            PlaceMarkerWindows(marker);
            return _queue.Enqueue(marker);
        }

        /// <inheritdoc/>
        public ulong CompletedValue
        {
            get
            {
                if (!KhaozEngineD3D11.IsPlatformSupported)
                    throw D3D11PlatformGuard.NotOnThisPlatform("fence timeline");

                PollWindows();
                return _queue.Completed;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) DisposeWindows();
        }

        // Drain the front of the queue while the oldest marker reports done. See the ordering note on
        // D3D11EventTimelineQueue for why the front is the only marker worth asking about.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void PollWindows()
        {
            while (_queue.TryPeekOldest(out object marker) && IsCompleteWindows(marker)) _queue.RetireOldest();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        object CreateMarkerWindows()
            => _device.CreateQuery(new Vortice.Direct3D11.QueryDescription(
                Vortice.Direct3D11.QueryType.Event, Vortice.Direct3D11.QueryFlags.None));

        // End and not Begin: an event query has no duration to bracket, it marks the single point the GPU has to
        // reach. Beginning one is an error the runtime reports through the debug layer and nothing else.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void PlaceMarkerWindows(object marker) => _context.End((Vortice.Direct3D11.ID3D11Query)marker);

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        bool IsCompleteWindows(object marker)
            => _context.IsDataAvailable((Vortice.Direct3D11.ID3D11Query)marker,
                Vortice.Direct3D11.AsyncGetDataFlags.DoNotFlush);

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void DisposeWindows()
        {
            foreach (object marker in _queue.TakeEveryMarker()) ((Vortice.Direct3D11.ID3D11Query)marker).Dispose();
        }
    }
}
