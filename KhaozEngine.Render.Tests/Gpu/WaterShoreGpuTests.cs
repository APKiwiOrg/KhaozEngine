using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The bathymetry path drawn end to end: a sloping beach, a depth field over it, and the two things the field
    /// is supposed to buy - shallows that calm down and a surf band that breaks on them.
    /// <para>
    /// <b>Statistical rather than a golden, and deliberately geometry-free.</b> Every claim below is made by
    /// comparing two RENDERS of the same scene that differ in one knob, so nothing depends on where in the frame
    /// the shore happens to land, on the camera, or on the day a golden was baked. That matters most for the
    /// locality claim: proving "only the shallows changed" from image coordinates would need the projection, while
    /// binding a field that is uniformly DEEP proves it outright - the field is live, the knobs are on, and the
    /// picture has to be the one with no field at all.
    /// </para>
    /// <para>
    /// The sea state is small (two cascades at 64) for the same reason the FFT golden's is: this runs on two
    /// software rasterizers, and what needs proving per backend is that the new binding, the new sampler and the
    /// new branches cross-compile and draw, not that they draw a big ocean.
    /// </para>
    /// </summary>
    public sealed class WaterShoreGpuTests
    {
        const int W = 480, H = 320;

        // A beach running up along +X: ground at -4 at the origin, rising 0.12 per metre, so the water is 4 m deep
        // at x = 0 and dry from x = 33 on. Both the drawn seabed and the depth field are built from these two
        // numbers, which is what makes the field describe the geometry the depth buffer also sees.
        const float GroundAtOrigin = -4f;
        const float Slope = 0.12f;
        const float PlaneHalfExtent = 70f;
        const int BeachTiles = 26;
        const float BeachTileSize = 8f;

        static float GroundY(float x) => GroundAtOrigin + Slope * x;

        static WaterBathymetry SlopedField()
        {
            var field = new WaterBathymetry(128, centerX: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent);
            field.FillFromGround((x, _) => GroundY(x), surfaceY: 0f);
            return field;
        }

        static WaterBathymetry UniformlyDeepField()
        {
            var field = new WaterBathymetry(128, centerX: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent);
            Array.Fill(field.Depths, 400f);
            field.MarkChanged();
            return field;
        }

        static float[] Render(Action<WaterSettings> tune)
        {
            MeshHandle tile = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    tile = scene.LoadMesh(MeshPrimitives.Tile(BeachTileSize, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    WaterSeaState sea = scene.Post.Water.SeaState;
                    sea.Seed = 20260728;
                    sea.CascadeCount = 2;
                    sea.CascadeResolution = 64;
                    scene.Post.Water.SeaState = sea;
                    tune(scene.Post.Water);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));
                    scene.EffectTimeSeconds = 3f;
                },
                drawFrame: scene =>
                {
                    // The beach is many SMALL tiles, each rotated to the ramp angle and dropped onto it. One big
                    // quad would not do: the depth the water pass reconstructs is written per vertex and
                    // interpolated, so across a large perspective triangle the reconstructed seabed drifts far
                    // enough to break the shore fade (see WaterDistanceBandingProbe's note).
                    float angle = MathF.Atan(Slope);
                    for (int gz = 0; gz < BeachTiles; gz++)
                    {
                        for (int gx = 0; gx < BeachTiles; gx++)
                        {
                            float x = (gx - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            float z = (gz - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            scene.Draw(tile,
                                Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(x, GroundY(x), z),
                                new Color(0.42f, 0.38f, 0.30f, 1f));
                        }
                    }
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f,
                        halfExtentX: PlaneHalfExtent));
                },
                frames: 2);
            return GoldenCompare.Downsample(rgba, W, H);
        }

        static (float Mean, float Worst) Difference(float[] a, float[] b)
        {
            double sum = 0;
            float worst = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                float d = MathF.Abs(a[i] - b[i]);
                sum += d;
                worst = MathF.Max(worst, d);
            }
            return ((float)(sum / a.Length), worst);
        }

        /// <summary>Cells that got materially BRIGHTER from a to b. Foam is the whitest thing on the surface, so
        /// this is how "the band added white" is asserted without needing to know where in the frame the shore
        /// landed. A global maximum will not do: the sky and the sand are brighter than any foam and never move,
        /// so the max is theirs in every render.</summary>
        static int Whitened(float[] a, float[] b, float by)
        {
            int cells = 0;
            for (int cell = 0; cell < a.Length / 3; cell++)
            {
                float da = (a[cell * 3] + a[cell * 3 + 1] + a[cell * 3 + 2]) / 3f;
                float db = (b[cell * 3] + b[cell * 3 + 1] + b[cell * 3 + 2]) / 3f;
                if (db - da > by) cells++;
            }
            return cells;
        }

        [GpuFact]
        public void ADepthFieldCalmsTheShallowsAndBreaksSurfOnThem()
        {
            using (GpuDeviceContext probe = GpuDeviceContext.CreateHeadless())
            {
                Assert.True(probe.GpuDevice.Capabilities.SupportsCompute,
                    $"{probe.GpuDevice.Backend} reports no compute support, so the surface would fall back to the " +
                    "procedural path, where the whole depth-driven group is inert by design and this would pass " +
                    "vacuously");
            }

            float[] none = Render(_ => { });
            float[] deep = Render(w => w.Bathymetry = UniformlyDeepField());
            float[] shoaled = Render(w =>
            {
                w.Bathymetry = SlopedField();
                w.SurfStrength = 0f;
            });
            float[] surf = Render(w => w.Bathymetry = SlopedField());

            // 1. LOCALITY. A field is bound and every knob is live, but the water is 400 m deep everywhere, so
            //    tanh(k d) is 1 and the break line is far below the seabed. The picture must be the no-field one.
            (float mean, float worst) sameSea = Difference(none, deep);
            Assert.True(sameSea.worst < GoldenCompare.Tolerance,
                $"a uniformly DEEP depth field moved the render by {sameSea.worst:F4} (mean {sameSea.mean:F4}). " +
                "The taper is supposed to be 1 in deep water, so binding a field must change nothing out there - " +
                "this says the shoaling is global rather than local.");

            // 2. SHOALING. The same field, now sloped, with the surf band OFF: the only thing that can move the
            //    picture is the swell calming as the bottom comes up.
            (float mean, float worst) calmed = Difference(none, shoaled);
            Assert.True(calmed.mean > 0.002f && calmed.worst > 0.03f,
                $"the sloped depth field only moved the surface by {calmed.mean:F4} mean / {calmed.worst:F4} worst " +
                "with the surf band off, which is too little to be the shallows calming down.");

            // 3. SURF. Turning the band on has to change the picture again, on top of the shoaling, and it has to
            //    do it by adding WHITE - foam is the brightest thing on the surface, so the peak brightness has to
            //    rise. A band that merely darkened or tinted the shallows would pass a difference test alone.
            (float mean, float worst) broke = Difference(shoaled, surf);
            Assert.True(broke.worst > 0.05f,
                $"enabling the surf band changed the render by only {broke.worst:F4} at worst, so no surf is being " +
                "drawn at all.");
            int whitened = Whitened(shoaled, surf, 0.05f);
            Assert.True(whitened >= 4,
                $"only {whitened} cells got materially brighter when the surf band was enabled. Foam is the " +
                "whitest thing on the surface, so whatever the band is drawing, it is not foam.");
            Assert.True(Whitened(surf, shoaled, 0.05f) < whitened,
                "the surf band darkened more of the frame than it whitened, which is not what foam does.");
        }

        /// <summary>
        /// The crest-phase lock, which is the difference between a wave crashing and a strip glowing: the band has
        /// to MOVE with the sea. Two frames of wave time apart, with the camera and the whole scene identical, the
        /// surf render must change more than the same two frames change without it.
        /// </summary>
        [GpuFact]
        public void TheSurfBandTravelsWithTheWavesRatherThanSittingStill()
        {
            using (GpuDeviceContext probe = GpuDeviceContext.CreateHeadless())
                Assert.True(probe.GpuDevice.Capabilities.SupportsCompute, "no compute support");

            // The comparison is against SHOALING-ONLY, not against the plain surface. Shoaling flattens the
            // shallows, so it removes motion by design and a plain-surface baseline would be measuring that
            // instead. Holding the taper fixed and toggling only the band leaves the band's own time variation.
            float[] shoalA = RenderAt(1.0f, w => { w.Bathymetry = SlopedField(); w.SurfStrength = 0f; });
            float[] shoalB = RenderAt(3.5f, w => { w.Bathymetry = SlopedField(); w.SurfStrength = 0f; });
            float[] surfA = RenderAt(1.0f, w => w.Bathymetry = SlopedField());
            float[] surfB = RenderAt(3.5f, w => w.Bathymetry = SlopedField());

            float calmMotion = Difference(shoalA, shoalB).Mean;
            float surfMotion = Difference(surfA, surfB).Mean;
            Assert.True(surfMotion > calmMotion * 1.05f,
                $"over the same 2.5 s of wave time the surf render moved {surfMotion:F4} against {calmMotion:F4} " +
                "for the identical scene with the band switched off. A band gated on crest PHASE travels with the " +
                "waves; one that sits still is a painted ring and would add no motion at all.");
        }

        static float[] RenderAt(float time, Action<WaterSettings> tune)
        {
            MeshHandle tile = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    tile = scene.LoadMesh(MeshPrimitives.Tile(BeachTileSize, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    WaterSeaState sea = scene.Post.Water.SeaState;
                    sea.Seed = 20260728;
                    sea.CascadeCount = 2;
                    sea.CascadeResolution = 64;
                    scene.Post.Water.SeaState = sea;
                    tune(scene.Post.Water);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));
                    scene.EffectTimeSeconds = time;
                },
                drawFrame: scene =>
                {
                    float angle = MathF.Atan(Slope);
                    for (int gz = 0; gz < BeachTiles; gz++)
                    {
                        for (int gx = 0; gx < BeachTiles; gx++)
                        {
                            float x = (gx - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            float z = (gz - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            scene.Draw(tile,
                                Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(x, GroundY(x), z),
                                new Color(0.42f, 0.38f, 0.30f, 1f));
                        }
                    }
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f,
                        halfExtentX: PlaneHalfExtent));
                },
                frames: 2);
            return GoldenCompare.Downsample(rgba, W, H);
        }
    }
}
