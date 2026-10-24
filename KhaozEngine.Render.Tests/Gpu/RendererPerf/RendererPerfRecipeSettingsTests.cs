using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeSettingsTests
{
    [Theory]
    [InlineData(IndirectDiffuseMode.Directional, false)]
    [InlineData((IndirectDiffuseMode)99, false)]
    [InlineData(IndirectDiffuseMode.Uniform, true)]
    public void Frozen_construction_refuses_new_indirect_lighting_work(IndirectDiffuseMode mode, bool irradiance)
    {
        using var recipe = new ChangedIndirectRecipe(mode, irradiance);
        var error = Assert.Throws<InvalidOperationException>(recipe.Freeze);
        Assert.Contains("indirect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class ChangedIndirectRecipe(IndirectDiffuseMode mode, bool irradiance) : RendererPerfRecipe("empty")
    {
        protected override void ConfigureSettings(PixelPostProcessSettings post)
        {
            post.IndirectDiffuse = mode;
            post.AmbientIrradiance = irradiance ? IrradianceSH.Uniform(Vector3.One) : IrradianceSH.Zero;
        }
    }
}
