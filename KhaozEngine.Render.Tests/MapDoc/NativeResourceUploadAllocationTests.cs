using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeResourceUploadAllocationTests
{
    [Fact]
    public void MeasureFoliage_AllocatesOnlyItsReturnedDemandToken()
    {
        using var host = new NativeUploadTestScene();
        NativeResourceUploadTests.LoadParts(host.Scene);
        var data = new SceneUploadData(null, null, NativeRenderAssetFixtures.FoliageSource(), default);
        var expected = host.Scene.MeasureResourceUpload(data);
        SceneUploadDemand? token = null, measured = null;
        void Token() => token = new(expected.InputDigest, expected.OwnedGpuBytes, expected.PeakStagingGpuBytes,
            expected.PeakCpuBytes, expected.MaxRetiredGpuBytes, expected.TransferBytes);
        void Measure() => measured = host.Scene.MeasureResourceUpload(data);
        Token(); Measure(); Token(); Measure();

        long tokenBytes = AllocationMeasurement.Measure("one scene demand token", Token);
        long measuredBytes = AllocationMeasurement.Measure("native foliage upload measurement", Measure);

        Assert.True(tokenBytes > 0);
        Assert.Equal(tokenBytes, measuredBytes);
        Assert.Equal(token, measured);
    }
}
