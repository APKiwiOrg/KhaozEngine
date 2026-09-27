using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The temporal mip bias reaches the shipped model program through the scene's frame block. A ground plane wears
    /// a white-noise albedo with its full generated mip chain, so each coarser level halves the texture's contrast,
    /// and the rendered contrast names the level the model program read. Two fresh scenes at the
    /// <see cref="TemporalUpscale.Native"/> preset differ only in <see cref="TemporalSettings.MipBiasOffset"/>: 0 there
    /// writes a bias of exactly 0, and minus 2 writes minus 2. Same internal size, same jitter phase, same resolve, so
    /// the only thing that can raise the contrast is a finer mip.
    /// </summary>
    public sealed class MaterialMipBiasSceneGpuTests
    {
        const int W = 160, H = 90, TexSize = 64;

        readonly ITestOutputHelper _out;

        public MaterialMipBiasSceneGpuTests(ITestOutputHelper output) => _out = output;

        [GpuFact]
        public void ANegativeBiasReadsAFinerMipOfAModelTexture()
        {
            (byte[] unbiased, float zeroLane) = Render(0f);
            (byte[] biased, float biasLane) = Render(-2f);
            Assert.Equal(0, BitConverter.SingleToInt32Bits(zeroLane));
            Assert.Equal(-2f, biasLane);

            double flat = Rgba8Stats.LumaDeviation(unbiased), fine = Rgba8Stats.LumaDeviation(biased);
            _out.WriteLine($"luma deviation: bias 0 {flat:F2}, bias -2 {fine:F2}, ratio {fine / flat:F2}");
            Assert.True(fine > 1.5 * flat, $"bias -2 left the contrast at {fine:F2} against {flat:F2} unbiased");
        }

        static (byte[] Rgba, float BiasLane) Render(float offset)
        {
            MeshHandle ground = default;
            using var fx = new TemporalFixture(W, H, s =>
            {
                s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                s.Post.Temporal.Upscale = TemporalUpscale.Native;
                s.Post.Temporal.MipBiasOffset = offset;
                s.Post.Starfield = false;
                s.Post.Outline = false;
                s.Camera.Elevation = 1.45f;   // near top-down, so the footprint is close to isotropic
                s.Camera.OrthoSize = 6f;
                Scene3D.TextureHandle noise = s.LoadTexture(Noise(), TexSize, TexSize);
                ground = s.LoadMesh(MeshOps.ScaleUv(MeshPrimitives.Plane(60f, 60f), 60f), noise);
            });
            byte[] rgba = fx.Frame((scene, _) => scene.Draw(ground, Matrix4x4.Identity, Color.White));
            float lane = BitConverter.ToSingle(fx.Scene.FrameImageForTests.Slice(120, 4));   // Params.z
            return (rgba, lane);
        }

        /// <summary>A grey white-noise texture from a fixed seed, so every run sees the same texels.</summary>
        static byte[] Noise()
        {
            var px = new byte[TexSize * TexSize * 4];
            uint state = 0x9E3779B9u;
            for (int i = 0; i < px.Length; i += 4)
            {
                state = state * 1664525u + 1013904223u;
                byte v = (byte)(state >> 24);
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
            return px;
        }
    }
}
