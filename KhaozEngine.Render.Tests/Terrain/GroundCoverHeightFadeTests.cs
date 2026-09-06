using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class GroundCoverHeightFadeTests
{
    static readonly IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> Meshes =
        new Dictionary<string, IReadOnlyList<MeshHandle>> { ["grass"] = [new MeshHandle(4)] };

    static GroundCoverRenderOptions Options() => new()
    {
        DrawRadius = 30f,
        FadeBandWidth = 20f,
        DistantDensity = 0f,
        InstanceFadeBandWidth = 6f,
        FadeMode = GroundCoverFadeMode.HeightScale,
    };

    static GroundCoverInstance Cover(float x) => new("grass", new Vector3(x, 2, 3),
        Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationZ(.3f) *
        Matrix4x4.CreateTranslation(x, 2, 3), .5f);

    static SceneInstances Queue(GroundCoverInstance cover, Vector3 focus, GroundCoverRenderOptions? options = null)
    {
        var queue = new SceneInstances();
        GroundCoverRenderer.Queue(queue, [cover], Meshes, focus, options ?? Options());
        return queue;
    }

    [Fact]
    public void HeightFadeKeepsTheRootAndFootprintFixedOnSlopedGround()
    {
        GroundCoverInstance cover = Cover(17f);
        SceneInstances.Instance faded = Assert.Single(Queue(cover, new Vector3(0, 0, 3)).Items);

        Assert.Equal(0f, faded.DissolveThreshold);
        Assert.Equal(cover.Transform.Translation, faded.World.Translation);
        Assert.Equal(new Vector3(cover.Transform.M11, cover.Transform.M12, cover.Transform.M13),
            new Vector3(faded.World.M11, faded.World.M12, faded.World.M13));
        Assert.Equal(new Vector3(cover.Transform.M31, cover.Transform.M32, cover.Transform.M33),
            new Vector3(faded.World.M31, faded.World.M32, faded.World.M33));
        Assert.InRange(faded.World.M22 / cover.Transform.M22, .499f, .501f);
        Assert.InRange(faded.World.GetDeterminant(), .1f, cover.Transform.GetDeterminant());
    }

    [Fact]
    public void NearbyCoverKeepsItsAuthoredTransform()
    {
        GroundCoverInstance cover = Cover(5f);
        Assert.Equal(cover.Transform, Assert.Single(Queue(cover, Vector3.Zero).Items).World);
    }

    [Fact]
    public void MovementChangesHeightSmoothlyWithoutMovingRootsOrIntroducingCutouts()
    {
        GroundCoverInstance cover = Cover(17f);
        Matrix4x4 first = Assert.Single(Queue(cover, new Vector3(0, 0, 3)).Items).World;
        Matrix4x4 next = Assert.Single(Queue(cover, new Vector3(.02f, 0, 3)).Items).World;
        Assert.Equal(first.Translation, next.Translation);
        Assert.InRange(next.M22 - first.M22, 0.0001f, .01f);
        Assert.Equal(first, Assert.Single(Queue(cover, new Vector3(0, 0, 3)).Items).World);
    }

    [Fact]
    public void FullyFadedCoverIsCulledBeforeASingularTransformCanReachTheShader()
    {
        Assert.Empty(Queue(Cover(20f), new Vector3(0, 0, 3)).Items);
    }

    [Fact]
    public void ExtendingTheHorizonDoesNotExtendTheDenseArea()
    {
        GroundCoverRenderOptions options = Options();
        options.DrawRadius = 64f;
        options.DensityRadius = 32f;
        options.FadeBandWidth = 20f;
        options.DistantDensity = .1f;
        GroundCoverInstance dense = Cover(40f);
        GroundCoverInstance distant = dense with { ThinningRank = .02f };

        Assert.Empty(Queue(dense, new Vector3(0, 0, 3), options).Items);
        Assert.Single(Queue(distant, new Vector3(0, 0, 3), options).Items);
        options.DrawRadius = 96f;
        Assert.Empty(Queue(dense, new Vector3(0, 0, 3), options).Items);
    }

    [Fact]
    public void DensityRadiusAlsoAppliesWhenTheFadeBandIsDisabled()
    {
        GroundCoverRenderOptions options = Options();
        options.DrawRadius = 64f;
        options.DensityRadius = 32f;
        options.FadeBandWidth = 0f;
        Assert.Empty(Queue(Cover(40f), new Vector3(0, 0, 3), options).Items);
    }

    [Fact]
    public void InvalidFadeSettingsAreRejected()
    {
        GroundCoverRenderOptions options = Options();
        options.InstanceFadeBandWidth = float.NaN;
        Assert.Throws<ArgumentException>(() => Queue(Cover(5f), Vector3.Zero, options));
        options.InstanceFadeBandWidth = -1f;
        Assert.Throws<ArgumentException>(() => Queue(Cover(5f), Vector3.Zero, options));
        options.InstanceFadeBandWidth = 1f;
        options.FadeMode = (GroundCoverFadeMode)99;
        Assert.Throws<ArgumentException>(() => Queue(Cover(5f), Vector3.Zero, options));
        options.FadeMode = GroundCoverFadeMode.HeightScale;
        options.DensityRadius = float.PositiveInfinity;
        Assert.Throws<ArgumentException>(() => Queue(Cover(5f), Vector3.Zero, options));
    }
}
