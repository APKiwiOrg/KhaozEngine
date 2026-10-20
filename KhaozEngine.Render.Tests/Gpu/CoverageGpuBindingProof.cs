using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuBindingProof
{
    internal static void AssertRollback(CoverageGpuScene fixture)
    {
        var scene = fixture.Fixture.Scene;
        var actor = fixture.Actors[0];
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Camera.Azimuth = scene.Camera.Elevation = 0;
        scene.Camera.OrthoSize = 4;
        var source = new AttachmentTestSource();
        using var cache = new AttachmentSetCache(new Scene3DAttachmentScene(scene), source);
        var mesh = source.Mesh("plate.glb", Plate(actor.Data));
        Assert.True(Matrix4x4.Invert(actor.Palette[0], out var local));
        local.M14 = local.M24 = local.M34 = 0;
        local.M44 = 1;
        string setHash = source.Add("set.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [new RigidAttachmentPartDefinition(1, mesh, "root", local)])));
        var loaded = cache.Get("set.json", setHash);
        string coverageHash = source.Add("coverage.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(
            setHash, HostCoverageMapJson.SemanticSha256(actor.Data.Map),
            [new(1, 1, "r0", Matrix4x4.Identity, [new(0, 0, 0, 4)], .4f, -2, 2, .02f, .8f)])));
        var coverage = cache.GetCoverage(loaded, "coverage.json", coverageHash);
        var map = new ContractJointMap(actor.Skeleton,
            new SkeletonContract([new("root", null, true), new("hip", "root", true)]));
        var instance = new CoveredAttachmentInstance(1, loaded.Bind(map), coverage);
        using var binding = new HostCoverageBinding(new Scene3DAttachmentScene(scene), actor.Skeleton,
            [actor.Host], MotionKey.From(900), CoverageFallback.UncoveredCurrentPose);
        Assert.True(binding.TrySetEquipment([instance], out var failure), failure.ToString());
        Capture(1);
        Capture(1);
        Assert.True(binding.TrySetEquipment([instance with { InstanceId = 2 }], out failure), failure.ToString());
        byte[] before = Capture(2);
        Assert.False(binding.TrySetEquipment([instance with { InstanceId = 0 }], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        byte[] after = Capture(2);
        var motion = scene.ReadMotionTargetForTests();
        Assert.Contains(motion.Motion, v => !KhaozEngine.Render3D.Internal.MotionMath.IsBackground(v));
        Assert.All(motion.Motion.Where(v => !KhaozEngine.Render3D.Internal.MotionMath.IsBackground(v)),
            v => Assert.InRange(v.Length(), 0, .000001f));
        Assert.Equal(before.Length, after.Length);
        Assert.All(fixture.ReadMask(), value => Assert.Equal((byte)0, value));
        Assert.True(Enumerable.Range(0, before.Length / 4).Count(i => after[i * 4] > after[i * 4 + 2] * 1.4f) > 100);
        Assert.True(Enumerable.Range(0, before.Length / 4).Count(i => after[i * 4 + 2] > after[i * 4] * 2f) > 100);

        byte[] Capture(ulong expectedInstance)
        {
            SkinnedCoverageFrame frame = default;
            using var evidence = new CoverageSceneEvidence(fixture.Fixture);
            byte[] colour = evidence.Frame((s, _) =>
            {
                Assert.True(binding.TryPrepare(actor.Skeleton, actor.Palette, Matrix4x4.Identity,
                    new RigidInstanceDraw(default, Matrix4x4.Identity) { Tint = new(.12f, .7f, 1, 1) },
                    out frame, out var refused), refused.ToString());
                var draw = new SkinnedInstanceDraw(actor.Host, Matrix4x4.Identity) { Coverage = frame, Motion = MotionKey.From(900) };
                s.DrawSkinned(draw, actor.Palette);
                var group = s.BeginMeshOutline(Color.White, 1);
                s.DrawSkinnedOutline(group, draw, actor.Palette);
                foreach (var equipment in binding.PreparedEquipmentDraws.Span) s.Draw(equipment.Rigid);
            }, () => [frame]);
            Assert.Equal(CoveragePresentation.Dispatched, scene.GetCoverageOutcome(frame).Presentation);
            var surface = Assert.Single(evidence.Surfaces);
            Assert.Equal(0u, surface.HeaderWords[8]);
            var commands = frame.Preparation!.Commands.AsSpan(0, frame.Preparation.CommandCount).ToArray();
            Assert.Contains(commands, command => command.EquipmentInstanceId == expectedInstance);
            Assert.DoesNotContain(commands, command => command.EquipmentInstanceId != expectedInstance);
            var outline = scene.ReadCoverageOutlinesForTests();
            CoverageArtifacts.Write(new(colour, scene.ReadCoverageDepthForTests(), scene.ReadCoverageNormalForTests(),
                null, null, null, outline.Full, outline.Visible,
                surface.Signature, fixture.Fixture.Device.Backend,
                fixture.Fixture.Device.Capabilities.DeviceName, $"coverage-binding-{Guid.NewGuid():N}-instance{expectedInstance}")
            { Surfaces = evidence.Surfaces, SignatureSource = "selected-gpu" });
            return colour;
        }
    }

    static byte[] Plate(CoverageFixture data)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("plate");
        var primitive = mesh.UsePrimitive(new MaterialBuilder("solid"));
        for (int t = 0; t < data.RigidIndices.Length / 3; t++)
        {
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(int c) =>
                new(new VertexPositionNormal(data.RigidPositions[data.RigidIndices[t * 3 + c]], Vector3.UnitZ));
            primitive.AddTriangle(V(0), V(1), V(2));
        }
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string path = Path.Combine(Path.GetTempPath(), $"coverage-plate-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }
}
