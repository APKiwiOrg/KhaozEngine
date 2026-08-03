namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE M3 MEASUREMENT, as a per-frame value snapshot: how many times a frame had to wait for a ring segment
    /// to come free, and how long those waits took in total.
    /// <para>
    /// M3's bet is that three segments are enough that this never happens at all, and its exit criterion is a
    /// stall count of ZERO across a full soak capture window. So the interesting reading is not the size of the
    /// number, it is whether the number exists: one stall says the CPU reached a segment the GPU was still
    /// reading, which means the pipeline depth is wrong for that machine and <c>KE_D3D11_FRAMES_IN_FLIGHT</c> is
    /// the lever. The duration is carried alongside because a count with no cost attached cannot be weighed
    /// against raising the segment count, which costs memory in every uniform buffer at once.
    /// </para>
    /// <para>
    /// Same shape as <see cref="D3D11DrainStats"/> and for the same reason: a value a consumer reads whenever it
    /// likes, describing the frame that has ENDED rather than the one being built, so a reader never sees a
    /// half-accumulated total. <c>D3D11RingAllocator.BeginFrame</c> is what rolls one frame into the next, on the
    /// same present boundary the drain stats use.
    /// </para>
    /// <para>
    /// A SEGMENT THAT WAS ALREADY FREE DOES NOT COUNT, which is what makes zero meaningful. Every frame asks the
    /// completion timeline whether its next segment is finished with, and the overwhelming majority of those
    /// questions are answered yes immediately. Counting the question rather than the wait would report a stall on
    /// every frame of a run that never stalled once.
    /// </para>
    /// </summary>
    internal readonly struct D3D11BackpressureStats
    {
        internal D3D11BackpressureStats(int count, double totalMs)
        {
            Count = count;
            TotalMs = totalMs;
        }

        /// <summary>Segment acquisitions that actually waited on the GPU during the frame just ended. The M3 exit
        /// criterion is this being zero for a whole capture window.</summary>
        internal int Count { get; }

        /// <summary>Wall-clock milliseconds those waits spent blocked, summed. 0 when <see cref="Count"/> is
        /// 0.</summary>
        internal double TotalMs { get; }
    }
}
