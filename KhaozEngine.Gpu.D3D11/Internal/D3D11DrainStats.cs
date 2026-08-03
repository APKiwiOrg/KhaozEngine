namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE M2 MEASUREMENT, as a per-frame value snapshot: how many times <c>WaitForIdle</c> actually drained in
    /// the frame just ended, and how long those drains took in total.
    /// <para>
    /// Two numbers rather than one because they answer different questions and a regression shows up in only one
    /// of them. <see cref="Count"/> says how often the engine asks the GPU to catch up, which is a property of
    /// the renderer and moves when a caller is added or removed. <see cref="TotalMs"/> is the cost, which is the
    /// gate: M2 passes when it stays under 0.2 ms per frame at the 125 fps baseline across two consecutive soak
    /// builds. A count that rises while the total stays flat is not a regression, and a total that rises on a
    /// flat count is the interesting one.
    /// </para>
    /// <para>
    /// Same shape as <c>WaterFrameStats</c> and <c>Scene3D.LastFrameStats</c>: a value a consumer reads whenever
    /// it likes, describing the frame that has ENDED rather than the one being built, so a reader never sees a
    /// half-accumulated total. <see cref="D3D11FenceSubsystem.BeginFrame"/> is what rolls one frame into the
    /// next.
    /// </para>
    /// <para>
    /// A drain that returns immediately still COUNTS. The kill switch and a dead device both make
    /// <c>WaitForIdle</c> return without draining, and those do not count, because counting them would report a
    /// run with the switch down as having drained a few hundred times for zero milliseconds.
    /// </para>
    /// </summary>
    internal readonly struct D3D11DrainStats
    {
        internal D3D11DrainStats(int count, double totalMs)
        {
            Count = count;
            TotalMs = totalMs;
        }

        /// <summary>Drains that actually waited on the GPU during the frame just ended.</summary>
        internal int Count { get; }

        /// <summary>Wall-clock milliseconds those drains spent waiting, summed. 0 when
        /// <see cref="Count"/> is 0.</summary>
        internal double TotalMs { get; }
    }
}
