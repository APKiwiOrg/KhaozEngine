namespace KhaozEngine.Render3D
{
    /// <summary>Last-frame water diagnostics, read via <see cref="Scene3D.LastWaterStats"/>. Same shape as
    /// <see cref="Scene3D.LastFrameStats"/> and <c>Scene3DChunkSink.MergeStats</c> (KhaozEngine.Terrain.Render3D): a
    /// value snapshot a consumer reads whenever it likes. Issue #374: before this, both numbers existed only as
    /// internal counters on <c>WaterRenderer</c> (visible to KhaozEngine's own tests via InternalsVisibleTo), so a
    /// consuming game could not read either without reflection.
    /// <para><b>Semantics match the underlying counters exactly</b> (this is a passthrough, not a reinterpretation):
    /// both fields describe the water renderer's LAST <c>Draw</c> call, not "this Scene3D frame". A frame that
    /// queues no water plane skips that <c>Draw</c> entirely, so both fields hold whatever they were after the last
    /// frame that DID queue water, rather than resetting to 0. A consumer that cares about "did water even run this
    /// frame" already knows that from having called (or not called) <c>DrawWater</c> itself.</para></summary>
    public readonly struct WaterFrameStats
    {
        /// <summary>GPU stalls (<c>Submit</c> + <c>WaitForIdle</c> pairs) the ocean FFT paid in the water renderer's
        /// last <c>Draw</c>: 1 when the FFT ocean cascades were updated, 0 when nothing this Draw read them (no
        /// plane used <see cref="WaterWaveSource.FftOcean"/>, or the GPU lacks compute support). Independent of
        /// cascade count and resolution. See <c>OceanFftProducer.LastStallCount</c> (issue #311).</summary>
        public int OceanStalls { get; }

        /// <summary>Wall-clock milliseconds the ocean FFT spent blocked on that drain: the whole cost of #311's
        /// missing cross-dispatch barrier, measured rather than assumed. 0 when <see cref="OceanStalls"/> is 0. See
        /// <c>OceanFftProducer.LastStallMs</c>.</summary>
        public double OceanStallMs { get; }

        /// <summary>Clipmap grids rebuilt AND re-uploaded in the water renderer's last <c>Draw</c>, across every
        /// queued plane. 0 on most frames: a world-locked ring only moves when the camera crosses one of its snap
        /// boundaries. Always 0 when <see cref="WaterSettings.GridMode"/> is not <see cref="WaterGridMode.Clipmap"/>.
        /// See <c>WaterRenderer.LastClipmapRebuilds</c>.</summary>
        public int ClipmapRebuilds { get; }

        internal WaterFrameStats(int oceanStalls, double oceanStallMs, int clipmapRebuilds)
        {
            OceanStalls = oceanStalls;
            OceanStallMs = oceanStallMs;
            ClipmapRebuilds = clipmapRebuilds;
        }
    }
}
