using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Pure enum/description -> Veldrid mapping checks. No GPU required: a wrong mapping (blend,
    /// format, topology, etc.) would show up as a renderer golden failure, so these guard the seam cheaply.</summary>
    public class VeldridMapTests
    {
        [Theory]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, PixelFormat.R8_G8_B8_A8_UNorm)]
        [InlineData(GpuPixelFormat.R32Float, PixelFormat.R32_Float)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, PixelFormat.D32_Float_S8_UInt)]
        public void PixelFormat_RoundTrips(GpuPixelFormat engine, PixelFormat veldrid)
        {
            Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));
            Assert.Equal(engine, VeldridMap.FromVeldrid(veldrid));
        }

        [Theory]
        [InlineData(GpuBlendFactor.SourceAlpha, BlendFactor.SourceAlpha)]
        [InlineData(GpuBlendFactor.InverseSourceAlpha, BlendFactor.InverseSourceAlpha)]
        [InlineData(GpuBlendFactor.One, BlendFactor.One)]
        [InlineData(GpuBlendFactor.Zero, BlendFactor.Zero)]
        public void BlendFactor_Maps(GpuBlendFactor engine, BlendFactor veldrid)
            => Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));

        [Theory]
        [InlineData(GpuBlendFunction.Add, BlendFunction.Add)]
        [InlineData(GpuBlendFunction.ReverseSubtract, BlendFunction.ReverseSubtract)]
        public void BlendFunction_Maps(GpuBlendFunction engine, BlendFunction veldrid)
            => Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));

        [Theory]
        [InlineData(GpuPrimitiveTopology.TriangleList, PrimitiveTopology.TriangleList)]
        [InlineData(GpuPrimitiveTopology.LineList, PrimitiveTopology.LineList)]
        public void Topology_Maps(GpuPrimitiveTopology engine, PrimitiveTopology veldrid)
            => Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));

        [Theory]
        [InlineData(GpuComparison.LessEqual, ComparisonKind.LessEqual)]
        [InlineData(GpuComparison.Always, ComparisonKind.Always)]
        public void Comparison_Maps(GpuComparison engine, ComparisonKind veldrid)
            => Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));

        [Theory]
        [InlineData(GpuIndexFormat.UInt16, IndexFormat.UInt16)]
        [InlineData(GpuIndexFormat.UInt32, IndexFormat.UInt32)]
        public void IndexFormat_Maps(GpuIndexFormat engine, IndexFormat veldrid)
            => Assert.Equal(veldrid, VeldridMap.ToVeldrid(engine));

        [Fact]
        public void TextureUsage_Flags_Combine()
        {
            var u = VeldridMap.ToVeldrid(GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled);
            Assert.True(u.HasFlag(TextureUsage.RenderTarget));
            Assert.True(u.HasFlag(TextureUsage.Sampled));
        }

        [Fact]
        public void BufferUsage_Flags_Map()
        {
            Assert.Equal(BufferUsage.VertexBuffer, VeldridMap.ToVeldrid(GpuBufferUsage.VertexBuffer));
            Assert.Equal(BufferUsage.UniformBuffer, VeldridMap.ToVeldrid(GpuBufferUsage.UniformBuffer));
        }

        [Fact]
        public void ShaderStages_Flags_Combine()
        {
            var s = VeldridMap.ToVeldrid(GpuShaderStages.Vertex | GpuShaderStages.Fragment);
            Assert.True(s.HasFlag(ShaderStages.Vertex));
            Assert.True(s.HasFlag(ShaderStages.Fragment));
        }

        [Fact]
        public void ResourceKind_Maps()
        {
            Assert.Equal(ResourceKind.TextureReadOnly, VeldridMap.ToVeldrid(GpuResourceKind.TextureReadOnly));
            Assert.Equal(ResourceKind.Sampler, VeldridMap.ToVeldrid(GpuResourceKind.Sampler));
            Assert.Equal(ResourceKind.UniformBuffer, VeldridMap.ToVeldrid(GpuResourceKind.UniformBuffer));
        }

        [Fact]
        public void SamplerFilter_Maps()
        {
            Assert.Equal(SamplerFilter.MinPoint_MagPoint_MipPoint, VeldridMap.ToVeldrid(GpuSamplerFilter.MinPointMagPointMipPoint));
            Assert.Equal(SamplerFilter.MinLinear_MagLinear_MipLinear, VeldridMap.ToVeldrid(GpuSamplerFilter.MinLinearMagLinearMipLinear));
        }

        [Fact]
        public void AlphaBlendPreset_Maps_To_SourceAlpha_InverseSourceAlpha()
        {
            BlendAttachmentDescription a = VeldridMap.ToVeldrid(GpuBlendAttachment.AlphaBlend);
            Assert.True(a.BlendEnabled);
            Assert.Equal(BlendFactor.SourceAlpha, a.SourceColorFactor);
            Assert.Equal(BlendFactor.InverseSourceAlpha, a.DestinationColorFactor);
            Assert.Equal(BlendFunction.Add, a.ColorFunction);
        }

        [Fact]
        public void AdditivePreset_Maps_To_SourceAlpha_One()
        {
            BlendAttachmentDescription a = VeldridMap.ToVeldrid(GpuBlendAttachment.Additive);
            Assert.True(a.BlendEnabled);
            Assert.Equal(BlendFactor.SourceAlpha, a.SourceColorFactor);
            Assert.Equal(BlendFactor.One, a.DestinationColorFactor);
        }

        [Fact]
        public void OverridePreset_Disables_Blend()
        {
            BlendAttachmentDescription a = VeldridMap.ToVeldrid(GpuBlendAttachment.OverrideBlend);
            Assert.False(a.BlendEnabled);
        }

        [Fact]
        public void OutputDescription_RoundTrips_Depth_And_Multiple_Colour()
        {
            var engine = new GpuOutputDescription(
                GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);
            OutputDescription veldrid = VeldridMap.ToVeldrid(engine);
            Assert.True(veldrid.DepthAttachment.HasValue);
            Assert.Equal(3, veldrid.ColorAttachments.Length);

            GpuOutputDescription back = VeldridMap.FromVeldrid(veldrid);
            Assert.Equal(GpuPixelFormat.D32FloatS8UInt, back.Depth);
            Assert.Equal(3, back.Colour.Length);
            Assert.Equal(GpuPixelFormat.R32Float, back.Colour[2]);
        }

        [Fact]
        public void DepthOnlyLessEqual_Preset_Tests_And_Writes()
        {
            var d = GpuDepthStencilState.DepthOnlyLessEqual;
            Assert.True(d.DepthTestEnabled);
            Assert.True(d.DepthWriteEnabled);
            Assert.Equal(GpuComparison.LessEqual, d.Comparison);
        }

        [Fact]
        public void Disabled_Depth_Preset_Off()
        {
            var d = GpuDepthStencilState.Disabled;
            Assert.False(d.DepthTestEnabled);
            Assert.False(d.DepthWriteEnabled);
        }

        [Fact]
        public void AnisotropicFilterMapsToVeldrid()
        {
            Assert.Equal(SamplerFilter.Anisotropic, VeldridMap.ToVeldrid(GpuSamplerFilter.Anisotropic));
        }
    }
}
