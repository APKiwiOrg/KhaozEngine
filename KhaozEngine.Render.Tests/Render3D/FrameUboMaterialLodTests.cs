using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The material LOD rides the frame header's free Params.z and Params.w lanes, and the default leaves
    /// the packed frame block byte-identical.</summary>
    public sealed class FrameUboMaterialLodTests
    {
        const int ParamsOffset = 112;   // ViewProj 64 + Dir 16 + Color 16 + Ambient 16

        static ReadOnlySpan<ModelRenderer.PointLightData> NoLights => ReadOnlySpan<ModelRenderer.PointLightData>.Empty;

        static ModelRenderer NewRenderer(FakeGpuDevice device)
        {
            IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, target);
            return new ModelRenderer(device, framebuffer.Outputs, 64, 1);
        }

        [Fact]
        public void ParamsSitsWhereTheOffsetSaysIt() => Assert.Equal(ParamsOffset,
            (int)Marshal.OffsetOf<ModelRenderer.FrameUbo>(nameof(ModelRenderer.FrameUbo.Params)));

        [Fact]
        public void TheDefaultLeavesTheFrameBlockUnchanged()
        {
            using var device = new FakeGpuDevice();
            using ModelRenderer model = NewRenderer(device);
            using var cl = new NullGpuCommandList();
            var s = new PixelPostProcessSettings();
            model.SetFrameUniforms(cl, Matrix4x4.Identity, Vector3.Zero, s, NoLights);
            byte[] implicitDefault = model.FrameImage.ToArray();
            model.SetFrameUniforms(cl, Matrix4x4.Identity, Vector3.Zero, s, NoLights, default, Vector2.Zero);
            Assert.True(implicitDefault.AsSpan().SequenceEqual(model.FrameImage));
            Assert.Equal(0, BitConverter.ToInt32(implicitDefault, ParamsOffset + 8));
            Assert.Equal(0, BitConverter.ToInt32(implicitDefault, ParamsOffset + 12));
        }

        [Fact]
        public void TheMaterialLodLandsInParamsZw()
        {
            using var device = new FakeGpuDevice();
            using ModelRenderer model = NewRenderer(device);
            using var cl = new NullGpuCommandList();
            var s = new PixelPostProcessSettings();
            model.SetFrameUniforms(cl, Matrix4x4.Identity, Vector3.Zero, s, NoLights, default,
                new Vector2(-1.5f, -0.6464466f));
            byte[] img = model.FrameImage.ToArray();
            Assert.Equal(s.CelBands, BitConverter.ToSingle(img, ParamsOffset));
            Assert.Equal(-1.5f, BitConverter.ToSingle(img, ParamsOffset + 8));
            Assert.Equal(-0.6464466f, BitConverter.ToSingle(img, ParamsOffset + 12));
        }
    }
}
