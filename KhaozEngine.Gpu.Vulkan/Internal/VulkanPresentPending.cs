using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE THREE THINGS THAT QUEUE A RECREATE, coalesced into one piece of state applied at the next present
    /// boundary (V-W6): a resize, a runtime present-mode change, and an <c>OUT_OF_DATE</c> or <c>SUBOPTIMAL</c>
    /// result from either <c>vkAcquireNextImageKHR</c> or <c>vkQueuePresentKHR</c>.
    ///
    /// <para><b>ALL THREE ARE THE SAME EVENT AS FAR AS VULKAN IS CONCERNED, which is why they share one flag.</b>
    /// A swapchain cannot be resized in place and cannot have its present mode changed in place, so every one of
    /// them is a full recreate of every object in a generation. Keeping three separate pending states would mean
    /// three paths that all end in the same call, and a boundary that could run two recreates in one frame.</para>
    ///
    /// <para><b>NOTHING HERE TAKES A LOCK OR MAKES A NATIVE CALL, which is what makes a foreign-thread resize
    /// safe.</b> <c>ResizeSwapchain</c> arrives from a window callback on whatever thread the platform runs
    /// callbacks on, possibly while the submit thread is inside <c>vkQueueSubmit</c>. It stores a number and
    /// returns. The submit thread applies it where it PROVABLY owns the queue and no recording is in flight, so a
    /// resize during recording becomes structurally impossible rather than contractually forbidden.</para>
    ///
    /// <para><b>THE SIZE IS COALESCED TO THE LAST REQUEST</b>, which is the whole reason a drag-resize is
    /// affordable: a burst of thirty size events between two presents costs ONE recreate rather than thirty. The
    /// exchange is atomic, so two threads racing leave one of the two sizes and never a mix of the halves.</para>
    /// </summary>
    internal sealed class VulkanPresentPending
    {
        // A packed size no window can be, so it doubles as "nothing queued" without a second field a writer would
        // have to publish in the right order. -1 is every one of the 64 bits set, so the one request that could
        // collide is a 4294967295 by 4294967295 pixel window.
        const long NothingPending = -1L;

        long _size = NothingPending;
        int _recreate;

        /// <summary>Whether anything is queued. For diagnostics and for the tests that pin the coalescing, never
        /// to decide anything: <see cref="Take"/> is what the boundary calls.</summary>
        internal bool HasWork => Interlocked.Read(ref _size) != NothingPending || Volatile.Read(ref _recreate) != 0;

        /// <summary>The size queued but not yet applied, or null when there is none. Diagnostic only.</summary>
        internal VulkanExtent? PendingSize
        {
            get
            {
                long packed = Interlocked.Read(ref _size);
                return packed == NothingPending ? null : Unpack(packed);
            }
        }

        /// <summary>
        /// Queue a resize, coalescing onto any earlier one. Takes no lock, makes no native call and never blocks,
        /// so a window callback on any thread is safe even while the submit thread holds the submit lock.
        /// </summary>
        internal void QueueResize(uint width, uint height) => Interlocked.Exchange(ref _size, Pack(width, height));

        /// <summary>
        /// Queue a recreate with no size change: a runtime <c>SyncToVerticalBlank</c> flip, or an
        /// <c>OUT_OF_DATE</c> or <c>SUBOPTIMAL</c> result. The extent then comes from the surface at apply time,
        /// which is the right answer for all three.
        /// </summary>
        internal void QueueRecreate() => Volatile.Write(ref _recreate, 1);

        /// <summary>
        /// TAKE EVERYTHING QUEUED AND CLEAR IT, atomically enough that a request arriving while the boundary runs
        /// is queued for the NEXT boundary rather than lost or half-applied.
        /// </summary>
        /// <param name="size">The coalesced size, or null when only a flag was set.</param>
        /// <returns>Whether a recreate is due at all.</returns>
        internal bool Take(out VulkanExtent? size)
        {
            long packed = Interlocked.Exchange(ref _size, NothingPending);
            bool flagged = Interlocked.Exchange(ref _recreate, 0) != 0;

            size = packed == NothingPending ? null : Unpack(packed);
            return flagged || size is not null;
        }

        // One long carries both halves so the queue is a single atomic exchange rather than two fields a reader
        // could catch mid-update. Width in the high half, height in the low half.
        static long Pack(uint width, uint height) => ((long)width << 32) | height;

        static VulkanExtent Unpack(long packed) => new((uint)(packed >> 32), (uint)packed);
    }
}
