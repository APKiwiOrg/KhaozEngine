using KhaozEngine.Render3D;
using KhaozEngine.MapDoc.Render3D;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeResourceUploadAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSink_RefusedAdmissionDoesNotAllocatePayloadCopies(bool terrain)
    {
        using var host = new NativeUploadTestScene();
        var resource = LargeResource(terrain);
        var sink = new MapScene3DSink(host.Scene);
        var admission = new MapRenderAdmission(new(MaxResidentCpuBytes: 1));
        MapUploadReservation? reservation = null;
        void Refused() => reservation = admission.TryReserve(sink.MeasureUpload(resource));
        Refused();
        long allocated = AllocationMeasurement.Measure("refused native upload", Refused, 32 * 1024 * 1024);
        Assert.Null(reservation); Assert.Equal(0, admission.CpuBytes); Assert.Equal(0, host.Device.UploadBytes);
        Assert.True(allocated < 16_384, $"Refused admission allocated {allocated} bytes for borrowed payloads.");
    }
    [Fact]
    public void NativeSink_DeniedClaimDoesNotDetachOrSynthesizePayloads()
    {
        using var host = new NativeUploadTestScene(); var sink = new MapScene3DSink(host.Scene);
        var resource = LargeResource(true); var demand = sink.MeasureUpload(resource);
        // Both ground layers and all detached mesh bytes coexist with renderer preparation inside this demand.
        Assert.True(demand.PeakCpuBytes >= 2L * 1024 * 1024 * 4 * 2 + 100_000L * ModelVertex.SizeInBytes);
        void Denied()
        {
            using var denied = new NativeUploadReservation(demand) { Deny = true };
            Assert.Throws<System.InvalidOperationException>(() => sink.BeginUpload(resource, denied));
        }
        Denied();
        long allocated = AllocationMeasurement.Measure("denied native claim", Denied, 32 * 1024 * 1024);
        Assert.True(allocated < 16_384, $"Denied claim allocated {allocated} bytes before detachment.");
        Assert.Equal(0, host.Device.UploadBytes);
    }
    static MapPreparedResource LargeResource(bool terrain)
    {
        var vertices = new ModelVertex[100_000];
        var mesh = new GltfMesh(vertices, new uint[] { 0, 1, 2 });
        var materials = new MapTerrainMaterialSet([
            new MapTerrainLayer(1, new("textured", Vector4.One, 1, 0, 0, new(new DecodedImage(new byte[1024 * 1024 * 4], 1024, 1024), null, null))),
            new MapTerrainLayer(2, new("white", Vector4.One, 1, 0, 0, default))]);
        MapResourcePayload payload = terrain ? new MapTerrainResource(mesh, materials, [default]) : new MapRigidResource(mesh, default);
        return new MapPreparedResource(new(new(terrain ? MapRenderKind.Terrain : MapRenderKind.Placement, "large", "mesh", 0),
            new("closure", "source", "scope", "policy", 1)), new(0, 0, 0, 1, 1, 1),
            payload, "large");
    }
    [Fact]
    public void MeasureFoliage_AllocatesOnlyItsReturnedDemandToken()
    {
        using var host = new NativeUploadTestScene();
        NativeResourceUploadTests.LoadParts(host.Scene);
        var data = new SceneUploadData(null, null, NativeRenderAssetFixtures.FoliageSource(), default);
        var expected = host.Scene.MeasureResourceUpload(data);
        SceneUploadDemand? token = null, measured = null;
        void Token() => token = new(expected.InputDigest, expected.OwnedGpuBytes, expected.PeakStagingGpuBytes,
            expected.PeakCpuBytes, expected.MaxRetiredGpuBytes, expected.TransferBytes, expected.RetainedCpuBytes);
        void Measure() => measured = host.Scene.MeasureResourceUpload(data);
        Token(); Measure(); Token(); Measure();

        long tokenBytes = AllocationMeasurement.Measure("one scene demand token", Token);
        long measuredBytes = AllocationMeasurement.Measure("native foliage upload measurement", Measure);

        Assert.True(tokenBytes > 0);
        Assert.Equal(tokenBytes, measuredBytes);
        Assert.Equal(token, measured);
    }
}
