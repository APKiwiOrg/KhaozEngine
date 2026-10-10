using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class NativeCollisionServiceTests
{
    [Fact]
    public void DryRun_NeverWritesOrResizesTheMesh()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: true);
        Assert.False(edit.Applied);
        Assert.NotEqual(edit.BeforeSha256, edit.AfterSha256);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Contains("wall-1", edit.AffectedPlacementIds);
        Assert.True(edit.Effects.Invalidates.HasFlag(MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency));
    }

    [Fact]
    public void Apply_WritesNewContentAndReloadsWithTheNewCollider()
    {
        using var f = new NativeCollisionToolFixture();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: false);
        Assert.True(edit.Applied);
        using var reopened = f.Reopen();
        var m = reopened.Service.Measure("wall-1");
        Assert.Equal(edit.AfterSha256, m.ColliderSha256);
        Assert.Equal(2.3f, m.EffectiveTop - m.EffectiveBottom, 4);
    }

    [Fact]
    public void BakedMeshCollider_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("compound boxes", Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("rock-mesh", 0f, 1f)).Message);
    }
}
