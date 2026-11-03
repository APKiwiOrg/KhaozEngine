using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeMobilityTests
{
    [Fact]
    public void Static_defaults_preserve_the_legacy_draw_encoding()
    {
        RigidInstanceDraw[] draws =
        [
            default,
            new(new MeshHandle(7), Matrix4x4.Identity),
            new(new MeshHandle(7), Matrix4x4.Identity) { ShadowMobility = ShadowCasterMobility.Static },
        ];

        Assert.All(draws, draw =>
        {
            JsonObject encoded = JsonSerializer.SerializeToNode(draw, RendererPerfRecipe.Json)!.AsObject();
            Assert.False(encoded.ContainsKey(nameof(RigidInstanceDraw.ShadowMobility)));
            Assert.True(encoded.ContainsKey(nameof(RigidInstanceDraw.World)));
            Assert.True(encoded.ContainsKey(nameof(RigidInstanceDraw.CastsShadows)));
            Assert.True(encoded.ContainsKey(nameof(RigidInstanceDraw.Motion)));
        });
    }

    [Theory]
    [InlineData(ShadowCasterMobility.Dynamic, "\"Dynamic\"")]
    [InlineData((ShadowCasterMobility)99, "99")]
    public void Nondefault_mobility_changes_authored_and_derived_identity(ShadowCasterMobility mobility, string json)
    {
        using var original = new MobilityRecipe(ShadowCasterMobility.Static);
        using var changed = new MobilityRecipe(mobility);
        original.Freeze();
        changed.Freeze();
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(changed.Describe());

        Assert.Equal(json, authored["Geometry"]!["Draws"]![0]!["ShadowMobility"]!.ToJsonString());
        Assert.NotEqual(original.Facts.GeometrySha256, changed.Facts.GeometrySha256);
        Assert.NotEqual(original.Facts.RecipeSha256, changed.Facts.RecipeSha256);
        Assert.NotEqual(original.Facts.Derived.GeometrySha256, derived.GeometrySha256);
        Assert.NotEqual(original.Facts.Derived.ConstructionSha256, derived.ConstructionSha256);
    }

    [Fact]
    public void Dynamic_work_is_rejected_by_the_frozen_static_construction()
    {
        using var original = new MobilityRecipe(ShadowCasterMobility.Static);
        using var changed = new MobilityRecipe(ShadowCasterMobility.Dynamic);
        original.Freeze();
        changed.Freeze();
        JsonObject frozen = JsonNode.Parse(RendererPerfFrozenRecipe.Freeze(original.Describe()))!.AsObject();
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(changed.Describe());

        var error = Assert.Throws<RendererPerfFrozenRecipeException>(() => RendererPerfFrozenRecipe.Verify(
            frozen, authored, derived, RendererPerfFrozenRecipe.PlatformFamily));
        Assert.Equal(RendererPerfFrozenRecipeRefusal.AuthoredDiffers, error.Reason);
    }

    sealed class MobilityRecipe(ShadowCasterMobility mobility) : RendererPerfRecipe("empty")
    {
        protected override object Geometry => new
        {
            Draws = new[]
            {
                new RigidInstanceDraw(new MeshHandle(7), Matrix4x4.Identity) { ShadowMobility = mobility },
            },
        };
    }
}
