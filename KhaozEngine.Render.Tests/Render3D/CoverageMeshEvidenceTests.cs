using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMeshEvidenceTests
{
    [Theory]
    [InlineData("cutout")]
    [InlineData("splat")]
    [InlineData("tile")]
    public void Ineligible_rigid_upload_does_not_capture_coverage_evidence(string route)
    {
        using var h = new CoverageSceneFixture();
        var uploads = Uploads(h.Scene);
        int count = uploads.Count;
        var handle = route switch
        {
            "cutout" => h.Scene.LoadMesh(h.RigidMesh, new Scene3D.SurfaceMaps(default, alphaCutoff: .5f)),
            "splat" => h.Scene.LoadMesh(h.RigidMesh, new Scene3D.SplatMaterialHandle(0)),
            _ => h.Scene.LoadMesh(h.RigidMesh, new Scene3D.TileGroundMaterialHandle(0)),
        };
        Assert.Equal(count, uploads.Count);
        Assert.False(uploads.ContainsKey((handle.Index, handle.Generation)));
        h.Begin();
        Assert.False(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(handle, Matrix4x4.Identity)], new RigidInstanceDraw[1], out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eligible_rigid_upload_keeps_exact_geometry_and_finite_lane_snapshot(bool nonfinite)
    {
        using var h = new CoverageSceneFixture();
        var vertices = h.RigidMesh.Vertices.ToArray();
        vertices[^1].Normal.X = nonfinite ? float.NaN : 1;
        var mesh = new GltfMesh(vertices, h.RigidMesh.Indices32.ToArray());
        var handle = h.Scene.LoadMesh(mesh, new Scene3D.SurfaceMaps(default));
        var evidence = Uploads(h.Scene)[(handle.Index, handle.Generation)];
        Assert.Equal(h.Support.RenderEvidence.Slices[0], evidence.Fingerprint);
        Assert.Equal(h.RigidMesh.Vertices.Select(v => v.Position).Aggregate(Vector3.Min), evidence.Bounds.Min);
        Assert.Equal(h.RigidMesh.Vertices.Select(v => v.Position).Aggregate(Vector3.Max), evidence.Bounds.Max);
        Assert.Equal(!nonfinite, evidence.FinitePositionSinkInputs);
        vertices[^1].Normal.X = nonfinite ? 0 : float.NaN;
        vertices[0].Position = new(99);
        h.Begin();
        Assert.Equal(!nonfinite, h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(handle, Matrix4x4.Identity)], new RigidInstanceDraw[1], out var failure));
        Assert.Equal(nonfinite ? CoverageFailureCode.IdentityMismatch : CoverageFailureCode.None, failure.Code);
    }

    static Dictionary<(int, int), CoverageUploadEvidence> Uploads(Scene3D scene) =>
        (Dictionary<(int, int), CoverageUploadEvidence>)typeof(Scene3D)
            .GetField("_coverageUploads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
}
