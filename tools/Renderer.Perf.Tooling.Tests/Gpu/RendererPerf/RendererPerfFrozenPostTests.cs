using System;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFrozenPostTests
{
    static ColorGradeLut BlackLook() => new(new Vector3[ColorGradeLut.SampleCount]);

    [Fact]
    public void Bypassed_AO_and_grading_preserve_the_frozen_settings_encoding()
    {
        var post = new PixelPostProcessSettings();
        string original = JsonSerializer.Serialize(post, RendererPerfRecipe.Json);
        using var json = JsonDocument.Parse(original);
        Assert.False(json.RootElement.TryGetProperty(nameof(post.AmbientOcclusion), out _));
        Assert.False(json.RootElement.TryGetProperty(nameof(post.ColorGrade), out _));

        post.AmbientOcclusion.RadiusMeters *= 2;
        post.ColorGrade.Look = ColorGradeLut.Identity;
        Assert.Equal(original, JsonSerializer.Serialize(post, RendererPerfRecipe.Json));

        post.AmbientOcclusion.Enabled = true;
        post.AmbientOcclusion.Strength = 0f;
        post.ColorGrade.Look = BlackLook();
        post.ColorGrade.Strength = 0f;
        Assert.Equal(original, JsonSerializer.Serialize(post, RendererPerfRecipe.Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Freezing_a_recipe_refuses_new_post_work(bool grade)
    {
        using var control = RendererPerfRecipe.Create("empty", verifyFrozen: false);
        using var recipe = new ChangedPostRecipe(grade);
        Assert.Throws<InvalidOperationException>(recipe.Freeze);
    }

    [Fact]
    public void A_frozen_block_restores_bypassed_post_on_a_preconfigured_scene()
    {
        using var recipe = RendererPerfRecipe.Create("village", verifyFrozen: false);
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.AmbientOcclusion.Enabled = true;
        rig.Scene.Post.ColorGrade.Look = BlackLook();

        recipe.ResetBlock(rig.Scene);

        Assert.False(rig.Scene.Post.AmbientOcclusion.Enabled);
        Assert.Null(rig.Scene.Post.ColorGrade.Look);
    }

    sealed class ChangedPostRecipe(bool grade) : RendererPerfRecipe("empty")
    {
        protected override void ConfigureSettings(PixelPostProcessSettings post)
        {
            post.Hdr.Enabled = true;
            if (grade) post.ColorGrade.Look = BlackLook();
            else post.AmbientOcclusion.Enabled = true;
        }
    }
}
