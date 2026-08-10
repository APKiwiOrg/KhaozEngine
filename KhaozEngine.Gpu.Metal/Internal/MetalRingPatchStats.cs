namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE OFF-TIMELINE WRITE'S DEFERRAL COUNTERS, cumulative since the device was created: how many segment
    /// writes were queued as a pending patch instead of copied on the spot, and what became of each one. Every
    /// deferral leaves the queue exactly once, applied, coalesced or dropped with its ring, which is what makes
    /// <see cref="Outstanding"/> a READING rather than a running total.
    /// <para>
    /// IT COUNTS PATCHES RATHER THAN WAITS, because there are no waits on this path to count.
    /// <see cref="MetalRingAllocator.UpdateBuffer"/> never blocks, so a milliseconds field here would report zero
    /// forever and read as "the waits are cheap" rather than as "there are none".
    /// </para>
    /// <para>
    /// SEPARATE FROM <see cref="MetalBackpressure"/> ON PURPOSE. A deferred patch is not a stall: it says a
    /// caller wrote a uniform buffer off-timeline while an earlier frame was still reading a segment of it, which
    /// costs nobody a wait. Folding it in would turn a load-time write into evidence against
    /// <c>KE_METAL_FRAMES_IN_FLIGHT</c> and make MM4's zero-stall exit criterion unreachable for a reason
    /// unrelated to depth. That reasoning is the Vulkan sibling's and it carries unchanged, with one thing
    /// SHARPER here: on this backend the backpressure accumulator has exactly one source, the ring's own segment
    /// gate (M-R2), so a non-zero stall count is unambiguous and mixing a second meaning into it would cost more
    /// than it costs there.
    /// </para>
    /// <para>
    /// CUMULATIVE RATHER THAN ROLLED PER FRAME, because the writes this counts are typically LOAD-TIME and happen
    /// before any frame has begun. A per-frame roll would discard exactly the ones worth seeing. Row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/582) reaches it through
    /// <c>GpuDeviceCounters.OffTimelineDeferred</c> and <c>OffTimelineOutstanding</c>, which are the two fields
    /// that struct already carries for this reading.
    /// </para>
    /// </summary>
    internal readonly struct MetalRingPatchStats
    {
        internal MetalRingPatchStats(int deferred, int applied, int coalesced, int dropped)
        {
            Deferred = deferred;
            Applied = applied;
            Coalesced = coalesced;
            Dropped = dropped;
        }

        /// <summary>Segment writes queued as a patch rather than copied on the spot. One off-timeline call can
        /// raise this by up to <c>FramesInFlight</c> minus one, since the current segment is never
        /// deferred.</summary>
        internal int Deferred { get; }

        /// <summary>Patches replayed into their segment at the frame boundary that opened it.</summary>
        internal int Applied { get; }

        /// <summary>Queued patches DISCARDED by a later one that fully covers them, which is the bound on the
        /// storage. Reported rather than folded into <see cref="Applied"/>, because a coalesced patch was never
        /// replayed and calling it applied would make the pair stop adding up.</summary>
        internal int Coalesced { get; }

        /// <summary>Queued patches THROWN AWAY WITH THEIR RING, when the buffer behind it was disposed before the
        /// frame boundary that would have replayed them. Its own count for the reason <see cref="Coalesced"/> has
        /// one, and leaving it out of the reckoning is what would make <see cref="Outstanding"/> climb for good
        /// in a program that streams uniform buffers in and out.</summary>
        internal int Dropped { get; }

        /// <summary>What is still queued: everything deferred that has neither been replayed, nor superseded, nor
        /// thrown away with a disposed ring. A number that keeps climbing rather than settling means patches are
        /// being recorded for a segment nothing ever acquires, which on a running device means frames
        /// stopped.</summary>
        internal int Outstanding => Deferred - Applied - Coalesced - Dropped;
    }
}
