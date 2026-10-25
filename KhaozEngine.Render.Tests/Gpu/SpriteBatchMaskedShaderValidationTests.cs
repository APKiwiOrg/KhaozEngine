using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class SpriteBatchMaskedShaderValidationTests
    {
        [Fact]
        public void MaskedPairCrossCompilesToAllTargets()
        {
            ShaderValidation.ValidatePair(SpriteBatch.VertSrc, MaskedSpriteShader.FragmentSource, "Sprite2DMasked");
            Assert.Contains(ShippedShaderPrograms.GraphicsPrograms(), p =>
                p.Name == "Sprite2DMasked" && p.VertexGlsl == SpriteBatch.VertSrc &&
                p.FragmentGlsl == MaskedSpriteShader.FragmentSource);
        }

        [Fact]
        public void MaskedBindingsMatchPipelineLayouts()
        {
            Assert.Equal(new[] { "Tex", "Samp", "Mask", "MaskSamp" },
                VulkanDescriptorLimitTests.ShippedLayouts["SpriteBatch.maskedTexture"].Elements.Select(e => e.Name));
            Assert.Equal(68, VulkanDescriptorLimitTests.ShippedLayouts.Count);
            Assert.Equal(75, VulkanDescriptorLimitTests.ShippedPipelines.Count);
            Assert.Equal(new[] { "SpriteBatch.maskedTexture", "SpriteBatch.vp" },
                Assert.Single(VulkanDescriptorLimitTests.ShippedPipelines,
                    p => p.Pipeline == "SpriteBatch masked").Slots);
        }
    }
}
