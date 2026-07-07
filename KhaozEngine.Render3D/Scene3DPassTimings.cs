namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Last-frame CPU encode time (milliseconds) for each <see cref="Scene3D"/> render pass, read via
    /// <see cref="Scene3D.PassTimingsMs"/>. Only <see cref="Scene3D.EnableTiming"/> populates this (all fields stay
    /// 0 while it is off, the default); the brackets that fill it wrap ONLY a <c>Stopwatch</c> read when timing is
    /// disabled, so the near-zero-cost-when-off contract holds.
    /// <para>
    /// This measures wall-clock CPU time spent RECORDING commands for a pass (the span between that pass's first
    /// and last graphics-API call), not true GPU execution time - the GPU pipeline runs asynchronously behind the
    /// command list, so a cheap encode can precede expensive GPU work or vice versa. <c>KhaozEngine.Gpu</c>'s pinned
    /// Veldrid 4.9.0 exposes no timestamp-query API to measure true per-pass GPU time; see
    /// <c>docs/USING-KHAOZENGINE.md</c> for the full explanation. Feed these numbers into a
    /// <c>KhaozEngine.Diagnostics.PassTimings</c> meter (e.g. once per frame) to get rolling avg/min/max for a
    /// <c>DiagnosticsOverlay</c> section; <see cref="Render3D"/> itself has no dependency on
    /// <c>KhaozEngine.Diagnostics</c>, so that aggregation is the host's/game's glue code.
    /// </para>
    /// <para>
    /// Present/blit (swapping the finished frame to the screen) is not covered here: it happens in
    /// <c>KhaozEngine.Windowing.AppWindow.Run</c>, outside anything <see cref="Scene3D"/> records into.
    /// </para>
    /// </summary>
    public readonly struct Scene3DPassTimingsMs
    {
        /// <summary>CPU time recording the key-light shadow depth pass. 0 when the shadow tier is not
        /// <see cref="ShadowMode.ShadowMap"/> (the pass does not run) or when timing is off.</summary>
        public float ShadowDepthMs { get; }

        /// <summary>CPU time recording the model/terrain (splat) instanced draws.</summary>
        public float ModelMs { get; }

        /// <summary>CPU time recording transparents/decals: textured billboards, beams, overlay meshes, the MRT
        /// depth resolve, the sky background pass, shadow blob decals, ground decals, the colour/normal resolve,
        /// and the post-blit overlay draws (fills, lines, billboards).</summary>
        public float TransparentsMs { get; }

        /// <summary>CPU time recording the pixel post-process chain.</summary>
        public float PostMs { get; }

        internal Scene3DPassTimingsMs(float shadowDepthMs, float modelMs, float transparentsMs, float postMs)
        {
            ShadowDepthMs = shadowDepthMs;
            ModelMs = modelMs;
            TransparentsMs = transparentsMs;
            PostMs = postMs;
        }
    }
}
