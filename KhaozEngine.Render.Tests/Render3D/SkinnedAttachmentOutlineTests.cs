using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedAttachmentOutlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_outline_uses_the_same_pose_and_dissolve(bool tileFacade)
    {
        using var fixture = new MixedAttachmentSubmissionFixture(tileFacade);
        fixture.Scene.Begin();
        MeshOutlineGroup group = fixture.Scene.BeginMeshOutline(Color.White, 2, MeshOutlineOcclusion.None);
        fixture.Bound.QueueOutline(fixture.Skeleton, fixture.Palette, fixture.Model, group, .5f, true);

        Assert.Equal(3, fixture.Capability.OutlineCalls);
        Assert.Equal(3, fixture.Scene.MeshOutlinePartCount);
        Assert.Equal(MeshOutlineOcclusion.None, fixture.Scene.MeshOutlineOcclusionAt(0));
        Assert.All(fixture.Capability.OutlineDissolves, dissolve => Assert.Equal(.5f, dissolve));
        Assert.All(fixture.Capability.OutlineComplements, complement => Assert.True(complement));
        Assert.Equal(new Vector3(11, 2, 0), fixture.Capability.OutlineWorlds[0].Translation);
        Assert.Equal(fixture.Model, fixture.Capability.OutlineWorlds[1]);
        Assert.Equal(fixture.Model, fixture.Capability.OutlineWorlds[2]);
        Assert.Equal(4, fixture.Scene.OutlinePoseMatrixCount);
        Assert.Equal(Matrix4x4.CreateTranslation(0, 1, 0), fixture.Scene.OutlinePoseMatrixAt(0));
        Assert.Equal(Matrix4x4.CreateTranslation(0, -1, 0), fixture.Scene.OutlinePoseMatrixAt(1));
        Assert.Equal(fixture.Scene.OutlinePoseMatrixAt(0), fixture.Scene.OutlinePoseMatrixAt(2));
        Assert.Equal(.5f, fixture.Scene.MeshOutlineDissolveAt(0, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_later_part_publishes_no_prefix(bool rigidLast)
    {
        using var fixture = new MixedAttachmentSubmissionFixture(rigidLast: rigidLast);
        MeshOutlineGroup group = fixture.Scene.BeginMeshOutline(Color.White, 2);
        fixture.Palette[0].M41 = float.NaN;

        Assert.Throws<ArgumentException>(() => fixture.Bound.QueueOutline(fixture.Skeleton, fixture.Palette, fixture.Model, group, .5f, true));

        fixture.AssertNoCalls();
    }

    [Fact]
    public void Default_outline_is_solid_and_disposed_cache_cannot_publish_outlines()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        MeshOutlineGroup group = fixture.Scene.BeginMeshOutline(Color.White, 2);
        fixture.Bound.QueueOutline(fixture.Skeleton, fixture.Palette, fixture.Model, group);
        Assert.All(fixture.Capability.OutlineDissolves, dissolve => Assert.Equal(0f, dissolve));
        Assert.All(fixture.Capability.OutlineComplements, complement => Assert.False(complement));
        fixture.Scene.Begin();
        fixture.Capability.Clear();
        fixture.Cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Bound.QueueOutline(fixture.Skeleton, fixture.Palette, fixture.Model, group));
        fixture.AssertNoCalls();
    }
}
