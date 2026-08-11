using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE FAKE <see cref="IMetalSwapchainApi"/> EVERY DEVICE-FREE SWAPCHAIN ROW DRIVES, and the thing that makes
    /// M-W4 to M-W7 assertable at all. MM7 records that the incumbent's swapchain has ZERO automated coverage
    /// anywhere in the net, and the reason is that a headless runner has no window and no display. This fake
    /// removes the display from the question: what is left is the ORDER of a present boundary, the skipped
    /// present, the orphan target, the counters and the coalescing, and every one of those is a decision.
    ///
    /// <para><b>IT KEEPS AN ORDERED LOG rather than only per-call counters</b>, because half of what row 15 has to
    /// get right IS an order: present before apply, drain before the drawable-size write, capture after the
    /// present, acquire last and outside the lock. A count cannot fail on a transposition and a log can.</para>
    /// </summary>
    internal sealed class FakeMetalSwapchainApi : IMetalSwapchainApi
    {
        readonly Queue<MetalAcquiredDrawable> _scripted = new();
        int _nextHandle = 1000;

        /// <summary>Everything that happened, in order, across the api, the drain and the capture hook. The drain
        /// and the capture append through <see cref="Note"/>.</summary>
        internal List<string> Log { get; } = new();

        internal MetalDrawableSize ConfiguredSize { get; private set; }
        internal bool ConfiguredSrgb { get; private set; }
        internal bool ConfiguredSync { get; private set; }
        internal int ConfiguredMaximumDrawableCount { get; private set; } = -1;

        internal MetalDrawableSize LastDrawableSize { get; private set; }
        internal bool? LastDisplaySync { get; private set; }

        /// <summary>Every acquire's answer, in order, so a row can assert the RELATION between what was handed
        /// out and what was presented rather than encoding this fake's handle arithmetic.</summary>
        internal List<MetalAcquiredDrawable> Handed { get; } = new();

        internal List<IntPtr> Presented { get; } = new();

        /// <summary>The buffer each present was committed on, so a row can assert the present rode the buffer
        /// the boundary took OUTSIDE the lock rather than one taken inside it.</summary>
        internal List<IntPtr> PresentedOn { get; } = new();

        /// <summary>Every buffer handed out for a present, in order.</summary>
        internal List<IntPtr> PresentBuffers { get; } = new();

        /// <summary>Script the queue to refuse a present buffer, which on a device is a queue already in
        /// trouble.</summary>
        internal bool PresentBufferIsRefused { get; set; }

        internal List<IntPtr> Released { get; } = new();
        internal bool IsDisposed { get; private set; }
        internal int AcquireCount { get; private set; }

        /// <summary>
        /// THE SUBMIT LOCK THIS FAKE WATCHES, or null to watch nothing. M-W8's headline claim is that the
        /// drawable acquire holds NO lock, and the only way to assert that from underneath is to ask, at the
        /// moment of the call, whether the caller is inside it. The harness hands over the same object it gave
        /// the boundary.
        /// </summary>
        internal object? LockToWatch { get; set; }

        /// <summary>Whether any <see cref="NextDrawable"/> ran with <see cref="LockToWatch"/> held. M-W8 says
        /// this is false forever.</summary>
        internal bool AcquireSawLockHeld { get; private set; }

        /// <summary>Whether any <see cref="AcquirePresentBuffer"/> ran with <see cref="LockToWatch"/> held. Also
        /// false, and it is the OTHER blocking call the lock ordering keeps outside.</summary>
        internal bool PresentBufferSawLockHeld { get; private set; }

        /// <summary>Whether any <see cref="PresentDrawable"/> ran with <see cref="LockToWatch"/> held. TRUE, and
        /// it is the positive control that says this fake can see the lock at all.</summary>
        internal bool PresentSawLockHeld { get; private set; }

        /// <summary>How long the next acquire pretends to take, so the acquire-wait MILLISECONDS can be driven off
        /// zero rather than only the count. Real time, because <see cref="WaitAccumulator"/> reads a stopwatch
        /// around the call and there is nothing to inject.</summary>
        internal TimeSpan AcquireDelay { get; set; }

        /// <summary>Script the next acquire to answer with a drawable.</summary>
        internal void ScriptDrawable()
        {
            IntPtr drawable = new(_nextHandle++);
            IntPtr texture = new(_nextHandle++);
            _scripted.Enqueue(new MetalAcquiredDrawable(drawable, texture));
        }

        /// <summary>Script the next acquire to answer NIL, which is M-W5's whole condition.</summary>
        internal void ScriptNoDrawable() => _scripted.Enqueue(default);

        /// <summary>Append a note from outside the api, so the drain and the capture hook land in the same
        /// ordered log the native calls do.</summary>
        internal void Note(string what) => Log.Add(what);

        public void Configure(MetalDrawableSize size, bool colourSrgb, bool syncToVerticalBlank,
            int maximumDrawableCount)
        {
            ConfiguredSize = size;
            ConfiguredSrgb = colourSrgb;
            ConfiguredSync = syncToVerticalBlank;
            ConfiguredMaximumDrawableCount = maximumDrawableCount;
            LastDrawableSize = size;
            Log.Add("configure");
        }

        public void SetDrawableSize(MetalDrawableSize size)
        {
            LastDrawableSize = size;
            Log.Add($"drawableSize={size.Width}x{size.Height}");
        }

        public void SetDisplaySyncEnabled(bool enabled)
        {
            LastDisplaySync = enabled;
            Log.Add($"displaySync={enabled}");
        }

        public MetalAcquiredDrawable NextDrawable()
        {
            // M-W8's HEADLINE CLAIM, ASKED AT THE ONE MOMENT IT MEANS ANYTHING. -nextDrawable blocks for the
            // length of a display refresh, so an acquire made while the submit lock is held stops every other
            // thread's submit for a frame. Nothing above this line can see that, which is why the fake asks.
            if (LockToWatch is not null && Monitor.IsEntered(LockToWatch)) AcquireSawLockHeld = true;

            AcquireCount++;
            Log.Add("acquire");

            // A REAL SLEEP rather than an injected clock, because the accumulator times the call with a
            // stopwatch and has nothing to inject. Zero by default, so only the rows that assert MILLISECONDS pay
            // for it.
            if (AcquireDelay > TimeSpan.Zero) System.Threading.Thread.Sleep(AcquireDelay);

            // An unscripted acquire answers NIL rather than throwing, so a row that only cares about the first
            // few boundaries does not have to script every one of them.
            MetalAcquiredDrawable answer = _scripted.Count > 0 ? _scripted.Dequeue() : default;
            Handed.Add(answer);
            return answer;
        }

        public void ReleaseDrawable(IntPtr drawable)
        {
            Released.Add(drawable);
            Log.Add("releaseDrawable");
        }

        public IntPtr AcquirePresentBuffer()
        {
            // WATCHED FOR THE SAME REASON THE ACQUIRE IS: -commandBuffer blocks at the queue's cap and every
            // commit that could clear it needs the submit lock, so this one being inside the lock is a deadlock
            // rather than a stall.
            if (LockToWatch is not null && Monitor.IsEntered(LockToWatch)) PresentBufferSawLockHeld = true;

            Log.Add("acquirePresentBuffer");
            if (PresentBufferIsRefused) return IntPtr.Zero;

            IntPtr buffer = new(_nextHandle++);
            PresentBuffers.Add(buffer);
            return buffer;
        }

        public void PresentDrawable(IntPtr commandBuffer, IntPtr drawable)
        {
            // THE POSITIVE CONTROL. The commit is what the submit lock exists to order (M-N2), so this one is
            // expected to see it held, and a fake that could never see the lock at all would pass the two
            // negative rows above for the wrong reason.
            if (LockToWatch is not null && Monitor.IsEntered(LockToWatch)) PresentSawLockHeld = true;

            Presented.Add(drawable);
            PresentedOn.Add(commandBuffer);
            Log.Add("present");
        }

        public void Dispose()
        {
            IsDisposed = true;
            Log.Add("disposeApi");
        }
    }

    /// <summary>
    /// THE FAKE <see cref="IMetalOrphanTarget"/>: a handle and a tally. What the boundary has to get right about
    /// M-W5's target is WHEN it is created and WHEN it is destroyed, and neither needs a texture.
    /// </summary>
    internal sealed class FakeMetalOrphanTarget : IMetalOrphanTarget
    {
        int _nextHandle = 9000;

        /// <param name="log">The api's ordered log, when a row needs the orphan's calls interleaved with the
        /// native ones. The publish-before-release rule is an ORDER between two objects, so it can only be
        /// asserted in one sequence.</param>
        internal FakeMetalOrphanTarget(List<string>? log = null) => Log = log ?? new List<string>();

        internal List<string> Log { get; }
        internal int EnsureCount { get; private set; }
        internal int ReleaseCount { get; private set; }
        internal MetalDrawableSize LastSize { get; private set; }
        internal GpuPixelFormat LastFormat { get; private set; }
        internal bool IsLive { get; private set; }
        internal IntPtr Handle { get; private set; }

        /// <summary>
        /// Make <see cref="Release"/> a no-op that still logs, which is the DEAD-DEVICE shape rather than a
        /// convenience. The real orphan target is an ordinary engine <c>MTLTexture</c> and
        /// <c>MetalTexture.Dispose</c> returns early once the device's liveness token is dead, so on a teardown
        /// whose drain faulted the release runs and frees nothing.
        /// </summary>
        internal bool ReleaseIsInert { get; set; }

        public MetalAttachment Ensure(MetalDrawableSize size, GpuPixelFormat format)
        {
            EnsureCount++;
            LastSize = size;
            LastFormat = format;
            Log.Add($"ensure={size.Width}x{size.Height}");

            if (!IsLive || Handle == IntPtr.Zero) Handle = new IntPtr(_nextHandle++);
            IsLive = true;
            return new MetalAttachment(Handle, format);
        }

        public void Release()
        {
            ReleaseCount++;
            Log.Add("orphanRelease");
            if (!ReleaseIsInert) IsLive = false;
        }
    }
}
