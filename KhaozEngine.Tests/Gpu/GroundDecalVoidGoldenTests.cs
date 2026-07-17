using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The load-bearing net for the void ground-decal fallback: raw PIXEL A/B pairs over
    /// <see cref="VoidDecalScene"/>, rendering the identical scene with only <c>GroundDecal.VoidFallback</c> flipped
    /// and asserting what each region does.
    /// <para>
    /// WHY RAW PIXELS AND NOT JUST THE COMMITTED GOLDEN. <c>GoldenCompare</c> averages the frame into a 32x18 grid
    /// with a 0.06/channel tolerance, and 11.9.0 proved that grid cannot see fine or sparse detail at all (the
    /// starfield golden passed with the starfield pass commented out entirely - see <c>docs/TODO.md</c>). The ring
    /// here is coarse enough that <c>telegraph_ground_void</c> does bite, and a sabotage check is part of this
    /// release's verification rather than an assumption, but a committed grid can only ever say "the picture moved",
    /// never "the void projected and the strip did not". These A/B pairs say exactly that, and each one FAILS if the
    /// feature is removed, because the pair collapses to identical renders.
    /// </para>
    /// <para>
    /// Named <c>*GoldenTests</c> deliberately: the cross-platform matrix selects on
    /// <c>FullyQualifiedName~Golden</c>, and these must run on D3D11 and Vulkan too. The void path's ray-plane
    /// intersection unprojects at both NDC depth extremes, which is precisely the kind of math a backend's clip
    /// convention could diverge on. Gated on KE_GPU_TESTS.
    /// </para>
    /// </summary>
    public sealed class GroundDecalVoidGoldenTests
    {
        const int W = VoidDecalScene.W, H = VoidDecalScene.H;

        static byte[] Render(bool voidFallback, float voidDim = 0f)
        {
            MeshHandle island = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene => island = VoidDecalScene.Setup(scene),
                drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback, voidDim),
                frames: 2);
        }

        /// <summary>The pixel a world point on the decal's plane projects to. Uses the camera's own WorldToScreen (the
        /// forward inverse of the ScreenToRay convention the void shader mirrors), so the sample lands where the
        /// engine says it lands rather than where this test guesses.</summary>
        static (int x, int y) Pixel(Vector3 world)
        {
            Assert.True(VoidDecalScene.ProjectionCamera().WorldToScreen(world, W, H, out Vector2 p),
                $"{world} must project into the viewport");
            int x = (int)MathF.Round(p.X), y = (int)MathF.Round(p.Y);
            Assert.InRange(x, 0, W - 1);
            Assert.InRange(y, 0, H - 1);
            return (x, y);
        }

        static (byte r, byte g, byte b) At(byte[] rgba, (int x, int y) px)
        {
            int i = (px.y * W + px.x) * 4;
            return (rgba[i], rgba[i + 1], rgba[i + 2]);
        }

        static int Diff((byte r, byte g, byte b) a, (byte r, byte g, byte b) b) =>
            Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

        /// <summary>Is this pixel carrying the ring's pale-blue fill? NOT simply "blue &gt; red": the island's own tint
        /// (0.16, 0.17, 0.20) is already blue-dominant, so that test reads bare cliff as ring and passes vacuously.
        /// The ring's fill is emphatically blue (its b-r gap is ~114 against the island's ~14), so gate on the GAP.</summary>
        static bool IsRing((byte r, byte g, byte b) c) => c.b - c.r > 60;

        [GpuFact]
        public void Golden_void_fallback_paints_the_ring_past_the_islands_edge()
        {
            byte[] off = Render(voidFallback: false);
            byte[] on = Render(voidFallback: true);
            var px = Pixel(VoidDecalScene.VoidSample);

            var cOff = At(off, px);
            var cOn = At(on, px);

            // Off: nothing but the solid background. The ring truncated at the island's edge, which is the bug.
            Assert.True(Diff(cOff, (10, 13, 23)) < 30,
                $"with the fallback off, {VoidDecalScene.VoidSample} must be bare background, got {cOff}");
            // On: the plane projection paints the ring's pale blue over the void.
            Assert.True(Diff(cOn, cOff) > 40, $"the void pixel must change when the fallback is on: {cOff} -> {cOn}");
            Assert.True(IsRing(cOn), $"the void pixel must take the ring's fill, got {cOn}");
        }

        [GpuFact]
        public void Golden_void_fallback_leaves_on_ground_pixels_byte_identical()
        {
            // The zero-neutral contract at its sharpest: not "an unflagged decal is unchanged" (trivially true) but
            // "the very decal that opted IN renders its on-ground half byte-for-byte as before". The base pass never
            // learns about the flag, and this is what proves it on real hardware.
            byte[] off = Render(voidFallback: false);
            byte[] on = Render(voidFallback: true);
            var px = Pixel(VoidDecalScene.GroundSample);

            var cOff = At(off, px);
            Assert.True(IsRing(cOff), $"the ground sample must be ON the painted ring to be meaningful, got {cOff}");
            Assert.Equal(cOff, At(on, px));
        }

        [GpuFact]
        public void Golden_void_fallback_paints_in_front_of_a_camera_facing_cliff()
        {
            // The near-edge case, and the one that is easy to get exactly backwards. This pixel shows the +X cliff
            // face: real geometry, out of the Y band. The tempting rule is "geometry exists, so do not project" - but
            // the plane point here HANGS IN FRONT of that cliff (it is out over the void at the top surface's height,
            // while the cliff recedes below and behind it). Refusing to paint would eat most of the ring's near arc,
            // not leave a thin strip. Whether the plane is visible is a DEPTH question, not a has-geometry question.
            Assert.InRange(VoidDecalScene.CliffFrontSample.X, VoidDecalScene.StripStartX, VoidDecalScene.CliffEndX);

            byte[] off = Render(voidFallback: false);
            byte[] on = Render(voidFallback: true);
            var px = Pixel(VoidDecalScene.CliffFrontSample);

            var cOff = At(off, px);
            var cOn = At(on, px);
            Assert.False(IsRing(cOff), $"with the fallback off this pixel must be bare cliff, got {cOff}");
            Assert.True(IsRing(cOn), $"the ring must paint in front of the cliff it overhangs, got {cOn}");
        }

        [GpuFact]
        public void Golden_void_fallback_refuses_to_x_ray_geometry_in_front_of_the_plane()
        {
            // The mirror, and the reason the fix is a depth COMPARE rather than "always project when out of band". A
            // slab stands on the plane between the eye and the ring's projection. The ring is genuinely behind it and
            // must stay hidden: a sign error in the comparison shows it straight through solid geometry, and this is
            // the only test that would catch that.
            MeshHandle island = default, wall = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene => { island = VoidDecalScene.Setup(scene); wall = VoidDecalScene.LoadWall(scene); },
                drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback: true, wall: wall),
                frames: 2);

            var px = Pixel(VoidDecalScene.BehindWallSample);
            // Anti-vacuity guard. Without the wall this exact pixel IS painted by the fallback, so the assertion
            // below is measuring the occluder and not a spot the ring never reached. Drop this and the test would
            // still pass with the whole feature deleted.
            Assert.True(IsRing(At(Render(voidFallback: true), px)),
                "the wall sample must be painted when the wall is absent, or this test proves nothing");

            var c = At(rgba, px);
            // The wall is red-dominant, the ring blue-dominant. Behind the wall we must see WALL.
            Assert.False(IsRing(c), $"the ring must not x-ray through the wall in front of it, got {c}");
        }

        [GpuFact]
        public void Golden_void_dim_fades_only_the_void_pixels()
        {
            byte[] plain = Render(voidFallback: true, voidDim: 0f);
            byte[] dimmed = Render(voidFallback: true, voidDim: 0.6f);

            var voidPx = Pixel(VoidDecalScene.VoidSample);
            var groundPx = Pixel(VoidDecalScene.GroundSample);

            var vPlain = At(plain, voidPx);
            var vDim = At(dimmed, voidPx);
            // Dimming scales the void alpha, so the pixel composites closer to the dark background: strictly less blue.
            Assert.True(vDim.b < vPlain.b - 10, $"VoidDim must fade the void pixel toward the background: {vPlain} -> {vDim}");

            // ...and must not touch the ground half of the same decal.
            Assert.Equal(At(plain, groundPx), At(dimmed, groundPx));
        }

        [GpuFact]
        public void Golden_void_fallback_keeps_the_disc_flat_across_a_cliff_face()
        {
            // THE FLAT-DISC INVARIANT, stated so it cannot pass vacuously.
            //
            // The decal's downward gate tolerance exists to let it conform into terrain that dips below its authored
            // height. On a CLIFF it misfires: the face's top 0.3 is, at a single pixel with only depth, arithmetically
            // indistinguishable from a 0.3 dip - so the legacy path conforms the decal onto a vertical surface and
            // runs it down the edge, evaluated at the cliff's XZ instead of the plane point's. The geometric normal is
            // the only thing that tells them apart.
            //
            // So: with the normal gate, YTolerance must stop mattering ON A CLIFF entirely. Rendering the same scene
            // at the stock 0.3 and at 0 must agree. And the control proves the test has teeth: without the gate (flag
            // off) those two renders DIFFER, because 0.3 drips and 0 does not.
            byte[] RenderTol(bool voidFallback, float yTol)
            {
                MeshHandle island = default;
                return Render3DSnapshot.Capture(W, H,
                    setup: scene => island = VoidDecalScene.Setup(scene),
                    drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback, yTolerance: yTol),
                    frames: 2);
            }

            int Differing(byte[] a, byte[] b)
            {
                int n = 0;
                for (int i = 0; i < a.Length; i += 4)
                    if (Diff((a[i], a[i + 1], a[i + 2]), (b[i], b[i + 1], b[i + 2])) > 12) n++;
                return n;
            }

            // Control: the legacy path IS tolerance-sensitive on this cliff. If this ever stops being true the scene
            // no longer exercises the wrap-down and the assertion below would pass for the wrong reason.
            int legacyDelta = Differing(RenderTol(false, 0.3f), RenderTol(false, 0f));
            Assert.True(legacyDelta > 400,
                $"the scene must actually exercise the cliff wrap-down or this test is vacuous, got {legacyDelta} px");

            // The fix: with the gate, the tolerance cannot reach the cliff, so the two renders converge.
            int gatedDelta = Differing(RenderTol(true, 0.3f), RenderTol(true, 0f));
            Assert.True(gatedDelta < legacyDelta / 10,
                $"the normal gate must make YTolerance irrelevant on a cliff: {legacyDelta} px legacy vs {gatedDelta} px gated");
        }

        [GpuFact]
        public void Golden_void_fallback_projects_under_a_perspective_camera()
        {
            // The ortho iso camera's ray is constant across pixels, so it would not catch a void path that only works
            // for a constant ray. A perspective camera's ray fans out per pixel and its unprojection needs the real
            // w-divide at both depth extremes. Hardpoint's follow camera is perspective, so this is its case.
            var follow = new FollowCamera3D
            {
                Target = new Vector3(0f, VoidDecalScene.PlaneY, 0f),
                Pitch = 0.75f, Yaw = 0.6f, Distance = 16f, HeightOffset = 1.5f,
                AspectRatio = (float)W / H,
            };

            byte[] Cap(bool voidFallback)
            {
                MeshHandle island = default;
                return Render3DSnapshot.Capture(W, H,
                    setup: scene => { island = VoidDecalScene.Setup(scene); scene.CameraOverride = follow; },
                    drawFrame: scene => VoidDecalScene.Draw(scene, island, voidFallback),
                    frames: 2);
            }

            byte[] off = Cap(false);
            byte[] on = Cap(true);

            // Count blue-dominant (ring-fill) pixels. Under the fallback the ring must paint materially more of the
            // frame, because the whole overhanging remainder appears. A per-pixel probe would need the follow camera's
            // own projection; the population count is the backend-portable statement of the same fact.
            Assert.True(RingPixels(on) > RingPixels(off) + (W * H) / 200,
                $"the perspective render must gain ring pixels under the fallback: {RingPixels(off)} -> {RingPixels(on)}");
        }

        static int RingPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 2] > rgba[i] + 25 && rgba[i + 2] > 60) n++;
            return n;
        }
    }
}
