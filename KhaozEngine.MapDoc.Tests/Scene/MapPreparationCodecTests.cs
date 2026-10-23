using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationCodecTests
{
    [Fact]
    public void SurfaceMetadataHash_ReservesAndReleasesItsJsonWriterCapacity()
    {
        var surface = KhaozEngine.Tests.MapDoc.Prefabs.PrefabFixtures.Surface("invented", MapSurfaceRole.SupportFloor);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 8192));
        using var active = admission.Enter();
        Assert.Equal(64, MapSurfaceSemantics.SurfaceDigest(surface, []).Length);
        Assert.Equal(8192, admission.PeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    static MapSurfacePatch Patch() => new()
    {
        Key = new("invented", 0, 0), Width = 1, Depth = 1,
        Heights = new int[4], Cells = new MapSurfaceCell[1], Presence = [1UL],
    };

    [Fact]
    public void RepeatedDigest_ReleasesAllEncodingWorkspace()
    {
        var patch = Patch();
        string expected = MapSurfaceSemantics.PatchDigest(patch);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32_768));
        using var active = admission.Enter();
        for (int i = 0; i < 100; i++) Assert.Equal(expected, MapSurfaceSemantics.PatchDigest(patch));
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.InRange(admission.PeakBytes, 1, 32_768);
    }

    [Fact]
    public void CanonicalHashing_ChargesGrowthOverlapAndReleasesWorkspace()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 10_000));
        using var active = admission.Enter();
        using (var writer = new MapCanonical.HashingBufferWriter())
        {
            Assert.Equal(8192, admission.WorkspaceBytes);
            var error = Assert.Throws<MapSceneCapacityException>(() => writer.GetMemory(9000));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 24576, 10000), error.Capacity);
            Assert.Equal(8192, admission.WorkspaceBytes);
        }
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void Decode_OnlyRetainsPatchInventoryAndUnwindsInvalidPayload()
    {
        byte[] encoded = MapSurfacePatchCodec.Encode(Patch());
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32_768));
        using var active = admission.Enter();
        var decoded = MapSurfacePatchCodec.Decode(encoded, Patch().Key);
        Assert.Equal(32, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(encoded, new("wrong", 0, 0)));
        Assert.Equal(32, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
