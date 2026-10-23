using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapRecorderAdmissionTests
{
    [Fact]
    public void Recorder_RefusesResourceGrowthBeforePublishingTheResource()
    {
        var (resource, _) = Light();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        using var active = admission.Enter(); using var operation = admission.BeginPreparation();
        var recorder = new MapDrawRecorder();
        Assert.Throws<MapSceneCapacityException>(() => recorder.Register(resource));
        Assert.Empty(recorder.Snapshot().Resources);
        Assert.Equal(0, admission.PeakBytes);
    }

    [Fact]
    public void Recorder_AdmitsDrawGrowthAndSnapshotCopiesBeforeAllocation()
    {
        var (resource, draw) = Light();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 16_384));
        using var active = admission.Enter(); using var operation = admission.BeginPreparation();
        var recorder = new MapDrawRecorder(); recorder.Register(resource);
        using (var held = admission.Workspace())
        {
            held.Reserve(admission.RemainingPreparedBytes);
            Assert.Throws<MapSceneCapacityException>(() => recorder.Write(draw));
        }
        Assert.Empty(recorder.Snapshot().Draws);
        recorder.Write(draw);
        using (var held = admission.Workspace())
        {
            held.Reserve(admission.RemainingPreparedBytes);
            Assert.Throws<MapSceneCapacityException>(() => recorder.Snapshot());
        }
        Assert.Single(recorder.Snapshot().Draws);
    }

    static (MapPreparedResource, MapDrawRecord) Light()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapPlacementTests.Scene(closure);
        var recorder = new MapDrawRecorder(); NativeMapLights.Record(scene, NativeMapPlacementTests.Frame, recorder);
        var snapshot = recorder.Snapshot();
        return (snapshot.Resources.First(), snapshot.Draws.First());
    }
}
