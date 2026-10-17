using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Texture detail under a light that never changes, where the release of a still pixel whose shading changed must
    /// leave the resolve as it was (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 33). The release reads a pixel as still
    /// below <c>LockMotionStartPixels</c>, so a slow pan is still to it, and it runs at Quality, where the jitter aliases
    /// detail finer than an internal texel. Each run renders <see cref="FirstMeasured"/> frames and measures
    /// <see cref="Measured"/> more over the view less a <see cref="Margin"/> pixel border, against the supersampled
    /// reference of the same frames: the mean absolute luma error, and the temporal error, how far each pixel's change
    /// from frame to frame lies from the reference's (<see cref="TemporalAcceptance.TemporalError"/>). Both stay
    /// within <see cref="Tolerance"/> of the resolve's figures before the release on the same device class, measured on
    /// each and listed per class, since the software rasterisers round differently from hardware. With either of the
    /// release's two change tests alone deciding at Quality, the still wall of one display pixel texels measured 15
    /// and 44 percent above its figures, so both must read a change where the frame upscales.
    /// </summary>
    public sealed class TemporalStableDetailGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, FirstMeasured = 80, Measured = 8, Margin = 8;
        const float OrthoSize = 1.5f;
        const double Tolerance = 0.1;

        /// <summary>A run's figures: the mean absolute luma error against the reference and the temporal error.</summary>
        internal readonly record struct Figures(double Error, double TemporalError);

        readonly record struct Pan(string Name, TemporalUpscale Preset, float PixelsPerFrame);

        readonly record struct Fine(string Name, float TexelPixels);

        static readonly Pan[] Pans =
        {
            new("Quality pan 0.3 px", TemporalUpscale.Quality, 0.3f),
            new("Quality pan 0.8 px", TemporalUpscale.Quality, 0.8f),
            new("Native pan 0.3 px", TemporalUpscale.Native, 0.3f),
            new("Native pan 0.8 px", TemporalUpscale.Native, 0.8f),
        };

        static readonly Fine[] Fines =
        {
            new("Quality still, 1 px texels", 1f),
            new("Quality still, 0.5 px texels", 0.5f),
        };

        // The resolve's figures before the release, per device class, keyed by case name, from the hosted legs. Metal
        // on the dev machine measured the hardware figures too. With the release, hardware measures every pan within
        // 0.1 percent of these, and the still wall error 0.00548 and 0.00724 and temporal error 0.00112 and 0.00098.
        static readonly Dictionary<string, Figures> Hardware = new()
        {
            ["Quality pan 0.3 px"] = new(0.01448, 0.00612),
            ["Quality pan 0.8 px"] = new(0.01346, 0.01417),
            ["Native pan 0.3 px"] = new(0.01154, 0.00529),
            ["Native pan 0.8 px"] = new(0.01058, 0.01190),
            ["Quality still, 1 px texels"] = new(0.00535, 0.00105),
            ["Quality still, 0.5 px texels"] = new(0.00721, 0.00096),
        };

        static readonly Dictionary<string, Figures> Warp = new()
        {
            ["Quality pan 0.3 px"] = new(0.01448, 0.00609),
            ["Quality pan 0.8 px"] = new(0.01345, 0.01413),
            ["Native pan 0.3 px"] = new(0.01158, 0.00526),
            ["Native pan 0.8 px"] = new(0.01059, 0.01187),
            ["Quality still, 1 px texels"] = new(0.00534, 0.00105),
            ["Quality still, 0.5 px texels"] = new(0.00720, 0.00094),
        };

        static readonly Dictionary<string, Figures> Lavapipe = new()
        {
            ["Quality pan 0.3 px"] = new(0.01439, 0.00611),
            ["Quality pan 0.8 px"] = new(0.01351, 0.01420),
            ["Native pan 0.3 px"] = new(0.01154, 0.00529),
            ["Native pan 0.8 px"] = new(0.01064, 0.01192),
            ["Quality still, 1 px texels"] = new(0.00543, 0.00105),
            ["Quality still, 0.5 px texels"] = new(0.00714, 0.00098),
        };

        static Dictionary<string, Figures> Baseline =>
            TemporalDeviceClass.Warp ? Warp : TemporalDeviceClass.Lavapipe ? Lavapipe : Hardware;

        static PixelRect Region => new(Margin, Margin, W - Margin, H - Margin);

        [GpuFact]
        public void A_slow_pan_over_the_textured_wall_resolves_as_before_the_release()
        {
            var failures = new List<string>();
            foreach (Pan c in Pans)
            {
                var stage = new FrontStage(W, H, OrthoSize);
                void Draw(Scene3D s, int n)
                {
                    s.Camera.Target = new Vector3(n * c.PixelsPerFrame * stage.PixelWorld, 0f, 0f);
                    stage.TexturedWall(s);
                }

                byte[][] reference = TemporalAcceptance.ReferenceSequence(W, H,
                    s => stage.Setup(s, AntiAliasing.Temporal, TemporalUpscale.Native), Draw, FirstMeasured, Measured);
                byte[][] frames = TemporalAcceptance.Sequence(W, H, s => stage.Setup(s, AntiAliasing.Temporal, c.Preset),
                    Draw, FirstMeasured, Measured);
                Check(c.Name, Measure(frames, reference), failures);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [GpuFact]
        public void Texture_detail_near_the_display_nyquist_resolves_as_before_the_release_at_quality()
        {
            var failures = new List<string>();
            foreach (Fine c in Fines)
            {
                var stage = new FrontStage(W, H, OrthoSize);
                MeshHandle panel = default;
                void Setup(Scene3D s, TemporalUpscale preset)
                {
                    stage.Setup(s, AntiAliasing.Temporal, preset);
                    panel = s.LoadMesh(stage.PanelMesh(c.TexelPixels), s.LoadTexture(FrontStage.NoiseTexture(),
                        FrontStage.TextureSize, FrontStage.TextureSize));
                }

                void Draw(Scene3D s, int n) => s.Draw(panel, Matrix4x4.Identity);

                byte[][] reference = TemporalAcceptance.ReferenceSequence(W, H, s => Setup(s, TemporalUpscale.Native),
                    Draw, FirstMeasured, Measured);
                byte[][] frames = TemporalAcceptance.Sequence(W, H, s => Setup(s, TemporalUpscale.Quality), Draw,
                    FirstMeasured, Measured);
                Check(c.Name, Measure(frames, reference), failures);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        static Figures Measure(byte[][] frames, byte[][] reference)
        {
            double error = 0;
            for (int t = 0; t < frames.Length; t++)
                error += TemporalAcceptance.MeanAbsLuma(frames[t], reference[t], W, Region);
            return new Figures(error / frames.Length, TemporalAcceptance.TemporalError(frames, reference, W, H, Region));
        }

        void Check(string name, Figures f, List<string> failures)
        {
            output.WriteLine($"{TemporalDeviceClass.Name} {name}: error {f.Error:0.00000}, temporal error "
                + $"{f.TemporalError:0.00000}");
            if (!Baseline.TryGetValue(name, out Figures before))
            {
                failures.Add($"{name}: no figures before the release for {TemporalDeviceClass.Name}");
                return;
            }
            if (f.Error > before.Error * (1 + Tolerance))
                failures.Add($"{name}: error {f.Error:0.00000} above {before.Error:0.00000} plus a tenth");
            if (f.TemporalError > before.TemporalError * (1 + Tolerance))
                failures.Add($"{name}: temporal error {f.TemporalError:0.00000} above {before.TemporalError:0.00000} "
                    + "plus a tenth");
        }
    }
}
