using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// End-to-end proof of the shadow fit's light-movement epsilon (issue #410, design section 3.3) through a live
    /// render. The threshold arithmetic is pinned headless by KhaozEngine.Tests.Render3D.ShadowLightHoldTests. What
    /// only a real pass can prove is the other three things:
    /// <list type="number">
    /// <item>a sub-threshold sun actually stops the depth pass recording, with <c>LightMatrixChanged</c> going false
    ///   rather than the pass merely running faster,</item>
    /// <item>a caster that MOVES under a held sun still re-records (the ghosting guard the issue's acceptance
    ///   criterion is written around), and</item>
    /// <item>the receiver still agrees with the atlas on a held frame, which is the coupling hazard that decides
    ///   whether this had to be built as "do not re-fit" rather than the simpler "fit and skip".</item>
    /// </list>
    /// Gated on KE_GPU_TESTS.
    /// </summary>
    public sealed class ShadowLightHoldGpuTests
    {
        const int W = 320, H = 240;
        const float SunElevationDegrees = 35f;

        /// <summary>A key light travelling away from a sun at <see cref="SunElevationDegrees"/> and the given
        /// azimuth, matching the helper the shadow benches use.</summary>
        static Vector3 SunAt(float azimuthDegrees)
        {
            float e = SunElevationDegrees * MathF.PI / 180f;
            float a = azimuthDegrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(a) * MathF.Cos(e), -MathF.Sin(e), MathF.Sin(a) * MathF.Cos(e)));
        }

        static void ConfigureShadowScene(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = SunAt(0f);
            scene.Camera.Azimuth = 0.9f;
            scene.Camera.Elevation = 0.75f;
            scene.Camera.Frame(new Vector3(2f, 0f, 0f), new Vector3(14f, 8f, 14f));
        }

        // A sun step small enough that many frames of it stay inside the shipped one-texel threshold. Ruinborne's own
        // daylight rate is 0.00333 deg/frame, so this is about a seventeenth of it and the whole 40 frame run below
        // covers less than 0.008 deg, which is well inside the threshold this scene's cascade 0 implies.
        const float SubThresholdDegreesPerFrame = 0.0002f;
        const int HeldFrames = 40;

        [GpuFact]
        public void A_sub_threshold_sun_stops_re_recording_and_says_so()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            ConfigureShadowScene(scene);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(40f, 0.1f));
            MeshHandle post = scene.LoadMesh(MeshPrimitives.Box(1f));

            float azimuth = 0f;
            void DrawStatic(Scene3D s)
            {
                s.Post.LightDirection = SunAt(azimuth);
                s.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                s.Draw(post, Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f),
                    new Color(0.20f, 0.75f, 0.25f, 1f));
            }

            // Settle: the first frame has no atlas, so it always records.
            preview.Capture(DrawStatic);
            Assert.True(scene.LastShadowPassDiagnostics.Rendered);

            int rendered = 0, skipped = 0;
            for (int i = 0; i < HeldFrames; i++)
            {
                azimuth += SubThresholdDegreesPerFrame;
                preview.Capture(DrawStatic);
                ShadowPassDiagnostics d = scene.LastShadowPassDiagnostics;
                Assert.False(d.CasterDataChanged, "nothing in this scene moves");
                Assert.False(d.AnySkinnedCaster, "no skinned caster is queued");
                if (d.Rendered) rendered++;
                else
                {
                    skipped++;
                    Assert.False(d.LightMatrixChanged,
                        "a held frame must report the light matrix as UNCHANGED: that bit going false is the whole fix");
                    Assert.Equal(0, d.TotalDrawCalls);
                    Assert.Equal(0, d.TotalRigidSpanCount);
                }
            }
            Assert.Equal(HeldFrames, skipped);
            Assert.Equal(0, rendered);
        }

        [GpuFact]
        public void A_supra_threshold_step_re_fits_and_a_disabled_hold_never_holds_at_all()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            ConfigureShadowScene(scene);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(40f, 0.1f));
            MeshHandle post = scene.LoadMesh(MeshPrimitives.Box(1f));

            float azimuth = 0f;
            void DrawStatic(Scene3D s)
            {
                s.Post.LightDirection = SunAt(azimuth);
                s.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                s.Draw(post, Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f),
                    new Color(0.20f, 0.75f, 0.25f, 1f));
            }

            preview.Capture(DrawStatic);
            azimuth += SubThresholdDegreesPerFrame;
            preview.Capture(DrawStatic);
            Assert.True(scene.LastShadowPassDiagnostics.Skipped, "the sub-threshold step must hold first");

            // One step far past the threshold: the fit adopts, the matrices move, the atlas is repainted.
            azimuth += 5f;
            preview.Capture(DrawStatic);
            ShadowPassDiagnostics moved = scene.LastShadowPassDiagnostics;
            Assert.True(moved.Rendered);
            Assert.True(moved.LightMatrixChanged);
            Assert.False(moved.CasterDataChanged);
            Assert.True(moved.RigidDrawCalls > 0);

            // And it settles again straight away, so the re-fit is a step rather than a latch.
            azimuth += SubThresholdDegreesPerFrame;
            preview.Capture(DrawStatic);
            Assert.True(scene.LastShadowPassDiagnostics.Skipped);

            // With the budget at 0 the epsilon is gone and every sun movement re-records, which is the pre-17.36.1
            // behaviour this knob has to be able to restore exactly.
            scene.Post.Quality.Shadows.ShadowLightHoldTexels = 0f;
            for (int i = 0; i < 5; i++)
            {
                azimuth += SubThresholdDegreesPerFrame;
                preview.Capture(DrawStatic);
                ShadowPassDiagnostics d = scene.LastShadowPassDiagnostics;
                Assert.True(d.Rendered, "with the hold disabled, any sun movement must re-record");
                Assert.True(d.LightMatrixChanged);
            }
        }

        [GpuFact]
        public void A_caster_that_moves_under_a_held_sun_still_re_records()
        {
            // The ghosting guard from #410's acceptance criterion. The hold suppresses the LIGHT reason only: a
            // caster that moves must still repaint the atlas, or its old silhouette survives in it.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            ConfigureShadowScene(scene);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(40f, 0.1f));
            MeshHandle post = scene.LoadMesh(MeshPrimitives.Box(1f));

            float azimuth = 0f, casterZ = 0f;
            void Draw(Scene3D s)
            {
                s.Post.LightDirection = SunAt(azimuth);
                s.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                s.Draw(post, Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(0f, 1.5f, casterZ),
                    new Color(0.20f, 0.75f, 0.25f, 1f));
            }

            preview.Capture(Draw);
            azimuth += SubThresholdDegreesPerFrame;
            preview.Capture(Draw);
            Assert.True(scene.LastShadowPassDiagnostics.Skipped, "the sun alone must be held at this rate");

            for (int i = 0; i < 6; i++)
            {
                azimuth += SubThresholdDegreesPerFrame;
                casterZ += 0.25f;
                preview.Capture(Draw);
                ShadowPassDiagnostics d = scene.LastShadowPassDiagnostics;
                Assert.True(d.Rendered, "a moving caster must repaint the atlas even while the sun is held");
                Assert.True(d.CasterDataChanged, "and it must name the caster, not the light, as the reason");
                Assert.False(d.LightMatrixChanged, "the sun really is still held: only the caster reason is set");
                Assert.True(d.RigidDrawCalls > 0);
            }

            // Stop the caster and the pass settles back to skipping, so the re-records above tracked the caster
            // rather than latching the pass dirty.
            azimuth += SubThresholdDegreesPerFrame;
            preview.Capture(Draw);
            azimuth += SubThresholdDegreesPerFrame;
            preview.Capture(Draw);
            Assert.True(scene.LastShadowPassDiagnostics.Skipped);
        }

        // ---- the receiver-coupling hazard ------------------------------------------------------------------------

        // An absurd budget, on purpose. At the shipped budget of one texel the hold can only ever produce a shadow
        // displacement below one texel, which is sub-pixel on screen, so a probe could not tell a correct build from
        // the broken one. Cranking the budget makes the SAME mechanism hold across a rotation large enough to see,
        // which is what gives this test teeth: with the fit's light input held, the atlas and the receiver both stay
        // at the held direction and the shadow does not move at all. Had the fit been left live and only the
        // RECORDING skipped, the receiver would sample the old atlas through the new matrix and the shadow would
        // shift by the whole un-recorded delta, which is what the numbers below would then read.
        const float HugeHoldTexels = 2000f;
        const float VisibleSunStepDegrees = 6f;

        [GpuFact]
        public void A_held_frame_keeps_the_receiver_and_the_atlas_in_agreement()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            bool[] reference = ShadowMaskAt(gd, VisibleSunStepDegrees, hold: HugeHoldTexels, out bool heldFrameSkipped,
                out int heldShadowPixels);
            bool[] refAtStart = ShadowMaskAt(gd, 0f, hold: HugeHoldTexels, out _, out int startShadowPixels);
            bool[] recorded = ShadowMaskAt(gd, VisibleSunStepDegrees, hold: 0f, out bool offFrameSkipped,
                out int offShadowPixels);

            Assert.True(heldFrameSkipped, "the held run must actually be holding, or this proves nothing");
            Assert.False(offFrameSkipped, "the control run must actually re-record, or the comparison has no baseline");
            Assert.True(startShadowPixels > 400, $"the probe window must contain a substantial shadow ({startShadowPixels} px)");

            int heldDrift = Differences(reference, refAtStart);
            int recordedDrift = Differences(recorded, refAtStart);

            // With the light INPUT held, the fit, the atlas and the receiver tail are all still at azimuth 0, so the
            // shadow is exactly where the un-moved sun put it. On local Metal this lands at 0 differing pixels out of
            // 512 shadowed ones. The tolerance is for the handful whose DIFFUSE shading (which does follow the live
            // sun, only the shadow fit is held) could cross the mask threshold on another backend.
            Assert.True(heldDrift < startShadowPixels / 20,
                $"a held frame shifted the shadow by {heldDrift} px against {startShadowPixels} px of shadow: the " +
                "receiver is sampling the atlas through a matrix the atlas was not recorded with");
            // The control is the half that stops the assertion above being vacuous: the same rotation, re-recorded
            // rather than held, moves the shadow across a fifth of the probe window (178 px locally), so a probe that
            // reads 0 for the held frame is reading agreement rather than blindness.
            Assert.True(recordedDrift > startShadowPixels / 5,
                $"the control's re-recorded shadow moved only {recordedDrift} px of {startShadowPixels}: the probe " +
                "cannot resolve this displacement, so the held run's number proves nothing");
            Assert.True(offShadowPixels > 400);
            Assert.Equal(startShadowPixels, heldShadowPixels);   // a held frame casts the same shadow, not a similar one
        }

        /// <summary>
        /// Render a fixed scene twice, the second frame with the sun rotated by <paramref name="azimuthDegrees"/>,
        /// and return the second frame's shadow mask over the probe window. <paramref name="hold"/> is the
        /// <c>ShadowLightHoldTexels</c> budget, so the caller can run the same scene with the hold engaged and with
        /// it disabled and compare.
        /// </summary>
        static bool[] ShadowMaskAt(IGpuDevice gd, float azimuthDegrees, float hold, out bool skipped, out int shadowPixels)
        {
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            ConfigureShadowScene(scene);
            scene.Post.Quality.Shadows.ShadowLightHoldTexels = hold;
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(40f, 0.1f));
            MeshHandle post = scene.LoadMesh(MeshPrimitives.Box(1f));

            float azimuth = 0f;
            void Draw(Scene3D s)
            {
                s.Post.LightDirection = SunAt(azimuth);
                s.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                s.Draw(post, Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f),
                    new Color(0.20f, 0.75f, 0.25f, 1f));
            }

            preview.Capture(Draw);                     // records the atlas at azimuth 0
            azimuth = azimuthDegrees;
            Texture2D tex = preview.Capture(Draw);     // the frame under test
            skipped = scene.LastShadowPassDiagnostics.Skipped;
            byte[] rgba = GpuReadback.ToRgba(gd, tex.Handle, W, H);
            return ShadowMask(rgba, out shadowPixels);
        }

        /// <summary>
        /// Classify every pixel of the probe window as shadowed or lit. The threshold is a fraction of the window's
        /// OWN bright end rather than an absolute luminance, because the diffuse term follows the live sun and a
        /// rotated sun shades the whole floor a little differently even when the cast shadow has not moved at all.
        /// </summary>
        static bool[] ShadowMask(byte[] rgba, out int shadowPixels)
        {
            (int x0, int y0, int x1, int y1) = ProbeWindow;
            int n = (x1 - x0) * (y1 - y0);
            var lum = new float[n];
            int k = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 4;
                    lum[k++] = 0.2126f * rgba[i] + 0.7152f * rgba[i + 1] + 0.0722f * rgba[i + 2];
                }
            var sorted = (float[])lum.Clone();
            Array.Sort(sorted);
            float lit = sorted[(int)(n * 0.9f)];       // the lit floor, robust to the caster's own bright pixels
            float threshold = 0.80f * lit;
            var mask = new bool[n];
            shadowPixels = 0;
            for (int i = 0; i < n; i++)
                if (mask[i] = lum[i] < threshold) shadowPixels++;
            return mask;
        }

        // A window over the floor to the shadow side of the caster, clear of the sky and of the caster itself.
        static (int, int, int, int) ProbeWindow => (40, 90, 280, 230);

        static int Differences(bool[] a, bool[] b)
        {
            int diff = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
            return diff;
        }
    }
}
