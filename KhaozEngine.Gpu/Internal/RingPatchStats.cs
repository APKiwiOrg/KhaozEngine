namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE OFF-TIMELINE WRITE'S DEFERRAL COUNTERS, cumulative since the device was created: how many segment
    /// writes were queued as a pending patch instead of copied on the spot, and what became of each one. Every
    /// deferral leaves the queue exactly once, applied, coalesced or dropped with its ring, which is what makes
    /// <see cref="Outstanding"/> a READING rather than a running total.
    /// <para>
    /// IT COUNTS PATCHES RATHER THAN WAITS, because there are no waits on this path to count. A device-level
    /// uniform-buffer write that meets a segment an earlier frame is still reading records the bytes and returns,
    /// and the segment's next acquire writes them. Nothing blocks, so a milliseconds field here would report zero
    /// forever and read as "the waits are cheap" rather than as "there are none". That is also why this is a
    /// separate struct from <see cref="WaitTotals"/> rather than another pair beside it.
    /// </para>
    /// <para>
    /// SEPARATE FROM THE BACKPRESSURE ACCUMULATOR ON PURPOSE, and this is the one number the backends deliberately
    /// do NOT fold into their single stall count. That accumulator exists because the waits it covers are the same
    /// statement about the same lever, pipeline DEPTH, so one number answers all of them. A deferred patch is not
    /// a stall at all, so folding it in would turn a load-time write into evidence against the frames-in-flight
    /// setting and make a zero-stall exit criterion unreachable for a reason unrelated to depth.
    /// </para>
    /// <para>
    /// CUMULATIVE RATHER THAN ROLLED PER FRAME, because the writes this counts are typically LOAD-TIME and happen
    /// before any frame has begun, so a per-frame roll would discard exactly the ones worth seeing. It reaches the
    /// seam through <c>GpuDeviceCounters.OffTimelineDeferred</c> and <c>OffTimelineOutstanding</c>, which are the
    /// two fields that struct already carries for this reading.
    /// </para>
    /// <para>
    /// <b>ONE TYPE FOR THREE BACKENDS (#531's second extraction).</b> The three copies were code-identical, four
    /// <c>int</c>s and one subtraction under three sets of prose and one <c>PendingPatchStats</c>-versus-
    /// <c>RingPatchStats</c> naming. Unlike the wait counters, the accumulation sites agree here too: all three
    /// rings defer on the same condition and retire a patch the same three ways. That is what makes this the one
    /// counter of the set where the extraction is not carriers-only.
    /// </para>
    /// </summary>
    internal readonly struct RingPatchStats
    {
        internal RingPatchStats(int deferred, int applied, int coalesced, int dropped)
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
