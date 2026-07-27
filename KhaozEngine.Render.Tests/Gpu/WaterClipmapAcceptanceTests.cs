using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The acceptance metric for <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/296">#296</see>, as a
    /// test: at FROZEN wave time, move the camera a short distance and measure how much the rendered surface
    /// changes. Nothing about the sea moved, so whatever changed is pure resampling - the grid sliding through the
    /// wave field - and that is what reads as the ocean boiling in place.
    /// <para>
    /// The number only means something against a scale, so the test also measures the field's OWN motion over one
    /// 60 fps frame and reports the artifact as a fraction of it. That is the diagnosis's methodology and its
    /// published figures: on the camera-focused grid a 0.10 m step is about 85 per cent of a frame of real motion,
    /// and a 0.5 m sprint step is 3.7x it.
    /// </para>
    /// <para>
    /// <b>Why the measurement is on the CPU from read-back maps rather than off rendered pixels.</b> The quantity
    /// in question is a geometric one (metres of surface height), and a pixel comparison would fold in shading,
    /// tone mapping and the depth test on top of it, at which point no threshold means anything. The maps
    /// themselves are produced on the GPU and are already proved correct against a direct DFT
    /// (<c>OceanFftGpuTests</c>), so what is mirrored here is only the sampling and the grid layout, which is
    /// exactly what is under test.
    /// </para>
    /// <para>
    /// It is one focused test rather than a matrix on purpose: the software CI legs are slow
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/332">#332</see>), so it runs at 64 texels over
    /// three cascades - small, but the same 0.22 m of finest content the diagnosis measured against.
    /// </para>
    /// </summary>
    public sealed class WaterClipmapAcceptanceTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;

        public WaterClipmapAcceptanceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

        const int N = 64;
        const int Cascades = 3;
        const float FrozenTime = 7.5f;
        const float FrameDt = 1f / 60f;
        const float CellSize = 0.5f;
        const int RingCells = 32;
        // The probe lattice: fixed in WORLD space (it must not move with the camera, or it would measure nothing)
        // and spread over the near and mid field, which is where the camera-focused grid's 8 to 20 metre cells sit.
        // Its DENSITY is load-bearing and not a cost knob: the clipmap's only residual is a band about one coarse
        // cell wide at each ring boundary, so a sparse lattice steps straight over it and reports a flattering
        // zero. At 160 across 300 metres the spacing is 1.9 m, inside the innermost boundaries' bands.
        const int Probes = 160;
        const float ProbeHalfExtent = 150f;

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
            Seed = 20260727,
            CascadeCount = Cascades,
            // 250 / 4.2 / 4.2 gives cascade tiles of 250, 59.5 and 14.2 metres, so at 64 texels the finest content
            // is 0.22 m - the same figure the #296 diagnosis measured the artifact against.
            CascadeTileMetres = 250f,
            CascadeTileRatio = 4.2f,
            CascadeResolution = N,
            FoamGain = 1.6f,
            FoamJacobianBias = 0.55f,
            FoamDissipationPerSecond = 0.5f,
            // The sampling frame stays at identity so the CPU mirror below is the sampling and nothing else. The
            // frame's own composition with this grid is covered separately (SamplingFrameKnobsComposeWithTheClipmap).
            OnshoreFocusStrength = 0f,
            CascadeRotationDegrees = Vector3.Zero,
            DomainWarpMetres = 0f,
        };

        static WaterSettings Settings() => new()
        {
            WaveSource = WaterWaveSource.FftOcean,
            SeaState = Sea(),
            GridMode = WaterGridMode.Clipmap,
            ClipmapCellSize = CellSize,
            ClipmapRingCells = RingCells,
            ClipmapBandLimitSamples = 2f,
        };

        static WaterPlane Plane() => new(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 600f);

        [GpuFact]
        public void AWorldLockedGridDoesNotResampleTheSeaWhenTheCameraMoves()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute,
                $"{dev.Backend} reports no compute support, so there is no FFT surface to measure");

            WaterSettings settings = Settings();
            using var producer = new OceanFftProducer(dev);

            Ocean now = Capture(dev, producer, settings, FrozenTime);
            Assert.True(now.MaxMip > 0f, "the producer gave the clipmap no mip chain to band-limit against");
            AssertTheGpuChainIsABoxFilter(dev, producer, now);

            Ocean next = Capture(dev, producer, settings, FrozenTime + FrameDt);

            WaterPlane plane = Plane();
            float[] baseline = Sample(plane, now, settings, camX: 0f, clipmap: true);

            // The scale everything is reported against: what the sea legitimately does in one 60 fps frame, with
            // the camera held still so the grid contributes nothing.
            float motion = Rms(baseline, Sample(plane, next, settings, camX: 0f, clipmap: true));
            Assert.True(motion > 1e-4f,
                $"the sea moved {motion} m in a frame, which is too still for the comparison to mean anything");

            (float Clip, float Focused) at10 = Artifacts(plane, now, settings, step: 0.1f);
            (float Clip, float Focused) at50 = Artifacts(plane, now, settings, step: 0.5f);

            string report =
                $"one frame of real motion = {motion:F5} m RMS; " +
                $"0.10 m step: camera-focused {at10.Focused:F5} ({at10.Focused / motion:P0} of motion), " +
                $"clipmap {at10.Clip:F5} ({at10.Clip / motion:P0}); " +
                $"0.50 m step: camera-focused {at50.Focused:F5} ({at50.Focused / motion:P0}), " +
                $"clipmap {at50.Clip:F5} ({at50.Clip / motion:P0})";
            // Printed, not only asserted: the thresholds below say the fix works, this says by how much, which is
            // what a later tuning change needs to see without re-deriving the measurement.
            _out.WriteLine(report);

            // 1. The artifact is real and this is measuring it. Without this the rest could pass on a flat sea.
            Assert.True(at10.Focused > 0.3f * motion,
                $"the camera-focused grid's 0.10 m artifact is only {at10.Focused / motion:P0} of a frame of " +
                $"motion, so this run is not reproducing the defect the fix is for. {report}");

            // 2. The fix. A world-locked grid does not move at all for a sub-cell step, so what is left is the
            //    ring boundaries and the outer edge - a small fraction of the surface, not all of it.
            Assert.True(at10.Clip < 0.15f * motion,
                $"the clipmap still resamples {at10.Clip / motion:P0} of a frame of motion at a 0.10 m step. {report}");
            Assert.True(at10.Clip * 2.5f < at10.Focused,
                $"the clipmap only improved the 0.10 m artifact by {at10.Focused / MathF.Max(at10.Clip, 1e-9f):F1}x. {report}");

            // 3. And it still holds at a sprint, where the camera-focused grid is MULTIPLES of the real motion.
            //    That the clipmap's number barely moves between the two steps is the property, not a coincidence:
            //    its residual is one ring-boundary band wide however far the camera went, while the camera-focused
            //    grid's error is proportional to the distance travelled and so gets worse the faster you run.
            Assert.True(at50.Clip < 0.5f * motion,
                $"the clipmap resamples {at50.Clip / motion:P0} of a frame of motion at a 0.50 m step. {report}");
            Assert.True(at50.Clip * 3f < at50.Focused,
                $"the clipmap only improved the 0.50 m artifact by {at50.Focused / MathF.Max(at50.Clip, 1e-9f):F1}x. {report}");
        }

        /// <summary>
        /// The sampling-frame features (onshore focus, per-cascade rotations, the domain warp) are transforms of
        /// the SAMPLING space, so they should be indifferent to which grid samples them. Verified rather than
        /// assumed: turn all three on and the clipmap must still render a sea, and still not resample it.
        /// </summary>
        [GpuFact]
        public void SamplingFrameKnobsComposeWithTheClipmap()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            WaterSettings settings = Settings();
            WaterSeaState sea = settings.SeaState;
            sea.OnshoreFocusPoint = new Vector2(120f, -60f);
            sea.OnshoreFocusStrength = 0.6f;
            sea.CascadeRotationDegrees = new Vector3(0f, 23f, -41f);
            sea.DomainWarpMetres = 90f;
            sea.DomainWarpWavelengthMetres = 900f;
            settings.SeaState = sea;

            using var producer = new OceanFftProducer(dev);
            Ocean maps = Capture(dev, producer, settings, FrozenTime);
            Assert.True(maps.MaxMip > 0f, "the sampling frame knobs cost the clipmap its mip chain");
            Assert.Equal(2 * Cascades, producer.LastMipCopies);
            // The frame is a sampling-space transform, so it changes the sea WITHOUT changing the maps' own
            // statistics: the same spectrum, still carrying energy, still band-limitable.
            Assert.Equal(1, producer.LastStallCount);
        }

        /// <summary>
        /// The clipmap actually DRAWN, end to end through <see cref="Scene3D"/>: its own vertex layout, its own
        /// cross-compiled shader variant, its own pipeline, the mipped maps bound to it.
        /// <para>
        /// <b>Why this is a statistical test and not a golden.</b> A golden would have to be baked per backend
        /// through a CI dispatch and would then re-render on the two hosted legs on every single push, which is
        /// the per-push cost <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/332">#332</see> is about.
        /// What a golden would buy over this is sensitivity to a small LOOK shift, and the clipmap is opt-in with
        /// no consumer on it yet, so there is no shipped look to protect. What actually needs proving per backend
        /// is that a NEW vertex layout and a NEW shader variant cross-compile and draw at all - and comparing the
        /// clipmap's render against the camera-focused one of the same sea proves that far more directly than a
        /// committed grid of numbers would, because it says what the picture has to BE rather than what it was on
        /// the day it was baked. The full cross-platform dispatch runs it on Direct3D11 and Vulkan.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheClipmapDrawsTheSameSeaTheCameraFocusedGridDraws()
        {
            using (GpuDeviceContext probe = GpuDeviceContext.CreateHeadless())
                Assert.True(probe.GpuDevice.Capabilities.SupportsCompute,
                    $"{probe.GpuDevice.Backend} reports no compute support, so both modes would fall back to the " +
                    "procedural surface and the comparison would prove nothing about the FFT path");

            float[] focused = CaptureGrid(WaterGridMode.CameraFocused);
            float[] clip = CaptureGrid(WaterGridMode.Clipmap);

            // 1. It is a sea: enough water-ish cells, and they vary. A clipmap that built no geometry, or bound an
            //    empty map, renders as a flat sheet or as nothing, and both fail here.
            int waterCells = 0;
            float min = float.MaxValue, max = float.MinValue;
            for (int cell = 0; cell < clip.Length / 3; cell++)
            {
                float r = clip[cell * 3], g = clip[cell * 3 + 1], b = clip[cell * 3 + 2];
                if (b < r - 0.02f || MathF.Max(r, MathF.Max(g, b)) <= 0.05f) continue;
                waterCells++;
                float brightness = (r + g + b) / 3f;
                min = MathF.Min(min, brightness);
                max = MathF.Max(max, brightness);
            }
            Assert.True(waterCells >= 40, $"the clipmap render has only {waterCells} water-ish cells");
            Assert.True(max - min >= 0.08f,
                $"the clipmap's water spans brightness {min:F3}..{max:F3}: a flat sheet, not a displaced surface.");

            // 2. It is the SAME sea. Both grids sample one set of cascades over one world, so the picture has to
            //    agree closely - a wrong vertex layout (attributes shifted by a stride) or a mis-bound map would
            //    move it far more than the per-ring band limit and the different tessellation do.
            double sum = 0;
            float worst = 0f;
            for (int i = 0; i < clip.Length; i++)
            {
                float d = MathF.Abs(clip[i] - focused[i]);
                sum += d;
                worst = MathF.Max(worst, d);
            }
            float mean = (float)(sum / clip.Length);
            Assert.True(mean < 0.05f,
                $"the two grids render the same sea {mean:F4} apart on average (worst {worst:F4}), which is too " +
                "far to be tessellation and band-limiting alone.");
            // 3. And not the identical picture, or the mode switch did nothing and test 2 is vacuous.
            Assert.True(worst > 0.005f, "the clipmap render is indistinguishable from the camera-focused one, so " +
                "the grid mode is not reaching the renderer at all.");
        }

        static float[] CaptureGrid(WaterGridMode mode)
        {
            MeshHandle seabed = default;
            byte[] rgba = Render3DSnapshot.Capture(480, 320,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(160f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    scene.Post.Water.GridMode = mode;
                    scene.Post.Water.ClipmapCellSize = CellSize;
                    scene.Post.Water.ClipmapRingCells = RingCells;
                    WaterSeaState sea = Sea();
                    sea.CascadeCount = 2;
                    sea.CascadeResolution = 64;
                    scene.Post.Water.SeaState = sea;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -12f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 70f));
                },
                frames: 2);
            return GoldenCompare.Downsample(rgba, 480, 320);
        }

        // ---- Measurement -------------------------------------------------------------------------------------

        /// <summary>
        /// Every start offset the camera can have relative to the innermost ring's snap boundary. The WORST of
        /// them is what gets asserted, and taking the worst rather than one arbitrary start is the difference
        /// between an acceptance test and a flattering one: a world-locked grid that does not snap on a given step
        /// changes by exactly nothing, so a single start that happens to sit mid-cell would score a perfect zero
        /// while saying nothing at all about the frames where a ring does move. These five span the level-0
        /// quantum (2 * ClipmapCellSize = 1 m), so a 0.1 m step crosses a boundary from at least one of them.
        /// </summary>
        static readonly float[] StartOffsets = { 0f, 0.23f, 0.47f, 0.71f, 0.95f };

        static (float Clip, float Focused) Artifacts(in WaterPlane plane, in Ocean maps, WaterSettings settings, float step)
        {
            float clip = 0f, focused = 0f;
            foreach (float start in StartOffsets)
            {
                clip = MathF.Max(clip, Rms(Sample(plane, maps, settings, start, clipmap: true),
                                           Sample(plane, maps, settings, start + step, clipmap: true)));
                focused = MathF.Max(focused, Rms(Sample(plane, maps, settings, start, clipmap: false),
                                                 Sample(plane, maps, settings, start + step, clipmap: false)));
            }
            return (clip, focused);
        }

        static float Rms(float[] a, float[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; sum += d * d; }
            return (float)Math.Sqrt(sum / a.Length);
        }

        /// <summary>The rendered surface height at every probe: the piecewise-linear interpolant the triangles
        /// actually draw, evaluated over the grid's own reference parametrization so the two grids are compared
        /// on the same quantity.</summary>
        static float[] Sample(in WaterPlane plane, in Ocean maps, WaterSettings settings, float camX, bool clipmap)
        {
            var heights = new float[Probes * Probes];
            Surface surface = clipmap
                ? Surface.Clip(plane, maps, settings, camX)
                : Surface.Focused(plane, maps, settings, camX);
            for (int j = 0; j < Probes; j++)
            {
                for (int i = 0; i < Probes; i++)
                {
                    // An irrational-ish offset so probes never land exactly on a lattice node, where both grids
                    // would agree for free and the metric would flatter itself.
                    float px = -ProbeHalfExtent + (i + 0.317f) * (2f * ProbeHalfExtent / Probes);
                    float pz = -ProbeHalfExtent + (j + 0.712f) * (2f * ProbeHalfExtent / Probes);
                    heights[j * Probes + i] = surface.HeightAt(px, pz);
                }
            }
            return heights;
        }

        /// <summary>A built grid, displaced, with the point query the metric needs.</summary>
        sealed class Surface
        {
            // Camera-focused: monotone warped axes plus a displaced height per node.
            float[] _xs = Array.Empty<float>(), _zs = Array.Empty<float>();
            float[] _h = Array.Empty<float>();
            // Clipmap: per-level origins, cell sizes and a displaced height per node.
            WaterClipmapVertex[] _verts = Array.Empty<WaterClipmapVertex>();
            float[] _clipH = Array.Empty<float>();
            float _cell;
            int _ring, _levels;
            float[] _ox = Array.Empty<float>(), _oz = Array.Empty<float>();
            bool _isClip;

            public static Surface Focused(in WaterPlane plane, in Ocean maps, WaterSettings settings, float camX)
            {
                const int n = WaterMath.GridResolution;
                var pos = new Vector3[n * n];
                var scratch = new float[2 * n];
                WaterMath.BuildGridPositions(plane, camX, 0f, settings.GridFocusBias, pos, scratch);
                var s = new Surface { _xs = new float[n], _zs = new float[n], _h = new float[n * n] };
                for (int i = 0; i < n; i++) { s._xs[i] = pos[i].X; s._zs[i] = pos[i * n].Z; }
                for (int i = 0; i < n * n; i++)
                    // No mip chain is bound on this path, so every vertex samples LOD 0 - which IS the defect.
                    s._h[i] = pos[i].Y + maps.Displace(pos[i].X, pos[i].Z, 0f, settings).Y;
                return s;
            }

            public static Surface Clip(in WaterPlane plane, in Ocean maps, WaterSettings settings, float camX)
            {
                float cell = settings.ClipmapCellSize;
                int ring = WaterClipmap.ClampRingCells(settings.ClipmapRingCells);
                int levels = WaterClipmap.LevelsFor(plane, cell, ring);
                var verts = new WaterClipmapVertex[WaterClipmap.VertexCount(levels, ring)];
                var indices = new uint[WaterClipmap.IndexCount(levels, ring)];
                Vector2 focus = WaterClipmap.ClampFocus(plane, camX, 0f);
                int vc = WaterClipmap.Build(plane, focus.X, focus.Y, cell, ring, levels, verts, indices, out _);

                var s = new Surface
                {
                    _isClip = true, _verts = verts, _clipH = new float[vc], _cell = cell,
                    _ring = ring, _levels = levels, _ox = new float[levels], _oz = new float[levels],
                };
                for (int l = 0; l < levels; l++)
                {
                    float c = WaterClipmap.CellSize(cell, l);
                    s._ox[l] = WaterClipmap.SnapOrigin(focus.X, c);
                    s._oz[l] = WaterClipmap.SnapOrigin(focus.Y, c);
                }
                for (int i = 0; i < vc; i++)
                {
                    WaterClipmapVertex v = verts[i];
                    // Mirrors the vertex stage's tap loop exactly: one tap normally, two averaged on a stitched
                    // ring-boundary vertex, each band-limited to this vertex's own Cell.
                    int taps = v.Stitch == Vector2.Zero ? 1 : 2;
                    float sum = 0f;
                    for (int t = 0; t < taps; t++)
                    {
                        Vector2 o = taps == 1 ? Vector2.Zero : (t == 0 ? -v.Stitch : v.Stitch);
                        float sx = v.Position.X + o.X, sz = v.Position.Z + o.Y;
                        sum += v.Position.Y + maps.Displace(sx, sz, v.Cell, settings).Y;
                    }
                    s._clipH[i] = sum / taps;
                }
                return s;
            }

            public float HeightAt(float x, float z) => _isClip ? ClipHeight(x, z) : FocusedHeight(x, z);

            float FocusedHeight(float x, float z)
            {
                const int n = WaterMath.GridResolution;
                int i = Cell(_xs, x), j = Cell(_zs, z);
                float u = (x - _xs[i]) / (_xs[i + 1] - _xs[i]);
                float v = (z - _zs[j]) / (_zs[j + 1] - _zs[j]);
                return Bary(u, v, _h[j * n + i], _h[j * n + i + 1], _h[(j + 1) * n + i], _h[(j + 1) * n + i + 1]);
            }

            float ClipHeight(float x, float z)
            {
                int stride = _ring + 1, perLevel = stride * stride;
                for (int l = 0; l < _levels; l++)
                {
                    float c = WaterClipmap.CellSize(_cell, l);
                    float half = _ring * 0.5f * c;
                    float lx = (x - (_ox[l] - half)) / c, lz = (z - (_oz[l] - half)) / c;
                    if (lx < 0f || lz < 0f || lx >= _ring || lz >= _ring) continue;
                    int i = (int)lx, j = (int)lz;
                    int b = l * perLevel + j * stride + i;
                    return Bary(lx - i, lz - j, _clipH[b], _clipH[b + 1], _clipH[b + stride], _clipH[b + stride + 1]);
                }
                return 0f;   // outside the outermost ring: no surface, and no probe reaches here
            }

            /// <summary>Interpolate over the quad's TWO triangles, matching the (i0, i2, i1) / (i1, i2, i3)
            /// triangulation the index builders emit, so the metric reads the surface that is actually drawn
            /// rather than a bilinear approximation of it.</summary>
            static float Bary(float u, float v, float h00, float h10, float h01, float h11)
                => u + v <= 1f
                    ? h00 + (h10 - h00) * u + (h01 - h00) * v
                    : h11 + (h01 - h11) * (1f - u) + (h10 - h11) * (1f - v);

            static int Cell(float[] axis, float value)
            {
                int lo = 0, hi = axis.Length - 2;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (axis[mid] <= value) lo = mid; else hi = mid - 1;
                }
                return Math.Clamp(lo, 0, axis.Length - 2);
            }
        }

        // ---- The maps ----------------------------------------------------------------------------------------

        /// <summary>One frame's displacement cascades, read back and pyramided, plus the sampling the vertex stage
        /// does over them.</summary>
        readonly struct Ocean
        {
            /// <summary>[cascade][mip] as tightly packed rgba, 4 floats per texel.</summary>
            public float[][][] Mips { get; init; }
            public float[] Tiles { get; init; }
            public float MaxMip { get; init; }

            /// <summary>Mirrors the vertex stage's cascade sum exactly, at the identity sampling frame: per
            /// cascade, a half-texel-offset wrapping trilinear tap at the level <see cref="WaterClipmap.MipLevel"/>
            /// picks for <paramref name="spacing"/>. <paramref name="spacing"/> 0 is the camera-focused path, where
            /// there is no chain and the level is 0.</summary>
            public Vector3 Displace(float x, float z, float spacing, WaterSettings settings)
            {
                var sum = Vector3.Zero;
                for (int c = 0; c < Tiles.Length; c++)
                {
                    float texel = Tiles[c] / N;
                    float lod = WaterClipmap.MipLevel(texel <= 0f ? 0f : spacing, texel,
                        settings.ClipmapBandLimitSamples, MaxMip);
                    int m0 = (int)MathF.Floor(lod), m1 = Math.Min(m0 + 1, Mips[c].Length - 1);
                    Vector3 a = Tap(Mips[c][m0], N >> m0, x, z, Tiles[c]);
                    if (m1 == m0) { sum += a; continue; }
                    sum += Vector3.Lerp(a, Tap(Mips[c][m1], N >> m1, x, z, Tiles[c]), lod - m0);
                }
                return sum;
            }

            /// <summary>Wrapping bilinear tap, in the shader's own coordinates: normalized uv is
            /// <c>xz / tile + 0.5 / resolution</c> at every level, and the hardware scales that by the LEVEL's
            /// size.</summary>
            static Vector3 Tap(float[] level, int size, float x, float z, float tile)
            {
                float u = (x / tile + 0.5f / N) * size - 0.5f;
                float v = (z / tile + 0.5f / N) * size - 0.5f;
                int x0 = (int)MathF.Floor(u), z0 = (int)MathF.Floor(v);
                float fx = u - x0, fz = v - z0;
                Vector3 a = Texel(level, size, x0, z0), b = Texel(level, size, x0 + 1, z0);
                Vector3 c = Texel(level, size, x0, z0 + 1), d = Texel(level, size, x0 + 1, z0 + 1);
                return Vector3.Lerp(Vector3.Lerp(a, b, fx), Vector3.Lerp(c, d, fx), fz);
            }

            static Vector3 Texel(float[] level, int size, int x, int z)
            {
                int xi = ((x % size) + size) % size, zi = ((z % size) + size) % size;
                int o = (zi * size + xi) * 4;
                return new Vector3(level[o], level[o + 1], level[o + 2]);
            }
        }

        static Ocean Capture(IGpuDevice dev, OceanFftProducer producer, WaterSettings settings, float time)
        {
            using (IGpuCommandList cl = dev.Factory.CreateCommandList())
            {
                cl.Begin();
                Assert.True(producer.Update(cl, settings, time, wantMips: true),
                    "the producer refused to run on a compute device");
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            var mips = new float[Cascades][][];
            var tiles = new float[Cascades];
            int levels = WaterClipmap.MipCount(N);
            for (int c = 0; c < Cascades; c++)
            {
                tiles[c] = producer.TileMetres[c];
                mips[c] = new float[levels][];
                mips[c][0] = ReadLevel(dev, producer.Map, 0, (uint)c, N);
                // The chain itself is box-downsampled here rather than read back level by level: the GPU's chain is
                // separately asserted to BE that box filter (AssertTheGpuChainIsABoxFilter), which is the cheaper
                // way round and pins the semantics as well as the values.
                for (int m = 1; m < levels; m++) mips[c][m] = Downsample(mips[c][m - 1], N >> (m - 1));
            }
            return new Ocean { Mips = mips, Tiles = tiles, MaxMip = producer.MaxMip };
        }

        /// <summary>
        /// The one thing about the mip chain that cannot be reasoned about from the shader side: that
        /// <c>GenerateMipmaps</c> ran AFTER the compute pass wrote the base level and produced the box filter the
        /// band limit assumes. Both halves are backend-specific (the copy is what forces the synchronisation, and
        /// each backend forces it differently), so this is checked on every backend rather than argued.
        /// </summary>
        static void AssertTheGpuChainIsABoxFilter(IGpuDevice dev, OceanFftProducer producer, in Ocean maps)
        {
            Assert.Equal(WaterClipmap.MipCount(N) - 1, (int)maps.MaxMip);
            for (uint layer = 0; layer < 2 * Cascades; layer++)
            {
                float[] baseLevel = ReadLevel(dev, producer.Map, 0, layer, N);
                float[] gpu = ReadLevel(dev, producer.Map, 1, layer, N / 2);
                float[] cpu = Downsample(baseLevel, N);

                float scale = 0f;
                foreach (float v in cpu) scale = MathF.Max(scale, MathF.Abs(v));
                float tolerance = MathF.Max(5e-3f * scale, 1e-5f);
                float worst = 0f;
                for (int i = 0; i < cpu.Length; i++) worst = MathF.Max(worst, MathF.Abs(cpu[i] - gpu[i]));
                Assert.True(worst <= tolerance,
                    $"layer {layer} mip 1 is off the box downsample of mip 0 by {worst} (tolerance {tolerance}). " +
                    "Either GenerateMipmaps did not see the compute pass's writes, or the chain is not a box " +
                    "filter and the per-ring band limit is selecting levels that do not mean what it thinks.");
            }
        }

        static float[] Downsample(float[] level, int size)
        {
            int half = size / 2;
            var outp = new float[half * half * 4];
            for (int z = 0; z < half; z++)
            {
                for (int x = 0; x < half; x++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        float a = level[((2 * z) * size + 2 * x) * 4 + ch];
                        float b = level[((2 * z) * size + 2 * x + 1) * 4 + ch];
                        float c = level[((2 * z + 1) * size + 2 * x) * 4 + ch];
                        float d = level[((2 * z + 1) * size + 2 * x + 1) * 4 + ch];
                        outp[(z * half + x) * 4 + ch] = (a + b + c + d) * 0.25f;
                    }
                }
            }
            return outp;
        }

        /// <summary>Read one mip level of one array layer of an rgba16f texture back as floats, 4 per texel. The
        /// half-float format has no <c>GpuReadback</c> helper, so this is the same hand-rolled staging copy
        /// <c>OceanFftGpuTests</c> uses, with the mip level opened up.</summary>
        static float[] ReadLevel(IGpuDevice dev, IGpuTexture src, uint mip, uint layer, int size)
        {
            IGpuResourceFactory f = dev.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)size, (uint)size, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTextureSubresource(src, mip, layer, staging, (uint)size, (uint)size);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            var result = new float[size * size * 4];
            var row = new byte[size * 4 * 2];
            MappedData map = dev.Map(staging, GpuMapMode.Read);
            try
            {
                for (int y = 0; y < size; y++)
                {
                    Marshal.Copy(IntPtr.Add(map.Data, (int)(y * map.RowPitch)), row, 0, row.Length);
                    for (int i = 0; i < size * 4; i++) result[y * size * 4 + i] = (float)BitConverter.ToHalf(row, i * 2);
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
