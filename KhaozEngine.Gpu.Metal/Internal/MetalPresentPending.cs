using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TWO THINGS THAT QUEUE WORK FOR THE NEXT PRESENT BOUNDARY (M-W7): a resize, and a runtime
    /// <c>SyncToVerticalBlank</c> change. Both are stored here and applied by the boundary on the submit thread.
    ///
    /// <para><b>THEY ARE TWO PIECES OF STATE RATHER THAN ONE, WHICH IS THE DIFFERENCE FROM THE VULKAN
    /// SIBLING.</b> There a resize and a present-mode change are the SAME event, because a <c>VkSwapchainKHR</c>
    /// can have neither changed in place, so both end in one full recreate and one flag is right. Metal needs no
    /// swapchain recreation at all: a resize is a <c>drawableSize</c> write and a vsync change is a
    /// <c>displaySyncEnabled</c> write, two independent properties of one layer. Folding them would make a plain
    /// vsync toggle rewrite the drawable size, which is a resize nobody asked for.</para>
    ///
    /// <para><b>NOTHING HERE TAKES A LOCK OR MAKES A NATIVE CALL, and that is what makes a foreign-thread resize
    /// safe.</b> <c>ResizeSwapchain</c> arrives from a window callback on whatever thread the platform runs
    /// callbacks on, possibly while the submit thread is inside a commit. It stores a number and returns. The
    /// submit thread applies it where it provably owns the queue and no recording is in flight, which is what
    /// turns the incumbent's inline resize (<c>MTLSwapchain.Resize</c> writes the layer and takes a fresh drawable
    /// on the CALLING thread with no drain anywhere) into something structurally safe rather than safe by
    /// accident of which thread Silk happens to raise the callback on.</para>
    ///
    /// <para><b>THE SIZE IS COALESCED TO THE LAST REQUEST.</b> A burst of thirty size events between two presents
    /// costs ONE apply. The exchange is atomic, so two threads racing leave one of the two sizes and never a mix
    /// of the halves.</para>
    ///
    /// <para><b>A VSYNC CHANGE IS COALESCED TO THE LAST REQUEST TOO, AND A REDUNDANT ONE IS STILL QUEUED.</b> The
    /// incumbent's setter compares against its own field first and does nothing when they match. That comparison
    /// is not reproduced, because on the incumbent it guards a write that is itself conditional on a deprecated
    /// enum (M-W2), and here the write is unconditional and cheap. Queueing a redundant flip costs one boolean
    /// store and one property write per boundary, and it removes the state this type would otherwise have to keep
    /// in step with the layer's.</para>
    /// </summary>
    internal sealed class MetalPresentPending
    {
        // A packed size no window can be, so it doubles as "nothing queued" without a second field a writer would
        // have to publish in the right order. -1 is every one of the 64 bits set, so the one request that could
        // collide is a 4294967295 by 4294967295 pixel window.
        const long NothingPending = -1L;

        // Three states in one int, because a bool plus a "was it set" bool is two fields a reader can catch
        // between.
        const int NoVsyncChange = -1;

        long _size = NothingPending;
        int _vsync = NoVsyncChange;

        /// <summary>Whether anything is queued. For diagnostics and for the tests that pin the coalescing, never
        /// to decide anything: <see cref="Take"/> is what the boundary calls.</summary>
        internal bool HasWork
            => Interlocked.Read(ref _size) != NothingPending || Volatile.Read(ref _vsync) != NoVsyncChange;

        /// <summary>The size queued but not yet applied, or null when there is none. Diagnostic only.</summary>
        internal MetalDrawableSize? PendingSize
        {
            get
            {
                long packed = Interlocked.Read(ref _size);
                return packed == NothingPending ? null : Unpack(packed);
            }
        }

        /// <summary>The vsync value queued but not yet applied, or null when there is none. Diagnostic only.
        /// </summary>
        internal bool? PendingSyncToVerticalBlank
        {
            get
            {
                int vsync = Volatile.Read(ref _vsync);
                return vsync == NoVsyncChange ? null : vsync != 0;
            }
        }

        /// <summary>
        /// Queue a resize, coalescing onto any earlier one. Takes no lock, makes no native call and never blocks,
        /// so a window callback on any thread is safe even while the submit thread holds the submit lock.
        /// </summary>
        internal void QueueResize(uint width, uint height)
            => Interlocked.Exchange(ref _size, Pack(width, height));

        /// <summary>Queue a runtime vsync change, coalescing onto any earlier one.</summary>
        internal void QueueSyncToVerticalBlank(bool value) => Volatile.Write(ref _vsync, value ? 1 : 0);

        /// <summary>
        /// TAKE EVERYTHING QUEUED AND CLEAR IT, atomically enough that a request arriving while the boundary runs
        /// is queued for the NEXT boundary rather than lost or half-applied.
        /// </summary>
        /// <param name="size">The coalesced size, or null when no resize was queued.</param>
        /// <param name="syncToVerticalBlank">The coalesced vsync value, or null when no change was queued.</param>
        /// <returns>Whether anything at all is due, which is what decides the boundary's drain: a boundary with
        /// nothing queued must not pay for one.</returns>
        internal bool Take(out MetalDrawableSize? size, out bool? syncToVerticalBlank)
        {
            long packed = Interlocked.Exchange(ref _size, NothingPending);
            int vsync = Interlocked.Exchange(ref _vsync, NoVsyncChange);

            size = packed == NothingPending ? null : Unpack(packed);
            syncToVerticalBlank = vsync == NoVsyncChange ? null : vsync != 0;
            return size is not null || syncToVerticalBlank is not null;
        }

        // One long carries both halves so the queue is a single atomic exchange rather than two fields a reader
        // could catch mid-update. Width in the high half, height in the low half.
        static long Pack(uint width, uint height) => ((long)width << 32) | height;

        static MetalDrawableSize Unpack(long packed) => new((uint)(packed >> 32), (uint)packed);
    }
}
