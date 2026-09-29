using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 7: a textured checkerboard at Performance keeps the native
    /// reference's detail within tolerance, and the bias is what keeps it (the same frame with the bias cancelled
    /// loses detail). The bias applies to every frame here, because each is the first render of a resolving frame.
    /// <para>
    /// HDR is off in every render, so the chain has no tonemap. The ambient is a quarter, so the lit checker spans
    /// 0.15 to 0.95 luma and never clips: at full ambient the key light drives the bright checks past white, and a
    /// clipped check reads the same sharp or soft. The checker repeats a quarter of a time a metre, a check 12.5 cm
    /// across, so a check at the band's far edge is one display pixel deep, the display's Nyquist, and five at its near
    /// edge. The whole band then holds detail native resolves, and its far part holds detail between Performance's
    /// internal Nyquist and the display's, which only the bias and the accumulation keep. The native reference is the
    /// same scene converged at Native with the same offset.
    /// </para>
    /// </summary>
    public sealed class TemporalMipBiasGoldenTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, NativeFrames = 32, PerformanceFrames = 48;
        const float Ambient = 0.25f, RepeatsPerMetre = 0.25f, DefaultOffset = -0.5f;

        // Cancels Performance's bias: log2(1/2) plus 1 is zero.
        const float CancellingOffset = 1f;

        /// <summary>
        /// Biased Performance detail as a share of native's, a regression floor rather than a quality claim. The
        /// claim that the bias keeps the detail rests on the unbiased control (<see cref="MaxUnbiasedDetailShare"/>)
        /// and the band error. A converged pixel takes a reconstruction kernel sized in display pixels, so Performance
        /// keeps 0.768 of native here on Metal (Quality 0.883), against 0.747 (Quality 0.781) with the kernel sized in
        /// internal pixels alone, which band-limits each frame to the internal Nyquist
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1188). The floor is the measured value less the margin the
        /// earlier floor kept.
        /// </summary>
        const double MinDetailKept = 0.72;

        const double MaxUnbiasedDetailShare = 0.9;   // unbiased detail as a share of biased: the bias keeps it
        const double MaxBandLumaError = 0.08;        // mean luma error against native in the band

        static readonly PixelRect Band = new(W / 5, H * 35 / 100, W * 4 / 5, H * 70 / 100);

        static byte[] Checker()
        {
            var rgba = new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    byte v = ((x / 8 + y / 8) & 1) == 0 ? (byte)38 : (byte)217;
                    int i = (y * 256 + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                    rgba[i + 3] = 255;
                }
            return rgba;
        }

        // A 16 by 30 m ground quad, both windings so culling cannot hide it.
        static GltfMesh Ground()
        {
            var n = Vector3.UnitY; var c = Vector4.One;
            ModelVertex V(float x, float z) => new(new Vector3(x, 0f, z), n, c, new Vector2(x, z) * RepeatsPerMetre);
            var v = new[] { V(-8f, 0.5f), V(8f, 0.5f), V(8f, 30.5f), V(-8f, 30.5f) };
            return new GltfMesh(v, new ushort[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        }

        /// <summary>The checkerboard converged over <paramref name="frames"/> frames, and the mip bias the last frame
        /// applied (the frame block's <c>Params.z</c>).</summary>
        static (byte[] Image, float Bias) Converged(TemporalUpscale preset, float mipBiasOffset, int frames)
        {
            MeshHandle ground = default;
            using var fx = new TemporalFixture(W, H, s =>
            {
                s.Post.UseSmoothPreset();
                s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                s.Post.Temporal.Upscale = preset;
                s.Post.Temporal.MipBiasOffset = mipBiasOffset;
                s.Post.Hdr.Enabled = false;   // the legacy chain, no tonemap, see the class summary
                s.Post.TransparentBackground = false;
                s.Post.AmbientColor = new Color(Ambient, Ambient, Ambient, 1f);
                s.CameraOverride = new FlyCamera3D
                {
                    Position = new Vector3(0f, 1.5f, 0f), Pitch = -0.45f, AspectRatio = (float)W / H,
                };
                ground = s.LoadMesh(Ground(), s.LoadTexture(Checker(), 256, 256));
            });
            fx.Frames(frames - 1, (s, _) => s.Draw(ground, Matrix4x4.Identity));
            byte[] image = fx.Frame((s, _) => s.Draw(ground, Matrix4x4.Identity));
            return (image, BitConverter.ToSingle(fx.Scene.FrameImageForTests.Slice(120, 4)));
        }

        [GpuFact]
        public void ACheckerboardAtPerformanceKeepsTheNativeReferencesDetail()
        {
            var (native, nativeBias) = Converged(TemporalUpscale.Native, DefaultOffset, NativeFrames);
            var (biased, biasedBias) = Converged(TemporalUpscale.Performance, DefaultOffset, PerformanceFrames);
            var (unbiased, unbiasedBias) = Converged(TemporalUpscale.Performance, CancellingOffset, PerformanceFrames);

            double cN = TemporalAcceptance.LocalContrast(native, W, H, Band);
            double cB = TemporalAcceptance.LocalContrast(biased, W, H, Band);
            double cU = TemporalAcceptance.LocalContrast(unbiased, W, H, Band);
            double err = TemporalAcceptance.MeanAbsLuma(biased, native, W, Band);
            output.WriteLine($"HDR off, mip bias applied: native {nativeBias}, Performance biased {biasedBias}, "
                + $"Performance unbiased {unbiasedBias}");
            output.WriteLine($"band {Band.X0},{Band.Y0} to {Band.X1},{Band.Y1}, local contrast: native {cN:0.0000}, "
                + $"Performance biased {cB:0.0000} ({cB / cN:0.000} of native, bound {MinDetailKept}), Performance "
                + $"unbiased {cU:0.0000} ({cU / cB:0.000} of biased, bound {MaxUnbiasedDetailShare}), biased error "
                + $"{err:0.0000} (bound {MaxBandLumaError})");
            output.WriteLine($"band mean luma: native {TemporalAcceptance.MeanLuma(native, W, Band):0.0000}, biased "
                + $"{TemporalAcceptance.MeanLuma(biased, W, Band):0.0000}, unbiased "
                + $"{TemporalAcceptance.MeanLuma(unbiased, W, Band):0.0000}");
            for (int y0 = Band.Y0; y0 < H; y0 += 21)
            {
                var rows = new PixelRect(Band.X0, y0, Band.X1, Math.Min(H, y0 + 21));
                double n = TemporalAcceptance.LocalContrast(native, W, H, rows);
                double b = TemporalAcceptance.LocalContrast(biased, W, H, rows);
                double u = TemporalAcceptance.LocalContrast(unbiased, W, H, rows);
                output.WriteLine($"  rows {rows.Y0} to {rows.Y1}: native {n:0.0000}, biased {b:0.0000} "
                    + $"({b / n:0.000}), unbiased {u:0.0000} ({u / b:0.000} of biased)");
            }

            Assert.Equal(0f, unbiasedBias);
            Assert.True(cB >= MinDetailKept * cN,
                $"Performance must keep {MinDetailKept:P0} of the native detail: {cB:0.0000} against {cN:0.0000}");
            Assert.True(cU < MaxUnbiasedDetailShare * cB,
                $"the bias must be what keeps it: unbiased {cU:0.0000} against biased {cB:0.0000}");
            Assert.True(err <= MaxBandLumaError,
                $"Performance must stay within {MaxBandLumaError} mean luma of native in the band, got {err:0.0000}");
            GoldenCompare.AssertOrUpdate("temporal_mipbias_checker_performance", biased, W, H);
        }
    }
}
