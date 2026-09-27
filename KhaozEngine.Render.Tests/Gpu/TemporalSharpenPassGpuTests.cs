using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The RCAS pass on a real device against its CPU mirror. The source is vertically symmetric, so the flip a
    /// fullscreen pass applies cannot change what any row reads, and the comparison needs no knowledge of the
    /// backend's clip convention. Beside the bands it carries the mirror's exact-value neighbourhoods as five-tap
    /// stamps: the limit binding, a channel clipped to 1 across the ring (in red and in green), a coloured ring that
    /// pins the luma weights, an isolated centre and a pure primary.
    /// <para>
    /// Two readbacks. An RGBA8 target checks every channel: each byte is the mirror's value rounded, within the
    /// conversion tolerance of a unorm target plus the float error below. A half-float source into an R32Float target
    /// checks red in float: the GPU samples exactly the inputs the mirror is given, so what is left is float
    /// arithmetic in a different order (the flip swaps north and south in every sum), a few ulps.
    /// </para>
    /// </summary>
    public sealed class TemporalSharpenPassGpuTests
    {
        const int W = 48, H = 32;

        // The float spacing just below 1. Every output lies in 0 to 1, so eight of them is a few ulps anywhere.
        const float Ulp = 1f / (1 << 24);
        const float FewUlps = 8 * Ulp;

        // A unorm target may round a value within 0.6 of a step either way, and the float error rides on top.
        const float UnormSlack = 0.6f + 255f * FewUlps;

        readonly ITestOutputHelper _output;

        public TemporalSharpenPassGpuTests(ITestOutputHelper output) => _output = output;

        // The stamp centres, all on row 2 and mirrored onto row H - 3.
        static readonly (string Name, int X)[] Stamps =
        {
            ("limit binds", 4), ("red clipped", 10), ("green clipped", 16),
            ("coloured luma", 28), ("isolated", 34), ("pure red", 40),
        };

        static byte[] Source()
        {
            var rgba = new byte[W * H * 4];
            for (int y = 0; y < H / 2; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y < 6 ? (x < W / 2 ? 0.25f : 0.75f)                                  // two-grey step
                        : y < 12 ? Math.Clamp(0.25f + 0.1f * (x - W / 2 + 3), 0.25f, 0.75f)      // soft ramp
                        : (x < W / 2 ? 0f : 1f);                                                   // full contrast
                    Put(rgba, x, y, new Vector3(v, v * 0.9f, v * 0.8f));
                }
            Put(rgba, 10, 8, new Vector3(0.55f, 0.5f, 0.45f));   // an isolated pixel in the flat left of the ramp band

            // The mirror's exact-value neighbourhoods: north, west, centre, east, south.
            Stamp(rgba, 4, G(0.45f), G(0.5f), G(0.52f), G(0.5f), G(0.55f));
            Stamp(rgba, 10, new(1f, 0.5f, 0.5f), new(1f, 0.4f, 0.4f), new(1f, 0.55f, 0.55f), new(1f, 0.6f, 0.6f),
                new(1f, 0.5f, 0.5f));
            Stamp(rgba, 16, new(0.5f, 1f, 0.5f), new(0.4f, 1f, 0.4f), new(0.55f, 1f, 0.55f), new(0.6f, 1f, 0.6f),
                new(0.5f, 1f, 0.5f));
            // The coloured ring with red as the standout channel, so the float readback sees it.
            Stamp(rgba, 28, G(0.5f), new(0.5f, 0.4f, 0.6f), new(0.6f, 0.5f, 0.5f), new(0.5f, 0.6f, 0.4f), G(0.5f));
            Stamp(rgba, 34, G(0.5f), G(0.5f), G(0.55f), G(0.5f), G(0.5f));
            Stamp(rgba, 40, new(0.5f, 0f, 0f), new(0.4f, 0f, 0f), new(0.55f, 0f, 0f), new(0.6f, 0f, 0f),
                new(0.5f, 0f, 0f));
            return rgba;
        }

        static Vector3 G(float v) => new(v, v, v);

        static void Stamp(byte[] rgba, int x, Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h)
        {
            const int y = 2;
            Put(rgba, x, y - 1, b);
            Put(rgba, x - 1, y, d);
            Put(rgba, x, y, e);
            Put(rgba, x + 1, y, f);
            Put(rgba, x, y + 1, h);
        }

        // Writes the row and its mirror, which keeps the source vertically symmetric.
        static void Put(byte[] rgba, int x, int y, Vector3 c)
        {
            foreach (int row in new[] { y, H - 1 - y })
            {
                int i = (row * W + x) * 4;
                rgba[i] = (byte)MathF.Round(c.X * 255f);
                rgba[i + 1] = (byte)MathF.Round(c.Y * 255f);
                rgba[i + 2] = (byte)MathF.Round(c.Z * 255f);
                rgba[i + 3] = 255;
            }
        }

        // The source as the half-float texture holds it, four floats per texel.
        static float[] HalfSource()
        {
            byte[] bytes = Source();
            var values = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) values[i] = (float)(Half)(bytes[i] / 255f);
            return values;
        }

        // Clamp addressing, as the pass's own point sampler.
        static Vector3 At(byte[] rgba, int x, int y)
        {
            int i = (Math.Clamp(y, 0, H - 1) * W + Math.Clamp(x, 0, W - 1)) * 4;
            return new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
        }

        static Vector3 At(float[] rgba, int x, int y)
        {
            int i = (Math.Clamp(y, 0, H - 1) * W + Math.Clamp(x, 0, W - 1)) * 4;
            return new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]);
        }

        static Vector3 Mirror(Func<int, int, Vector3> at, int x, int y, float sharpness)
            => TemporalSharpenMath.Sharpen(at(x, y - 1), at(x - 1, y), at(x, y), at(x + 1, y), at(x, y + 1), sharpness);

        static byte[] RunPass(float sharpness)
            => Run(sharpness, floatPath: false, (gd, target) => GpuReadback.ToRgba(gd, target, W, H));

        static float[] RunPassInFloat(float sharpness) => Run(sharpness, floatPath: true, TemporalTextureIo.Read);

        static T Run<T>(float sharpness, bool floatPath, Func<IGpuDevice, IGpuTexture, T> read)
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            IGpuResourceFactory f = gd.Factory;
            using IGpuTexture src = f.CreateTexture(GpuTextureDescription.Texture2D(W, H,
                floatPath ? GpuPixelFormat.R16G16B16A16Float : GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            if (floatPath) TemporalTextureIo.Upload(gd, src, HalfSource());
            else gd.UpdateTexture(src, Source(), 0, 0, W, H);
            using IGpuTexture dstTexture = f.CreateTexture(GpuTextureDescription.Texture2D(W, H,
                floatPath ? GpuPixelFormat.R32Float : GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer dst = f.CreateFramebuffer(null, dstTexture);
            using var pass = new TemporalSharpenPass(gd, dst.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();
            using (GpuRecording.Open(gd, cl, "TemporalSharpenPassGpuTests"))
            {
                pass.Prepare(cl, W, H, sharpness);   // the pass's uniform write, ahead of its draw in this recording
                pass.Draw(cl, src, dst);
            }
            gd.Submit(cl);
            gd.WaitForIdle();
            return read(gd, dstTexture);
        }

        [GpuTheory]
        [InlineData(0.25f)]
        [InlineData(1f)]
        [InlineData(float.NaN)]
        public void TheGpuPassMatchesItsCpuMirror(float sharpness)
        {
            byte[] src = Source();
            byte[] got = RunPass(sharpness);
            float worst = 0f;
            int moved = 0;
            string where = "nowhere";
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Vector3 want = 255f * Mirror((u, v) => At(src, u, v), x, y, sharpness);
                    int i = (y * W + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        float wanted = c == 0 ? want.X : c == 1 ? want.Y : want.Z;
                        float d = MathF.Abs(got[i + c] - wanted);
                        if (d > worst) { worst = d; where = $"({x},{y}) channel {c}"; }
                        moved = Math.Max(moved, Math.Abs((int)MathF.Round(wanted) - src[i + c]));
                    }
                    Assert.Equal(255, got[i + 3]);
                }
            // The largest step the mirror moves any channel off the source shows the comparison has something to see.
            _output.WriteLine($"sharpness {sharpness}: RGBA8 against 255 times the mirror worst {worst:F4} at {where}, "
                + $"mirror moves the source by up to {moved} steps");
            Assert.True(worst <= UnormSlack,
                $"the GPU pass is {worst:F4} steps from 255 times TemporalSharpenMath at {where}, past {UnormSlack}");
        }

        [GpuTheory]
        [InlineData(0.25f)]
        [InlineData(1f)]
        public void InFloatTheGpuPassMatchesItsCpuMirrorToAFewUlps(float sharpness)
        {
            float[] src = HalfSource();
            float[] got = RunPassInFloat(sharpness);   // red only
            float worst = 0f;
            string where = "nowhere";
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float want = Mirror((u, v) => At(src, u, v), x, y, sharpness).X;
                    float d = MathF.Abs(got[y * W + x] - want);
                    if (d > worst) { worst = d; where = $"({x},{y}) GPU {got[y * W + x]:R} mirror {want:R}"; }
                }
            _output.WriteLine($"sharpness {sharpness}: R32Float against the mirror worst {worst / Ulp:F1} ulps "
                + $"of 2^-24 at {where}");
            foreach ((string name, int x) in Stamps)
                _output.WriteLine($"  {name} centre: GPU {got[2 * W + x]:F6}, mirror "
                    + $"{Mirror((u, v) => At(src, u, v), x, 2, sharpness).X:F6}, source {src[(2 * W + x) * 4]:F6}");
            Assert.True(worst <= FewUlps,
                $"the GPU pass is {worst / Ulp:F1} ulps from TemporalSharpenMath at {where}, past {FewUlps / Ulp}");
        }

        [GpuFact]
        public void AFullContrastEdgeComesThroughUnchanged()
        {
            byte[] src = Source();
            byte[] got = RunPass(1f);
            for (int y = 13; y < H - 13; y++)
                for (int x = W / 2 - 2; x <= W / 2 + 1; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = (y * W + x) * 4 + c;
                        Assert.True(Math.Abs(got[i] - src[i]) <= 1, $"the black to white edge moved at ({x},{y})");
                    }
        }
    }
}
