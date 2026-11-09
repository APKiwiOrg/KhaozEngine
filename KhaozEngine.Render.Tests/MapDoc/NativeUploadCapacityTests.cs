using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeUploadCapacityTests
{
    [Fact]
    public void CapacityOnlyMeasurement_PricesUnresolvedFoliageWithoutAuthorizingItsClaim()
    {
        using var host = new NativeUploadTestScene();
        var description = new SceneUploadDescription(null, null,
            new(default, new[] { Matrix4x4.Identity }, new[] { .2f }, new[] { 0 },
                [new("invented", [new MeshHandle(12345, 1)], default, true)]), default);
        var capacity = host.Scene.MeasureResourceUploadCapacity(description);
        Assert.Equal(352L, capacity.OwnedGpuBytes);
        Assert.Equal(512L, capacity.PeakStagingGpuBytes);
        Assert.Equal(6400L, capacity.PeakCpuBytes);
        Assert.Equal(3656L, capacity.RetainedCpuBytes);
        Assert.Throws<ArgumentException>(() => host.Scene.MeasureResourceUpload(description));
    }
}
