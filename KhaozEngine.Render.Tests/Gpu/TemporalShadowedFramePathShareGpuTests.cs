using System;
using System.Globalization;
using System.Reflection;
using KhaozEngine.Gpu;
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
    /// production scene rendered beside it in lockstep should store the same locks, and the fact prints how many
    /// differ. It is a readback, not a timing, and asserts only that the categories cover every display pixel and
    /// that every slow pixel names its reason. Software devices render a smaller display, since only a GPU's share at
    /// 1440p feeds the bench.</para>
    /// </summary>
    public sealed class TemporalShadowedFramePathShareGpuTests(ITestOutputHelper output)
    {
        const int Width = 2560, Height = 1440, SoftwareWidth = 480, SoftwareHeight = 270;

        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void TheShadowedYardReportsItsVisibilityPathShare(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            (int w, int h) = (Width, Height);
            using (GpuDeviceContext probe = GpuDeviceContext.CreateHeadless())
                if (TemporalResolveCostPerfGpuTests.IsSoftwareDevice(probe.GpuDevice.Capabilities.DeviceName))
                    (w, h) = (SoftwareWidth, SoftwareHeight);
            var yard = new TemporalShadowedFrameCostPerfGpuTests.Yard(w, h);
            using var exporting = new TemporalFixture(w, h, s => yard.Setup(s, preset));
            using var production = new TemporalFixture(w, h, s => yard.Setup(s, preset));
            TemporalResolveRenderer exporter = UseExportingResolve(exporting);
            output.WriteLine($"{exporting.Device.Backend} on {exporting.Device.Capabilities.DeviceName}, {entry} {preset}, "
                + $"{w}x{h} display");

            foreach (TemporalFixture fx in new[] { exporting, production })
            {
                fx.Scene.TemporalResolveEntryForTests = entry;
                fx.Frames(TemporalShadowedFrameCostPerfGpuTests.CoverageFrame + 1, yard.Draw);
                Assert.True(fx.Scene.ResolvedLastRenderForTests, "the frame did not resolve");
                Assert.True(fx.Scene.LastShadowPassDiagnostics.Active, "the key light's shadow map did not run");
                Assert.Equal(entry, fx.Scene.TemporalResolveRendererForTests!.LastEntry);
            }
            Assert.Same(exporter, exporting.Scene.TemporalResolveRendererForTests);

            TemporalHistory history = exporting.Scene.TemporalHistory;
            float[] exported = TemporalTextureIo.Read(exporting.Device, history.Color(history.WriteIndex));
            float[] exportedState = TemporalTextureIo.Read(exporting.Device, history.Confidence(history.WriteIndex));
            TemporalHistory productionHistory = production.Scene.TemporalHistory;
            float[] productionState = TemporalTextureIo.Read(production.Device,
                productionHistory.Confidence(productionHistory.WriteIndex));
            Assert.Equal(w * h * 4, exported.Length);
            var paths = new TemporalVisibilityProductionPaths();
            paths.Add(exported);
            long lockDiffers = 0;
            for (int p = 0; p < w * h; p++)
                if (BitConverter.SingleToInt32Bits(exportedState[p * 2 + 1])
                    != BitConverter.SingleToInt32Bits(productionState[p * 2 + 1]))
                    lockDiffers++;

            long pixels = (long)w * h;
            string Share(long part) => (100.0 * part / pixels).ToString("0.00", CultureInfo.InvariantCulture) + "%";
            output.WriteLine($"  frame {TemporalShadowedFrameCostPerfGpuTests.CoverageFrame}, {paths.Pixels} display "
                + "pixels by visibility path:");
            output.WriteLine($"    not reaching the call site {Share(paths.NotReached)}");
            output.WriteLine($"    gated off by a zero lock (R1) {Share(paths.GatedByLock)}");
            output.WriteLine($"    gated off otherwise (no history, background, first frame) {Share(paths.GatedOtherwise)}");
            output.WriteLine($"    the check runs {Share(paths.FastPath + paths.SlowPath)}: fast {Share(paths.FastPath)} "
                + $"(range skip {Share(paths.RangeSkips)}, change computed {Share(paths.FastComputed)}, nothing accepted "
                + $"{Share(paths.FastWithoutChange)}), slow {Share(paths.SlowPath)} (at an edge {Share(paths.SlowEdge)}, "
                + $"on a base deviation {Share(paths.SlowDeviation)}, at the border {Share(paths.SlowBorder)}, at a binade "
                + $"{Share(paths.SlowBinade)}, change computed {Share(paths.ChangeComputed - paths.FastComputed)})");
            output.WriteLine($"    released {Share(paths.Released)} (on the fast path {Share(paths.FastReleased)})");
            output.WriteLine($"  stored locks apart from a production scene rendered beside it: {lockDiffers} of {pixels}");

            Assert.Equal(pixels, paths.Pixels);
            Assert.Equal(pixels, paths.NotReached + paths.GatedByLock + paths.GatedOtherwise + paths.FastPath
                + paths.SlowPath);
            Assert.Equal(paths.SlowPath, paths.SlowEdge + paths.SlowDeviation + paths.SlowBorder + paths.SlowBinade);
        }

        // The scene builds its resolve renderer on the first frame that resolves, unless one is there already. This
        // places one built over the path export before that frame. Disposed with the scene.
        static TemporalResolveRenderer UseExportingResolve(TemporalFixture fx)
        {
            FieldInfo field = typeof(Scene3D).GetField("_temporalResolve", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Scene3D no longer holds its resolve renderer in _temporalResolve");
            Assert.Null(field.GetValue(fx.Scene));
            var renderer = new TemporalResolveRenderer(fx.Device, TemporalVisibilityProductionPaths.Rewrite);
            field.SetValue(fx.Scene, renderer);
            return renderer;
        }
    }
}
