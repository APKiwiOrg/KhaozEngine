using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// THE MSAA RESOLVE DESTINATIONS, readable by the test assembly and by nothing else. The rest of
    /// <see cref="Scene3D"/> reaches its targets through the field directly, so this exists for one job: letting a
    /// test read back the intermediate target a frame RESOLVED into and check it carries that frame's geometry.
    ///
    /// <para><b>WHY A TEST NEEDS THAT AT ALL.</b> The golden family cannot see a dropped depth/normal resolve
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/603). A 32x18 averaged grid of the FINAL image moved by
    /// less than its own tolerance when the whole <see cref="Internal.RenderResources.ResolveDepthNormal"/> pair was
    /// silently discarded, so every golden stayed green while one of the two targets held the wrong frame. The
    /// destination is the thing that went wrong, so the destination is the thing to read.</para>
    ///
    /// <para><b>ITS OWN FILE BECAUSE <c>Scene3D.cs</c> IS AT ITS FROZEN SIZE.</b> The KESIZE ratchet holds that
    /// file at its measured line count, and the answer the ratchet asks for is new code in its own file rather than
    /// a split of the old one at an arbitrary line. Three accessors are not a type, so this is the partial that
    /// carries them, next to the other internal test-facing reads that live at the bottom of the main file.</para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>
        /// The sample count the internal MRT is currently allocated at: 1 for the single-sample path, above 1 when
        /// <see cref="PixelPostProcessSettings.Quality"/> asked for MSAA and the device could give it. Internal so
        /// a test can assert the MSAA path was ACTUALLY taken, which matters because
        /// <see cref="AntiAliasing.ResolveFor"/> silently downgrades to Fxaa on a device whose
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> is below the request, and a test that did not check
        /// would compare the single-sample path against itself and pass having measured nothing.
        /// </summary>
        internal int RenderTargetSampleCount => _res.SampleCount;

        /// <summary>
        /// The single-sample linear-depth target the post chain and the ground-decal pass sample. Under MSAA it is
        /// the DESTINATION of the first resolve in <see cref="Internal.RenderResources.ResolveDepthNormal"/>, and
        /// on the single-sample path it IS the MRT attachment. That is exactly what makes it a usable reference for
        /// itself: the same scene rendered both ways has to produce the same depth here.
        /// </summary>
        internal IGpuTexture ResolvedDepthTarget => _res.DepthColorTex!;

        /// <summary>
        /// The single-sample encoded-normal target, the destination of the SECOND resolve of that same pair, and
        /// the MRT attachment on the single-sample path for the reason above.
        /// </summary>
        internal IGpuTexture ResolvedNormalTarget => _res.NormalTex!;
    }
}
