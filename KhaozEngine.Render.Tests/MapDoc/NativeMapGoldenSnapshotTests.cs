using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapGoldenSnapshotTests
{
    [Fact]
    public void FoliageRecording_SurvivesResidencyDisposalWithoutPreregistration()
    {
        using var fixture = ResidencyFixture.Foliage();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.FoliageState(), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var recorder = new MapDrawRecorder();
        residency.Record(default, recorder);
        var snapshot = recorder.Snapshot();
        var foliage = snapshot.Resources.Where(r => r.Payload is MapFoliageResource)
            .Select(r => (MapFoliageResource)r.Payload).ToArray();
        Assert.NotEmpty(foliage);
        var transforms = foliage.SelectMany(f => f.WorldTransforms.ToArray()).ToArray();
        var models = foliage.SelectMany(f => f.Models).ToArray();
        var ranks = foliage.SelectMany(f => f.Ranks.ToArray()).ToArray();
        var indices = foliage.SelectMany(f => f.ModelIndices.ToArray()).ToArray();
        Assert.NotEmpty(transforms);
        Assert.NotEmpty(models);
        residency.Dispose();
        Assert.Equal(transforms, foliage.SelectMany(f => f.WorldTransforms.ToArray()));
        Assert.Equal(models, foliage.SelectMany(f => f.Models));
        Assert.Equal(ranks, foliage.SelectMany(f => f.Ranks.ToArray()));
        Assert.Equal(indices, foliage.SelectMany(f => f.ModelIndices.ToArray()));
    }
}
