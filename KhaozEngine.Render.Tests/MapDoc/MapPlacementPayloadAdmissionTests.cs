using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapPlacementPayloadAdmissionTests
{
    static GltfMesh Triangle() => new(new ModelVertex[3], new uint[] { 0, 1, 2 });
    // One owned mesh header, three vertices and three uint indices. The detached resource adds 256 bytes.
    static long MeshBytes => 256L + 3L * ModelVertex.SizeInBytes + 12;

    [Fact]
    public void DetachedCopy_RetainsItsPayloadAndReleasesCopySlackBeforeConsumption()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2 * (512 + 4 * MeshBytes)));
        using var active = admission.Enter();
        using var preparation = admission.BeginPreparation();
        var source = Triangle(); source.Vertices[0].Position = new(1, 2, 3);
        var payload = MapPlacementPayloadPreparation.Copy(source, default);
        source.Vertices[0].Position = new(9);
        Assert.Equal(new System.Numerics.Vector3(1, 2, 3), payload.BorrowMesh.Vertices[0].Position);
        Assert.Equal(256 + MeshBytes, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        var receipt = preparation.Complete(256 + MeshBytes);
        receipt.Dispose(); receipt.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void SequentialDetachedCopies_FitOneCopyWorkspaceAndTheEarlierOutput()
    {
        long retained = 256 + MeshBytes;
        long copyWorkspace = 512 + 4 * MeshBytes;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: retained + copyWorkspace));
        using var active = admission.Enter();
        using var preparation = admission.BeginPreparation();
        var source = Triangle();
        var first = MapPlacementPayloadPreparation.Copy(source, default);
        var second = MapPlacementPayloadPreparation.Copy(source, default);
        Assert.NotSame(source.Vertices, first.BorrowMesh.Vertices);
        Assert.NotSame(first.BorrowMesh.Vertices, second.BorrowMesh.Vertices);
        Assert.Equal(2 * retained, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        var receipt = preparation.Complete(2 * retained);
        receipt.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
