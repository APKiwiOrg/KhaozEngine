using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// On-device coverage of the FFT ocean's SAMPLING FRAME - the onshore focus, the per-cascade rotation offsets
    /// and the large-scale domain warp - with the features turned ON.
    /// <para>
    /// <b>This exists because every one of those knobs defaults to off.</b> The committed goldens
    /// (<c>scene3d_fftocean</c>, <c>scene3d_water</c>) therefore stay byte-identical through this release, which is
    /// exactly what makes it cheap and exactly the trap
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/298">#298</see> names: a feature whose default is
    /// "unchanged" ships with full green coverage of the path nobody asked for. So the coverage runs the other
    /// way round here. The frame's MATHS is pinned headlessly against its CPU mirror in <c>OceanFocusTests</c>;
    /// what needs a device is that the shaders actually carry it - that the rotation compiles and runs on all
    /// three backends, that it does not produce a NaN the vertex stage then hands to the rasterizer, and that the
    /// default really is the identity through the whole pipeline rather than only in the mirror.
    /// </para>
    /// <para>
    /// Sizes stay small (two cascades at 64, a 320x240 target) because these run on lavapipe and WARP as well as
    /// Metal, and none of the claims need a big picture.
    /// </para>
    /// </summary>
    public sealed class OceanFocusGpuTests
    {
        const int W = 320, H = 240;

        /// <summary>How the sea state is configured before a test's own overrides. Deliberately the shipped
        /// defaults except for the two knobs that make this affordable on a software rasterizer.</summary>
        static void BaseSea(WaterSeaState sea)
        {
            sea.Seed = 20260726;
            sea.CascadeCount = 2;
            sea.CascadeResolution = 64;
        }

        /// <summary>The scene: open water from an ELEVATED vantage, which is where the tiling this release is
        /// about actually reads, framed so the surface fills most of the picture.</summary>
        static byte[] Capture(Action<WaterSeaState> configure)
        {
            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(400f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);

                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    BaseSea(scene.Post.Water.SeaState);
                    configure(scene.Post.Water.SeaState);

                    scene.Camera.Frame(new Vector3(0f, -10f, 0f), new Vector3(0f, 35f, 150f));
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -14f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 300f));
                },
                frames: 2);
        }

        static void RequireCompute()
        {
            using GpuDeviceContext probe = GpuDeviceContext.CreateHeadless();
            Assert.True(probe.GpuDevice.Capabilities.SupportsCompute,
                $"{probe.GpuDevice.Backend} reports no compute support, so this scene would silently fall back to " +
                "the procedural surface and none of these assertions would be about the FFT sampling frame");
        }

        /// <summary>
        /// The surface still reads as a lit sea: enough water-ish cells to fill the frame, and enough brightness
        /// spread across them that it is not a flat sheet. This is what catches a NaN, and it catches it in both
        /// directions. A NaN displacement takes its whole triangle out of the rasterizer, so the cell count
        /// collapses; a NaN in the shading normal drives the fragment to a constant, so the spread collapses.
        /// Neither would fail a compile, and neither is visible in the produced maps, which the rotation does not
        /// touch.
        /// </summary>
        static void AssertReadsAsSea(byte[] rgba, string what)
        {
            float[] grid = GoldenCompare.Downsample(rgba, W, H);
            int cells = 0;
            float min = float.MaxValue, max = float.MinValue;
            for (int cell = 0; cell < grid.Length / 3; cell++)
            {
                float r = grid[cell * 3], g = grid[cell * 3 + 1], b = grid[cell * 3 + 2];
                if (b < r - 0.02f || MathF.Max(r, MathF.Max(g, b)) <= 0.05f) continue;
                cells++;
                float brightness = (r + g + b) / 3f;
                min = MathF.Min(min, brightness);
                max = MathF.Max(max, brightness);
            }
            Assert.True(cells >= 40,
                $"{what}: only {cells} blue-dominant cells (of {grid.Length / 3}). A non-finite vertex " +
                "displacement drops its whole triangle, which is what this count collapses on.");
            Assert.True(max - min >= 0.05f,
                $"{what}: water cells span only brightness {min:F3}..{max:F3}. A rotated frame that flattened the " +
                "surface, or a normal driven to a constant by a NaN, reads exactly like this.");
        }

        static int DifferingBytes(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
            return n;
        }

        // ---- The default really is the identity, all the way through the pipeline ------------------------------

        [GpuFact]
        public void EveryNewKnobAtItsDefaultRendersByteIdenticallyToAnUntouchedSeaState()
        {
            RequireCompute();

            byte[] untouched = Capture(_ => { });
            byte[] written = Capture(sea =>
            {
                // Written EXPLICITLY, not left at their field initializers, so this is a claim about the values
                // rather than about a code path nobody entered. This is the assertion that lets the release skip a
                // golden bake: if it holds, scene3d_fftocean and scene3d_water cannot have moved either.
                sea.OnshoreFocusPoint = Vector2.Zero;
                sea.OnshoreFocusStrength = 0f;
                sea.OnshoreFocusSectors = 12;
                sea.CascadeRotationDegrees = Vector3.Zero;
                sea.DomainWarpMetres = 0f;
                sea.DomainWarpWavelengthMetres = 1250f;
            });

            Assert.Equal(0, DifferingBytes(untouched, written));
        }

        [GpuFact]
        public void AFocusPointWithZeroStrengthIsStillTheUnfocusedSea()
        {
            RequireCompute();

            // Aiming the focus somewhere real while leaving the strength at 0 must change nothing at all. The
            // shader reaches the identity by an early return on the strength rather than by evaluating cos(0), and
            // this is the assertion that the gate is on the strength and not, say, on whether the point is set.
            byte[] baseline = Capture(_ => { });
            byte[] aimed = Capture(sea =>
            {
                sea.OnshoreFocusPoint = new Vector2(140f, -60f);
                sea.OnshoreFocusStrength = 0f;
            });

            Assert.Equal(0, DifferingBytes(baseline, aimed));
        }

        [GpuFact]
        public void TheWarpWavelengthAloneChangesNothingWhileTheAmplitudeIsZero()
        {
            RequireCompute();

            byte[] baseline = Capture(_ => { });
            byte[] tuned = Capture(sea => sea.DomainWarpWavelengthMetres = 400f);
            Assert.Equal(0, DifferingBytes(baseline, tuned));
        }

        [GpuFact]
        public void TheSectorCountAloneChangesNothingWhileTheFocusIsOff()
        {
            RequireCompute();

            // The sector ring only exists to carry the focus, so moving it with no focus set must be inert - and
            // that includes the clamp's two ends, which are the values a settings bag actually gets handed.
            byte[] baseline = Capture(_ => { });
            foreach (int n in new[] { 0, 4, 36, 100000 })
                Assert.Equal(0, DifferingBytes(baseline, Capture(sea => sea.OnshoreFocusSectors = n)));
        }

        // ---- The enabled paths --------------------------------------------------------------------------------

        [GpuFact]
        public void TheOnshoreFocusRotatesTheSurfaceWithoutBreakingIt()
        {
            RequireCompute();

            byte[] baseline = Capture(_ => { });
            byte[] focused = Capture(sea =>
            {
                // The focus point sits ON the drawn plane, which is the degenerate case worth running on a device:
                // the heading toward it is undefined AT it and its gradient is unbounded near it, so a vertex that
                // lands there is exactly where a NaN would enter and take its triangle with it.
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 1f;
            });

            AssertReadsAsSea(focused, "onshore focus at full strength");
            Assert.True(DifferingBytes(baseline, focused) > baseline.Length / 20,
                "turning the onshore focus on barely changed the picture: the rotation is not reaching the maps.");
        }

        [GpuFact]
        public void TheSectorCountChangesTheBlendWithoutChangingWhetherItIsASea()
        {
            RequireCompute();

            // Sectors is a quality knob, not a cost one, and the two ends of its range have to both work: 4 is a
            // coarse ring where the two taps are a quarter turn apart (the widest crossed sea the blend can make)
            // and 36 is fine enough that the pair is a directional spread. Both must render, and they must differ,
            // or the count is not reaching the quantizer.
            byte[] coarse = Capture(sea =>
            {
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 1f;
                sea.OnshoreFocusSectors = 4;
            });
            byte[] fine = Capture(sea =>
            {
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 1f;
                sea.OnshoreFocusSectors = 36;
            });

            AssertReadsAsSea(coarse, "focus at 4 sectors");
            AssertReadsAsSea(fine, "focus at 36 sectors");
            Assert.True(DifferingBytes(coarse, fine) > coarse.Length / 50,
                "the sector count changed nothing: it is not reaching the quantizer.");
        }

        [GpuFact]
        public void APartialFocusStillRendersASea()
        {
            RequireCompute();

            // Half strength is the worst case for the documented seam (see WaterSeaState.OnshoreFocusStrength):
            // the frame jumps by a full turn across the ray running downwind from the focus point. A seam is a
            // heading discontinuity, not a NaN, so the surface either side of it still has to be a surface.
            byte[] partial = Capture(sea =>
            {
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 0.5f;
            });
            AssertReadsAsSea(partial, "onshore focus at half strength");
        }

        [GpuFact]
        public void PerCascadeRotationOffsetsDecorrelateTheLatticesWithoutBreakingTheSurface()
        {
            RequireCompute();

            byte[] baseline = Capture(_ => { });
            byte[] rotated = Capture(sea => sea.CascadeRotationDegrees = new Vector3(0f, 19f, 37f));

            AssertReadsAsSea(rotated, "per-cascade rotation offsets");
            // Cascade 0's offset is 0, so only the finer cascade turns here and the coarse silhouette is largely
            // unmoved. The bar is therefore lower than the focus test's: what is being pinned is that a non-zero
            // offset reaches the sampler at all.
            Assert.True(DifferingBytes(baseline, rotated) > baseline.Length / 100,
                "per-cascade rotation offsets changed nothing: the offsets are not reaching the sampler.");
        }

        [GpuFact]
        public void TheDomainWarpBendsTheSamplingDomainWithoutBreakingTheSurface()
        {
            RequireCompute();

            byte[] baseline = Capture(_ => { });
            byte[] warped = Capture(sea =>
            {
                sea.DomainWarpMetres = 100f;
                sea.DomainWarpWavelengthMetres = 1250f;
            });

            AssertReadsAsSea(warped, "domain warp");
            Assert.True(DifferingBytes(baseline, warped) > baseline.Length / 50,
                "the domain warp changed nothing: the amplitude is not reaching the sampler.");
        }

        [GpuFact]
        public void AllThreeTogetherStillRenderASea()
        {
            RequireCompute();

            // The combination is the intended island configuration, and it is the one place the three interact:
            // the warp bends the domain, the focus turns it per position, and the cascade offsets turn each
            // lattice again on top. Composition errors (a rotation applied to an already-rotated position, a
            // vector left in the wrong frame) show up here rather than in any of the three alone.
            byte[] all = Capture(sea =>
            {
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 1f;
                sea.CascadeRotationDegrees = new Vector3(0f, 19f, 37f);
                sea.DomainWarpMetres = 100f;
                sea.DomainWarpWavelengthMetres = 1250f;
            });
            AssertReadsAsSea(all, "focus + cascade offsets + warp");
        }

        // ---- Determinism ---------------------------------------------------------------------------------------

        [GpuFact]
        public void TheSameSamplingFrameRendersTheSamePictureTwice()
        {
            RequireCompute();

            // Bitwise, not within a tolerance. The frame is a pure function of position and the settings bag, so a
            // difference between two runs would mean something in it depends on state that should not be in the
            // model - and the goldens' whole affordability rests on this surface being reproducible.
            void Configure(WaterSeaState sea)
            {
                sea.OnshoreFocusPoint = new Vector2(0f, -40f);
                sea.OnshoreFocusStrength = 1f;
                sea.CascadeRotationDegrees = new Vector3(0f, 19f, 37f);
                sea.DomainWarpMetres = 100f;
            }

            Assert.Equal(0, DifferingBytes(Capture(Configure), Capture(Configure)));
        }
    }
}
