using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHICH VISIBILITY PATH THE SHADOWED YARD'S DISPLAY PIXELS TAKE (review 7f r2, m-4). The timed shadowed frame case
    /// (<see cref="TemporalShadowedFrameCostPerfGpuTests"/>) prints how much of the display the key shadow covers, which
    /// says nothing of how many pixels hold a lock or run the visibility check, so a yard where few did would read as
    /// cheap without saying why. This fact renders the same scene through the case's own
    /// <see cref="TemporalShadowedFrameCostPerfGpuTests.Yard"/>, outside the timed class, with the scene's resolve
    /// renderer built over the path export (<see cref="TemporalVisibilityProductionPaths.Rewrite"/>), and reads the
    /// exported history on <see cref="TemporalShadowedFrameCostPerfGpuTests.CoverageFrame"/>. It prints the share of
    /// display pixels gated off by a lock whose bits are zero (R1), gated otherwise (no history, background or the
    /// first frame), skipped by range (R4), computing a change on the fast path, on the fast path with nothing
    /// accepted, on the slow path by reason, and released.
    /// <para>The export writes the path word in place of the history colour, and that history is read again the next
    /// frame. The state output, every input of the decision and every rule that sets a lock stay production's, so a
    /// production scene rendered beside it in lockstep must store the same locks, and the fact asserts that none
    /// differ (review 7f r3, m-4). It is a readback, not a timing, and asserts besides only that the categories cover
    /// every display pixel and that every slow pixel names its reason. Software devices render a smaller display,
    /// since only a GPU's share at 1440p feeds the bench.</para>
    /// </summary>
    public sealed class TemporalShadowedFramePathShareGpuTests(ITestOutputHelper output)
    {
        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void TheShadowedYardReportsItsVisibilityPathShare(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            (int w, int h) = TemporalVisibilityPathShareReadback.DisplaySize();
            var yard = new TemporalShadowedFrameCostPerfGpuTests.Yard(w, h);
            using var exporting = new TemporalFixture(w, h, s => yard.Setup(s, preset));
            using var production = new TemporalFixture(w, h, s => yard.Setup(s, preset));
            TemporalResolveRenderer exporter = TemporalVisibilityPathShareReadback.UseExportingResolve(exporting);
            output.WriteLine($"{exporting.Device.Backend} on {exporting.Device.Capabilities.DeviceName}, {entry} {preset}, "
                + $"{w}x{h} display");

            exporting.Scene.TemporalResolveEntryForTests = entry;
            production.Scene.TemporalResolveEntryForTests = entry;
            for (int frame = 0; frame <= TemporalShadowedFrameCostPerfGpuTests.CoverageFrame; frame++)
            {
                exporting.Frames(1, yard.Draw);
                production.Frames(1, yard.Draw);
            }
            foreach (TemporalFixture fx in new[] { exporting, production })
            {
                Assert.True(fx.Scene.ResolvedLastRenderForTests, "the frame did not resolve");
                Assert.True(fx.Scene.LastShadowPassDiagnostics.Active, "the key light's shadow map did not run");
                Assert.Equal(entry, fx.Scene.TemporalResolveRendererForTests!.LastEntry);
            }
            Assert.Same(exporter, exporting.Scene.TemporalResolveRendererForTests);

            TemporalVisibilityPathShareReadback.ReportAndAssert(exporting, production,
                TemporalShadowedFrameCostPerfGpuTests.CoverageFrame, output);
        }
    }
}
