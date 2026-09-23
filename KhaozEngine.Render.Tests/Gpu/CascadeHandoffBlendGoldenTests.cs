using System;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The net for the cross-cascade blend band (<c>ShadowSettings.ShadowCascadeBlend</c>): an in-session A/B pair over
    /// <see cref="CascadeHandoffScene"/>, the default blend of 0.15 against a hard cut of 0, on the same backend and
    /// adapter in one test, counting the pixels the blend moves inside the band.
    /// <para>
    /// WHY NOT THE COMMITTED GOLDEN. <c>scene3d_cascade_handoff</c> renders this very scene, and the golden audit
    /// (<c>docs/design/GOLDEN-TEST-AUDIT-2026-09-23.md</c> sections 2 and 3) measured it passing with the blend
    /// dropped: worst cell 0.0080, under even the tightened 0.01 tolerance. The blend changes 148 pixels by more than
    /// 16/255 on Metal, at most 0.21, all along one shadow edge where the hand-off crosses it, and a 32x18 average of
    /// the whole frame dilutes that to nothing. Pixels see it, so this reads pixels.
    /// </para>
    /// <para>
    /// NOTHING IS COMMITTED AND NOTHING IS BAKED. Same-backend captures are bit-identical from run to run on all
    /// three legs, so with the blend removed the two renders are identical and the count is 0. The reference is the
    /// engine's own hard-cut path, rendered in the same session, the shape <c>MsaaResolveTargetGoldenTests</c> and
    /// <c>GroundDecalVoidGoldenTests</c> use.
    /// </para>
    /// <para>
    /// Named <c>*GoldenTests</c> deliberately: the cross-platform matrix selects <c>FullyQualifiedName~Golden</c> on
    /// the push path, so this runs on Direct3D 11 and Vulkan as well as Metal. Gated on KE_GPU_TESTS.
    /// </para>
    /// </summary>
    public sealed class CascadeHandoffBlendGoldenTests
    {
        const int W = CascadeHandoffScene.W, H = CascadeHandoffScene.H;

        /// <summary>A pixel counts as moved when any channel differs by more than this many 8-bit steps.</summary>
        const int MovedStep = 16;

        /// <summary>
        /// How many moved pixels the band must hold. Measured 148 on Metal (Apple M2 Max), every one of them inside
        /// the band. A dropped blend scores 0, so the bar sits near a quarter of the measurement: far above the
        /// failure, and low enough that a software rasterizer drawing the seam edge a little shorter or softer still
        /// clears it.
        /// </summary>
        const int MinMovedInBand = 40;

        /// <summary>
        /// Pixels the CPU band mask is grown by before counting. The mask mirrors the shader's cascade selection
        /// without its normal offset and two-texel margin, and models the floor rather than the boxes, so its edge
        /// is a few pixels approximate.
        /// </summary>
        const int BandDilation = 3;

        /// <summary>Floor pixels the undilated band must cover for the scene to still exercise it. Measured 27553.
        /// </summary>
        const int MinBandPixels = 5000;

        readonly ITestOutputHelper _out;
        public CascadeHandoffBlendGoldenTests(ITestOutputHelper output) => _out = output;

        [GpuFact]
        public void Golden_cascade_blend_cross_fades_the_handoff_band()
        {
            byte[] blended = CascadeHandoffScene.Capture(CascadeHandoffScene.Camera(),
                CascadeHandoffScene.DefaultBlend);
            byte[] hardCut = CascadeHandoffScene.Capture(CascadeHandoffScene.Camera(), 0f);
            bool[] rawBand = CascadeHandoffScene.BlendBandMask(CascadeHandoffScene.Camera(),
                CascadeHandoffScene.DefaultBlend);
            bool[] band = Dilate(rawBand, BandDilation);

            int rawBandPixels = 0, moved = 0, movedInBand = 0, maxStep = 0;
            for (int p = 0; p < W * H; p++)
            {
                if (rawBand[p]) rawBandPixels++;
                int i = p * 4;
                int step = Math.Max(Math.Abs(blended[i] - hardCut[i]),
                    Math.Max(Math.Abs(blended[i + 1] - hardCut[i + 1]), Math.Abs(blended[i + 2] - hardCut[i + 2])));
                maxStep = Math.Max(maxStep, step);
                if (step <= MovedStep) continue;
                moved++;
                if (band[p]) movedInBand++;
            }
            _out.WriteLine($"band {rawBandPixels} px, moved over {MovedStep}/255: {moved} ({movedInBand} in band), " +
                $"max step {maxStep}/255 ({maxStep / 255f:0.####})");

            // Anti-vacuity: the hand-off has to cross the visible floor, or a zero count below would be the camera's
            // fault rather than the blend's.
            Assert.True(rawBandPixels >= MinBandPixels,
                $"only {rawBandPixels} floor pixels sit in a blend band (measured 27553): the hand-off no longer " +
                "crosses the visible floor, so this pair cannot see the blend. Re-frame CascadeHandoffScene.");

            Assert.True(movedInBand >= MinMovedInBand,
                $"ShadowCascadeBlend 0.15 against 0 moved only {movedInBand} band pixels by more than " +
                $"{MovedStep}/255 ({moved} in the whole frame, max step {maxStep}/255). Measured 148 on Metal, and 0 " +
                "means the two renders are the same picture: the blend band is not reaching the shader, or the " +
                "shader ignores it.");

            // Where it moves matters as much as how much. The blend only acts inside a band, so a difference outside
            // it is some other effect of the setting.
            Assert.True(movedInBand * 10 >= moved * 9,
                $"only {movedInBand} of the {moved} pixels the blend moved lie in the band. The cross-fade is " +
                "changing the frame outside the cascade hand-off.");
        }

        static bool[] Dilate(bool[] mask, int radius)
        {
            var result = new bool[mask.Length];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (!mask[y * W + x]) continue;
                    for (int dy = Math.Max(0, y - radius); dy <= Math.Min(H - 1, y + radius); dy++)
                        for (int dx = Math.Max(0, x - radius); dx <= Math.Min(W - 1, x + radius); dx++)
                            result[dy * W + dx] = true;
                }
            return result;
        }
    }
}
