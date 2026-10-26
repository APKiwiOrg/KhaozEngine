using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapGoldenSnapshotTests
{
    [Fact]
    public void FoliageEvidence_SurvivesCaptureResidencyDisposal()
    {
        using var fixture = ResidencyFixture.Foliage();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.FoliageState(), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var snapshot = NativeMapGoldenScene.SnapshotInventory(residency, default);
        var foliage = snapshot.Resources.Where(r => r.Payload is MapFoliageResource)
            .Select(r => (MapFoliageResource)r.Payload).ToArray();
        Assert.NotEmpty(foliage);
        var transforms = foliage.SelectMany(f => f.WorldTransforms.ToArray()).ToArray();
        var models = foliage.SelectMany(f => f.Models).ToArray();
        Assert.NotEmpty(transforms);
        Assert.NotEmpty(models);
        residency.Dispose();
        Assert.Equal(transforms, foliage.SelectMany(f => f.WorldTransforms.ToArray()));
        Assert.Equal(models, foliage.SelectMany(f => f.Models));
    }
}
