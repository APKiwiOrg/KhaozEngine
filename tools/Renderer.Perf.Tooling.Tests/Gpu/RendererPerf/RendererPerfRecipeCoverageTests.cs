using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeCoverageTests
{
    [Fact]
    public void Draws_without_coverage_preserve_the_legacy_draw_encoding()
    {
        JsonObject rigid = Encode(new RigidInstanceDraw(new MeshHandle(7), Matrix4x4.Identity));
        JsonObject skinned = Encode(new SkinnedInstanceDraw(default, Matrix4x4.Identity));

        Assert.False(rigid.ContainsKey(nameof(RigidInstanceDraw.Coverage)));
        Assert.False(skinned.ContainsKey(nameof(SkinnedInstanceDraw.Coverage)));
        Assert.True(rigid.ContainsKey(nameof(RigidInstanceDraw.World)));
        Assert.True(skinned.ContainsKey(nameof(SkinnedInstanceDraw.Model)));
    }

    [Fact]
    public void Staged_coverage_is_refused_instead_of_encoding_as_an_empty_object()
    {
        // The descriptors are opaque frame evidence with no public state, so a recipe cannot describe them.
        var rigid = new RigidInstanceDraw(new MeshHandle(7), Matrix4x4.Identity) { Coverage = new CoverageEquipmentDraw(null!, 1, 1, 0) };
        var skinned = new SkinnedInstanceDraw(default, Matrix4x4.Identity) { Coverage = new SkinnedCoverageFrame(null!, 1, 1) };

        Assert.Throws<InvalidOperationException>(() => Encode(rigid));
        Assert.Throws<InvalidOperationException>(() => Encode(skinned));
    }

    static JsonObject Encode<T>(T draw) => JsonSerializer.SerializeToNode(draw, RendererPerfRecipe.Json)!.AsObject();
}
