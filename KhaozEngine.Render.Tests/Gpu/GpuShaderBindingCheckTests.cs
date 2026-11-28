using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class GpuShaderBindingCheckTests
    {
        static readonly GpuResourceLayoutDescription SpriteTextureLayout = new(
            new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));

        static readonly GpuResourceLayoutDescription SpriteViewLayout = new(
            new GpuResourceLayoutElement("Vp", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true));

        static GpuShaderProgram Sprite(params GpuShaderBinding[] bindings)
        {
            var stage = new GpuShaderStageCode("main", ReadOnlyMemory<byte>.Empty, "", "");
            return new GpuShaderProgram("sprite", "", false, stage, stage, bindings);
        }

        static readonly GpuShaderBinding Tex = new("Tex", 0, 0, GpuResourceKind.TextureReadOnly, 0, 0);
        static readonly GpuShaderBinding Samp = new("Samp", 0, 1, GpuResourceKind.Sampler, 0, 0);
        static readonly GpuShaderBinding Vp = new("Vp", 1, 0, GpuResourceKind.UniformBuffer, 0, 0);

        [Fact]
        public void Accepts_bindings_that_match_the_declared_layouts()
            => GpuShaderBindingCheck.RequireMatches(Sprite(Tex, Samp, Vp), [SpriteTextureLayout, SpriteViewLayout]);

        [Fact]
        public void Refuses_a_kind_mismatch_naming_set_and_binding()
        {
            var wrongKind = Samp with { Kind = GpuResourceKind.TextureReadOnly };
            var ex = Assert.Throws<ShaderValidationException>(() => GpuShaderBindingCheck.RequireMatches(
                Sprite(Tex, wrongKind, Vp), [SpriteTextureLayout, SpriteViewLayout]));
            Assert.Equal(ShaderValidationRefusal.BundleLayoutMismatch, ex.Reason);
            Assert.Equal("sprite", ex.Label);
            Assert.Equal(0, ex.Set);
            Assert.Equal(1, ex.Binding);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(2, 0)]
        public void Refuses_a_binding_past_the_declared_layout(int set, int binding)
        {
            var past = new GpuShaderBinding("Extra", set, binding, GpuResourceKind.UniformBuffer, 0, 0);
            var ex = Assert.Throws<ShaderValidationException>(() => GpuShaderBindingCheck.RequireMatches(
                Sprite(Tex, Samp, Vp, past), [SpriteTextureLayout, SpriteViewLayout]));
            Assert.Equal(ShaderValidationRefusal.BundleLayoutMismatch, ex.Reason);
            Assert.Equal(set, ex.Set);
            Assert.Equal(binding, ex.Binding);
        }
    }
}
