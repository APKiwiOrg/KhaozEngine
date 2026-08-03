namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE OFF-TIMELINE WRITE'S DEFERRAL COUNTERS, cumulative since the device was created: how many segment
    /// writes were queued as a pending patch instead of copied on the spot, and what became of each one. Every
    /// deferral leaves the queue exactly once, applied, coalesced or dropped with its ring, which is what makes
    /// <see cref="Outstanding"/> a reading rather than a running total.
    /// <para>
    /// SAME REPORTING SHAPE AS <see cref="D3D11BackpressureStats"/>, AND NOT THE SAME MEASUREMENT. That struct is
    /// M3's, per frame, counting times a frame BLOCKED on the GPU. This one counts no time at all, because the
    /// path it describes never blocks: a device-level <c>UpdateBuffer</c> that meets a segment an earlier frame is
    /// still reading records the bytes and returns, and the segment's next acquire writes them. There is no wait
    /// to time, so a milliseconds field here would report zero forever and read as "the waits are cheap" rather
    /// than as "there are none".
    /// </para>
    /// <para>
    /// WHY IT IS REPORTED SEPARATELY, which is the reasoning the earlier wait counter carried and keeps.
    /// M3's exit criterion is that <c>D3D11RingAllocator.LastFrameBackpressure</c> is ZERO across a soak window,
    /// which reads as "this many segments are enough for this machine". A deferred patch says nothing about that:
    /// it says a caller wrote a uniform buffer off-timeline while an earlier frame was still reading a segment of
    /// it, which is normal and costs nobody a stall. Folding the two together would turn a load-time write into
    /// evidence against the segment count.
    /// </para>
    /// <para>
    /// CUMULATIVE RATHER THAN ROLLED PER FRAME, for the same reason the wait counter was: these writes are
    /// typically LOAD-TIME and happen before any frame has begun, so a per-frame roll would discard exactly the
    /// ones worth seeing.
    /// </para>
    /// </summary>
    internal readonly struct D3D11PendingPatchStats
    {
        internal D3D11PendingPatchStats(int deferred, int applied, int coalesced, int dropped)
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
        /// one: the patch was never replayed, so calling it applied would be a lie, and leaving it out of the
        /// reckoning entirely is what used to make <see cref="Outstanding"/> climb for good in a program that
        /// streams uniform buffers in and out.</summary>
        internal int Dropped { get; }

        /// <summary>What is still queued: everything deferred that has neither been replayed, nor superseded, nor
        /// thrown away with a disposed ring. A number that keeps climbing rather than settling means patches are
        /// being recorded for a segment nothing ever acquires, which on a running device means frames
        /// stopped.</summary>
        internal int Outstanding => Deferred - Applied - Coalesced - Dropped;
    }
}
