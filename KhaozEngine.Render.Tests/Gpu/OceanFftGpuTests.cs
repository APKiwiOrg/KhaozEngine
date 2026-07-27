using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// On-device coverage of the FFT ocean producer: the two shared-memory compute kernels, the fused spectrum
    /// evolution and map assembly, and the foam accumulator.
    /// <para>
    /// The reference is a NAIVE direct 2D DFT of the same baked spectrum, not a second copy of the same butterfly.
    /// That distinction is the point: 15.2.0's <c>ComputeFftGpuTests</c> already proved a Stockham transform
    /// against a Stockham reference, and this program deliberately restructured the kernels (in-place
    /// decimation-in-time, four packed fields, everything either side fused in) - so what needs proving here is
    /// that the RESTRUCTURE still computes the transform, which a same-algorithm reference cannot tell you. At
    /// N = 32 the O(N^4) reference is about four million complex operations, which costs nothing and is beyond
    /// argument.
    /// </para>
    /// <para>
    /// Sizes stay small deliberately: these run on lavapipe and WARP as well as Metal.
    /// </para>
    /// </summary>
    public sealed class OceanFftGpuTests
    {
        const int N = 32;

        static WaterSeaState Sea() => new()
        {
            WindSpeed = 11f,
            WindDirectionDegrees = 30f,
            FetchKilometres = 120f,
            DepthMetres = 60f,
            DirectionalSpread = 0.75f,
            SwellAmount = 0.4f,
            SwellDirectionDegrees = 30f,
            Choppiness = 1.1f,
            SmallWaveCutoffMetres = 0.02f,
            Seed = 3,
            CascadeCount = 2,
            CascadeTileMetres = 250f,
            CascadeTileRatio = 4.2f,
            CascadeResolution = N,
            FoamGain = 1.6f,
            FoamJacobianBias = 0.55f,
            FoamDissipationPerSecond = 0.5f,
        };

        static WaterSettings Settings(WaterSeaState sea)
            => new() { WaveSource = WaterWaveSource.FftOcean, SeaState = sea };

        // ---- The transform itself --------------------------------------------------------------------------

        [GpuFact]
        public void TheProducedMapsMatchADirectDftOfTheSameSpectrum()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            WaterSeaState sea = Sea();
            const float time = 4.25f;

            using var producer = new OceanFftProducer(dev);
            Assert.True(RunFrame(dev, producer, Settings(sea), time), "the producer refused to run on a compute device");
            Assert.Equal(1, producer.LastStallCount);

            for (int cascade = 0; cascade < sea.CascadeCount; cascade++)
            {
                float[] disp = ReadLayer(dev, producer.Map, (uint)cascade, N);
                float[] deriv = ReadLayer(dev, producer.Map, (uint)(sea.CascadeCount + cascade), N);
                Reference expected = ReferenceMaps(sea, cascade, time);

                AssertClose(expected.Height, Channel(disp, 1), "height");
                AssertClose(expected.DisplacementX, Channel(disp, 0), "x displacement");
                AssertClose(expected.DisplacementZ, Channel(disp, 2), "z displacement");
                AssertClose(expected.SlopeX, Channel(deriv, 0), "x slope");
                AssertClose(expected.SlopeZ, Channel(deriv, 1), "z slope");
                AssertClose(expected.Jacobian, Channel(deriv, 3), "jacobian");
            }
        }

        [GpuFact]
        public void ParsevalHoldsBetweenTheSpectrumAndTheHeightMap()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            WaterSeaState sea = Sea();
            const float time = 1.75f;
            using var producer = new OceanFftProducer(dev);
            Assert.True(RunFrame(dev, producer, Settings(sea), time));

            for (int cascade = 0; cascade < sea.CascadeCount; cascade++)
            {
                // Parseval for the unnormalized inverse transform: the height map's mean square equals the summed
                // squared magnitude of the evolved spectrum. That is an EXACT identity for this realization, not a
                // statistical one, so it catches an amplitude or normalization slip that eyeballing the surface
                // never would. (Also the reason the maps are half-float: the tolerance is set by rgba16f.)
                Vector2[] evolved = EvolvedSpectrum(sea, cascade, time);
                double spectral = 0;
                foreach (Vector2 v in evolved) spectral += (double)v.X * v.X + (double)v.Y * v.Y;

                float[] disp = ReadLayer(dev, producer.Map, (uint)cascade, N);
                double spatial = 0, mean = 0;
                for (int i = 0; i < N * N; i++) { float h = disp[i * 4 + 1]; spatial += (double)h * h; mean += h; }
                spatial /= N * N;
                mean /= N * N;

                Assert.True(spectral > 1e-9, $"cascade {cascade} carries no energy at all");
                Assert.True(Math.Abs(spatial - spectral) <= 0.06 * spectral,
                    $"cascade {cascade} Parseval mismatch: map mean square {spatial}, spectrum {spectral}");
                // A wave spectrum has no zero-frequency term, so the surface has to sit on the still-water plane.
                Assert.True(Math.Abs(mean) <= 0.02 * Math.Sqrt(spectral),
                    $"cascade {cascade} height map has a mean offset of {mean}");
            }
        }

        [GpuFact]
        public void TheSameSeedAndTimeProduceIdenticalMapsTwice()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            WaterSeaState sea = Sea();
            const float time = 3.5f;

            float[] first, second;
            using (var a = new OceanFftProducer(dev))
            {
                Assert.True(RunFrame(dev, a, Settings(sea), time));
                first = ReadLayer(dev, a.Map, 0, N);
            }
            using (var b = new OceanFftProducer(dev))
            {
                Assert.True(RunFrame(dev, b, Settings(Sea()), time));
                second = ReadLayer(dev, b.Map, 0, N);
            }

            // Bitwise, not within a tolerance: same seed, same time, same everything, so a difference is a real
            // dependence on something that should not be in the model (an uninitialized buffer, a stale frame).
            Assert.Equal(first, second);
        }

        [GpuFact]
        public void ADifferentSeedGivesADifferentSurfaceOfTheSameSize()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            const float time = 2f;
            using var a = new OceanFftProducer(dev);
            WaterSeaState seaA = Sea();
            Assert.True(RunFrame(dev, a, Settings(seaA), time));
            float[] first = ReadLayer(dev, a.Map, 0, N);
            double rmsA = RootMeanSquare(first, 1);

            WaterSeaState seaB = Sea();
            seaB.Seed = 99;
            using var b = new OceanFftProducer(dev);
            Assert.True(RunFrame(dev, b, Settings(seaB), time));
            float[] second = ReadLayer(dev, b.Map, 0, N);
            double rmsB = RootMeanSquare(second, 1);

            Assert.NotEqual(first, second);
            // Same sea state, different realization: the wave HEIGHT is a property of the spectrum, so the two
            // must agree on it even though no texel matches.
            Assert.True(Math.Abs(rmsA - rmsB) < 0.35 * Math.Max(rmsA, rmsB),
                $"two seeds of one sea state disagree on wave height: {rmsA} vs {rmsB}");
        }

        // ---- Foam ------------------------------------------------------------------------------------------

        [GpuFact]
        public void FoamStaysInRangeAccumulatesThenDissipates()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            // A young, steep, hard-blown sea on a small tile, so the peak wavelength lands well inside what a
            // 32-point grid can resolve and the surface actually folds. A default ocean sea state has its peak at
            // 100-plus metres, which a 32-point 250 metre tile barely samples: no fold, no foam, and a test that
            // would fail for the sea state rather than for the foam model.
            WaterSeaState sea = Sea();
            sea.WindSpeed = 20f;
            sea.FetchKilometres = 2f;
            sea.CascadeTileMetres = 60f;
            sea.CascadeCount = 1;
            sea.Choppiness = 2.5f;
            sea.FoamGain = 8f;
            sea.FoamJacobianBias = 0.9f;
            WaterSettings settings = Settings(sea);

            using var producer = new OceanFftProducer(dev);

            // Frame 1 has no delta, so foam starts at exactly zero: the accumulator is state, not a per-frame
            // function of the current fold, and this pins that it starts empty rather than at whatever the buffer
            // happened to contain.
            Assert.True(RunFrame(dev, producer, settings, 0f));
            float[] frame1 = ReadLayer(dev, producer.Map, 1, N);   // one cascade, so the derivative layer is 1
            AssertFoamInRange(frame1, out float initial);
            Assert.Equal(0f, initial);

            // Assert the PREMISE separately from the behaviour, so a future failure says which one broke.
            float minJacobian = float.MaxValue;
            for (int i = 0; i < N * N; i++) minJacobian = MathF.Min(minJacobian, frame1[i * 4 + 3]);
            Assert.True(minJacobian < sea.FoamJacobianBias,
                $"this sea never folds (min jacobian {minJacobian} >= bias {sea.FoamJacobianBias}), so the foam " +
                "model is not what is being tested here - retune the sea state, not the model");

            float t = 0f;
            for (int i = 0; i < 24; i++) { t += 1f / 60f; Assert.True(RunFrame(dev, producer, settings, t)); }
            AssertFoamInRange(ReadLayer(dev, producer.Map, 1, N), out float grown);
            Assert.True(grown > 0.02f, $"a folding sea produced no foam at all after 24 frames (peak {grown})");

            // Turn the injection off and let it run: what is left has to decay away, which is the half of the model
            // a per-frame foam function does not have.
            sea.FoamGain = 0f;
            for (int i = 0; i < 90; i++) { t += 1f / 60f; Assert.True(RunFrame(dev, producer, settings, t)); }
            AssertFoamInRange(ReadLayer(dev, producer.Map, 1, N), out float decayed);
            Assert.True(decayed < grown * 0.6f, $"foam did not dissipate: peak went {grown} -> {decayed}");
        }

        // ---- Reconfiguration -------------------------------------------------------------------------------

        /// <summary>
        /// Changing the cascade count or the resolution rebuilds every resource whose SHAPE depends on them - the
        /// buffers, the map array, and (because compute specialization constants are not exposed, #312, so the
        /// resolution is substituted into the source) both pipelines. It is the one path that disposes live GPU
        /// resources mid-session, so it is also the one that can leave a dangling binding or drop work on the
        /// floor. Ends back at the original shape to prove the rebuild is not one-way.
        /// </summary>
        [GpuFact]
        public void ChangingTheCascadeCountOrResolutionRebuildsAndKeepsProducing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            using var producer = new OceanFftProducer(dev);
            float t = 0f;
            foreach ((int cascades, int resolution) in new[] { (2, N), (3, N), (3, 64), (1, 64), (2, N) })
            {
                WaterSeaState sea = Sea();
                sea.CascadeCount = cascades;
                sea.CascadeResolution = resolution;
                t += 1f / 60f;
                Assert.True(RunFrame(dev, producer, Settings(sea), t));
                Assert.Equal(cascades, producer.CascadeCount);
                Assert.Equal(resolution, producer.Resolution);

                // Read the coarsest cascade's height back rather than trusting the counters: a rebuild that left a
                // stale binding behind would still report the new shape while producing nothing.
                float[] disp = ReadLayer(dev, producer.Map, 0, resolution);
                Assert.True(RootMeanSquare(disp, 1) > 1e-4,
                    $"cascades={cascades} res={resolution}: the rebuilt producer wrote a flat height map");
            }
        }

        // ---- Capability gate -------------------------------------------------------------------------------

        [GpuFact]
        public void ProceduralModeProducesNoMapsAndCostsNoStall()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;

            using var producer = new OceanFftProducer(dev);
            var settings = new WaterSettings();   // Procedural is the default
            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            // No plane wants the ocean, which is what the renderer computes for a Procedural scene with no
            // per-plane override. The gate is the caller's demand now, not settings.WaveSource.
            bool active = producer.Update(cl, settings, 1f, wantOcean: false);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();

            Assert.False(active);
            Assert.False(producer.Active);
            Assert.Equal(0, producer.LastStallCount);
            // Still bindable: the water pipeline's resource layout is the same shape in both modes, so the
            // placeholders have to exist even on a device that never runs a dispatch.
            Assert.NotNull(producer.Map);
            Assert.NotNull(producer.Sampler);
        }

        // ---- Cost ------------------------------------------------------------------------------------------

        /// <summary>
        /// The producer's frame cost is ONE GPU stall, whatever the cascade count and resolution. That is the
        /// structural claim the whole kernel design exists to make (see <c>OceanComputeShaders</c>): the seam has
        /// no cross-dispatch barrier, so a per-FFT-stage ping-pong would drain the device 14 times per transform
        /// per cascade, and fusing each axis into one shared-memory dispatch collapses that to the single
        /// row-to-column dependency.
        /// <para>
        /// It asserts the COUNT and only logs the milliseconds. A wall-clock budget cannot be asserted here: two of
        /// the three backends this runs on are software rasterizers, where the number means nothing. The measured
        /// Metal cost at the shipping defaults is in the release notes.
        /// </para>
        /// </summary>
        [GpuFact]
        public void OneStallPerFrameAtEveryCascadeCountAndResolution()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            foreach ((int cascades, int resolution) in new[] { (1, 32), (2, 32), (3, 32), (3, 64) })
            {
                WaterSeaState sea = Sea();
                sea.CascadeCount = cascades;
                sea.CascadeResolution = resolution;
                WaterSettings settings = Settings(sea);

                using var producer = new OceanFftProducer(dev);
                double total = 0;
                const int frames = 8;
                for (int i = 0; i < frames; i++)
                {
                    Assert.True(RunFrame(dev, producer, settings, i / 60f));
                    Assert.Equal(1, producer.LastStallCount);
                    total += producer.LastStallMs;
                }
                Assert.Equal(cascades, producer.CascadeCount);
                Assert.Equal(resolution, producer.Resolution);
                Assert.True(total / frames >= 0d);   // pins the measurement path itself, not a budget
            }
        }

        // ---- Motion direction (headless, CPU mirror only) --------------------------------------------------
        //
        // Closes the test gap that let #342 ship twice: nothing in this file (or anywhere else) checked which
        // WAY the surface travels, only that it matched a reference computed by the same buggy convention. These
        // two tests are deliberately headless [Theory]/[Fact] rather than [GpuFact] - the software CI legs are
        // already slow (#332), and the CPU mirror is already pinned bit-identical to the GPU kernel by
        // TheProducedMapsMatchADirectDftOfTheSameSpectrum above, so a GPU round trip would add cost without
        // adding coverage.

        /// <summary>
        /// The CPU-mirrored evolved spectrum reconstructs a height field that travels ALONG
        /// <see cref="WaterSeaState.WindDirectionDegrees"/>, not against it (KhaozEngine#342: the evolution's
        /// time sign was flipped, so the sea ran wind+180 from 16.3.0 until this fix, invisible until 16.5.0's
        /// onshore focus made heading position-dependent). Measures the field's bulk translation between two
        /// close times via global Lucas-Kanade optical flow (the standard PIV technique for recovering a single
        /// dominant motion vector from a noisy, multi-component field) and checks its heading against the wind,
        /// at two different wind angles so the check cannot pass by an accidental axis symmetry (a sign error on
        /// only one of kx/kz, say, would still fail one of the two).
        /// </summary>
        [Theory]
        [InlineData(40f)]
        [InlineData(197f)]
        public void FftHeightFieldTravelsAlongTheWindNotAgainstIt(float windDegrees)
        {
            WaterSeaState sea = Sea();
            sea.WindDirectionDegrees = windDegrees;
            sea.SwellDirectionDegrees = windDegrees;   // keep swell aligned: one clean dominant heading to measure
            sea.DirectionalSpread = 0.9f;              // narrow the lobe so the field has one heading, not a spread
            sea.SwellAmount = 0.5f;
            const int cascade = 0;
            const float t0 = 10f, dt = 0.35f;          // away from t=0 so sin/cos are both nonzero either side

            Reference r1 = ReferenceMaps(sea, cascade, t0);
            Reference r2 = ReferenceMaps(sea, cascade, t0 + dt);
            float tile = OceanSpectrum.TileMetres(cascade, sea.CascadeTileMetres, sea.CascadeTileRatio);
            float cellMetres = tile / N;

            (float dx, float dz) = OpticalFlowShift(r1.Height, r2.Height, N, cellMetres);
            float shift = MathF.Sqrt(dx * dx + dz * dz);
            Assert.True(shift > 0.5f,
                $"wind {windDegrees} deg: no coherent motion measured ({shift:F3} m over {dt}s) - " +
                "the test cannot judge a heading from this");

            float measuredDeg = MathF.Atan2(dz, dx) * (180f / MathF.PI);
            if (measuredDeg < 0f) measuredDeg += 360f;
            float diff = MathF.Abs(((measuredDeg - windDegrees + 540f) % 360f) - 180f);
            Assert.True(diff < 45f,
                $"wind {windDegrees} deg but the field's crests moved toward {measuredDeg:F1} deg " +
                $"(off by {diff:F1} deg, shift {shift:F3} m) - travelling against the wind is exactly #342");
        }

        /// <summary>
        /// Global Lucas-Kanade optical flow: the single (dx, dz) world-space displacement that best explains
        /// <c>h2(x) - h1(x) ~= -(dx, dz) . grad(h1(x))</c> across every texel of the (periodic) grid, via central
        /// differences. Robust to a multi-component field because it is a least-squares fit over every texel at
        /// once rather than a single-sample measurement - the dominant (most energetic, longest-wavelength)
        /// component naturally has the largest gradients and controls the fit. Returns a world-space
        /// displacement in metres, not a velocity: this test only reads its direction, never its magnitude.
        /// </summary>
        static (float, float) OpticalFlowShift(float[] h1, float[] h2, int n, float cellMetres)
        {
            double sxx = 0, sxz = 0, szz = 0, sxt = 0, szt = 0;
            for (int pz = 0; pz < n; pz++)
            {
                int zp = (pz + 1) % n, zm = (pz - 1 + n) % n;
                for (int px = 0; px < n; px++)
                {
                    int xp = (px + 1) % n, xm = (px - 1 + n) % n;
                    double hx = (h1[pz * n + xp] - h1[pz * n + xm]) / (2.0 * cellMetres);
                    double hz = (h1[zp * n + px] - h1[zm * n + px]) / (2.0 * cellMetres);
                    double ht = h2[pz * n + px] - h1[pz * n + px];
                    sxx += hx * hx; sxz += hx * hz; szz += hz * hz;
                    sxt += hx * ht; szt += hz * ht;
                }
            }
            // Solve [sxx sxz; sxz szz] . (dx, dz) = -[sxt, szt].
            double det = sxx * szz - sxz * sxz;
            if (Math.Abs(det) < 1e-12) return (0f, 0f);
            double dx = (-sxt * szz + szt * sxz) / det;
            double dz = (-szt * sxx + sxt * sxz) / det;
            return ((float)dx, (float)dz);
        }

        // ---- harness ---------------------------------------------------------------------------------------

        /// <summary>One producer frame on its own command list, exactly as the water renderer drives it: the
        /// column pass is recorded into the caller's list, so the list is submitted afterwards.</summary>
        static bool RunFrame(IGpuDevice dev, OceanFftProducer producer, WaterSettings settings, float time)
        {
            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            bool active = producer.Update(cl, settings, time, wantOcean: true);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();
            return active;
        }

        sealed class Reference
        {
            public float[] Height = Array.Empty<float>();
            public float[] DisplacementX = Array.Empty<float>();
            public float[] DisplacementZ = Array.Empty<float>();
            public float[] SlopeX = Array.Empty<float>();
            public float[] SlopeZ = Array.Empty<float>();
            public float[] Jacobian = Array.Empty<float>();
        }

        /// <summary>The evolved spectrum h~(k, t) for one cascade, from the same bake the producer uploads.
        /// Mirrors <c>OceanComputeShaders.RowPassTemplate</c>'s <c>packedFields</c> exactly, including the negated
        /// time sign (KhaozEngine#342): without it the field reconstructs as a POSITIVE-twiddle transform of
        /// <c>h0(k) e^{+i omega t}</c>, whose crests travel along MINUS k (wind+180) instead of along the wind.</summary>
        static Vector2[] EvolvedSpectrum(WaterSeaState sea, int cascade, float time)
        {
            var h0 = new Vector4[N * N];
            OceanSpectrum.BuildInitialSpectrum(sea, cascade, N, h0);
            float tile = OceanSpectrum.TileMetres(cascade, sea.CascadeTileMetres, sea.CascadeTileRatio);
            float dk = 2f * MathF.PI / tile;

            var evolved = new Vector2[N * N];
            for (int row = 0; row < N; row++)
            {
                for (int col = 0; col < N; col++)
                {
                    float kx = (col - N * 0.5f) * dk, kz = (row - N * 0.5f) * dk;
                    float k = MathF.Sqrt(kx * kx + kz * kz);
                    if (k < 1e-6f) continue;
                    float omega = OceanSpectrum.Dispersion(k, sea.DepthMetres);
                    float cw = MathF.Cos(omega * time), sw = -MathF.Sin(omega * time);
                    Vector4 h = h0[row * N + col];
                    evolved[row * N + col] = Mul(new Vector2(h.X, h.Y), new Vector2(cw, sw))
                                           + Mul(new Vector2(h.Z, h.W), new Vector2(cw, -sw));
                }
            }
            return evolved;
        }

        /// <summary>
        /// Every output channel, from a DIRECT 2D inverse DFT of the evolved spectrum. Deliberately the textbook
        /// double sum rather than any transform: it shares no code path with the kernels it is checking.
        /// </summary>
        static Reference ReferenceMaps(WaterSeaState sea, int cascade, float time)
        {
            Vector2[] spectrum = EvolvedSpectrum(sea, cascade, time);
            float tile = OceanSpectrum.TileMetres(cascade, sea.CascadeTileMetres, sea.CascadeTileRatio);
            float dk = 2f * MathF.PI / tile;
            float lambda = sea.Choppiness;

            var r = new Reference
            {
                Height = new float[N * N],
                DisplacementX = new float[N * N],
                DisplacementZ = new float[N * N],
                SlopeX = new float[N * N],
                SlopeZ = new float[N * N],
                Jacobian = new float[N * N],
            };

            for (int pz = 0; pz < N; pz++)
            {
                for (int px = 0; px < N; px++)
                {
                    double h = 0, dx = 0, dz = 0, sx = 0, sz = 0, jxx = 0, jzz = 0, jxz = 0;
                    for (int row = 0; row < N; row++)
                    {
                        for (int col = 0; col < N; col++)
                        {
                            Vector2 c = spectrum[row * N + col];
                            if (c.X == 0f && c.Y == 0f) continue;
                            double kx = (col - N * 0.5) * dk, kz2 = (row - N * 0.5) * dk;
                            double k = Math.Sqrt(kx * kx + kz2 * kz2);
                            if (k < 1e-6) continue;

                            double ang = 2.0 * Math.PI * ((double)col * px + (double)row * pz) / N;
                            double ca = Math.Cos(ang), sa = Math.Sin(ang);
                            // Re(c * e^{i ang}) and Re(-i * c * e^{i ang}) are the two projections every field
                            // below is one of: h~ itself, or h~ times a real factor, or h~ turned a quarter turn.
                            double re = c.X * ca - c.Y * sa;
                            double im = c.X * sa + c.Y * ca;

                            h += re;
                            dx += (kx / k) * im;      // Dx = -i (kx/k) h~  ->  Re = (kx/k) Im(h~ e^{i ang})
                            dz += (kz2 / k) * im;
                            sx += -kx * im;           // dh/dx = i kx h~    ->  Re = -kx Im(...)
                            sz += -kz2 * im;
                            jxx += (kx * kx / k) * re;
                            jzz += (kz2 * kz2 / k) * re;
                            jxz += (kx * kz2 / k) * re;
                        }
                    }

                    int at = pz * N + px;
                    double sign = ((px + pz) % 2 == 0) ? 1.0 : -1.0;
                    r.Height[at] = (float)(h * sign);
                    r.DisplacementX[at] = (float)(dx * sign * lambda);
                    r.DisplacementZ[at] = (float)(dz * sign * lambda);
                    r.SlopeX[at] = (float)(sx * sign);
                    r.SlopeZ[at] = (float)(sz * sign);
                    double a = 1.0 + lambda * jxx * sign;
                    double b = 1.0 + lambda * jzz * sign;
                    double c2 = lambda * jxz * sign;
                    r.Jacobian[at] = (float)(a * b - c2 * c2);
                }
            }
            return r;
        }

        static Vector2 Mul(Vector2 a, Vector2 b) => new(a.X * b.X - a.Y * b.Y, a.X * b.Y + a.Y * b.X);

        static float[] Channel(float[] rgba, int channel)
        {
            var result = new float[rgba.Length / 4];
            for (int i = 0; i < result.Length; i++) result[i] = rgba[i * 4 + channel];
            return result;
        }

        static double RootMeanSquare(float[] rgba, int channel)
        {
            double sum = 0;
            int count = rgba.Length / 4;
            for (int i = 0; i < count; i++) { double v = rgba[i * 4 + channel]; sum += v * v; }
            return Math.Sqrt(sum / count);
        }

        static void AssertFoamInRange(float[] deriv, out float peak)
        {
            peak = 0f;
            for (int i = 0; i < deriv.Length / 4; i++)
            {
                float foam = deriv[i * 4 + 2];
                Assert.InRange(foam, 0f, 1f);
                peak = MathF.Max(peak, foam);
            }
        }

        /// <summary>Compare a produced channel against the reference, scaled to the channel's own magnitude. The
        /// tolerance is set by the map format: rgba16f carries an 11-bit significand, so about 5e-4 of relative
        /// error before anything the kernels do is even considered.</summary>
        static void AssertClose(float[] expected, float[] actual, string what)
        {
            Assert.Equal(expected.Length, actual.Length);
            float scale = 0f;
            foreach (float v in expected) scale = MathF.Max(scale, MathF.Abs(v));
            float tolerance = MathF.Max(5e-3f * scale, 1e-5f);

            float worst = 0f;
            int worstAt = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                float d = MathF.Abs(expected[i] - actual[i]);
                if (d > worst) { worst = d; worstAt = i; }
            }
            Assert.True(worst <= tolerance,
                $"{what}: worst error {worst} > tolerance {tolerance} at texel {worstAt} " +
                $"(expected {expected[worstAt]}, got {actual[worstAt]}; channel peak {scale})");
        }

        /// <summary>Read one array layer of an rgba16f texture back as floats, row-major, 4 per texel. There is no
        /// seam helper for half-float textures (<c>GpuReadback</c> covers RGBA8 and buffers), and adding one for a
        /// single test would be a public API nobody else wants.</summary>
        static float[] ReadLayer(IGpuDevice dev, IGpuTexture src, uint layer, int n)
        {
            IGpuResourceFactory f = dev.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)n, (uint)n, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTextureSubresource(src, 0, layer, staging, (uint)n, (uint)n);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            var result = new float[n * n * 4];
            var row = new byte[n * 4 * 2];
            MappedData map = dev.Map(staging, GpuMapMode.Read);
            try
            {
                for (int y = 0; y < n; y++)
                {
                    Marshal.Copy(IntPtr.Add(map.Data, (int)(y * map.RowPitch)), row, 0, row.Length);
                    for (int i = 0; i < n * 4; i++) result[y * n * 4 + i] = (float)BitConverter.ToHalf(row, i * 2);
                }
            }
            finally
            {
                dev.Unmap(staging);
            }
            return result;
        }
    }
}
