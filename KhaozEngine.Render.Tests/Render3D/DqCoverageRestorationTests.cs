using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqCoverageRestorationTests
{
    public static TheoryData<bool, bool, bool, bool> Routes
    {
        get
        {
            var data = new TheoryData<bool, bool, bool, bool>();
            foreach (bool covered in new[] { false, true })
                foreach (bool dq in new[] { false, true })
                    foreach (bool gpu in new[] { false, true })
                        foreach (bool split in new[] { false, true }) data.Add(covered, dq, gpu, split);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void Ordinary_replacement_keeps_old_and_current_rejection_local_and_seeds_current_pose(
        bool covered, bool dq, bool gpu, bool split)
    {
        using var h = new CoverageSceneFixture();
        Scene3D scene = h.Scene;
        RejectionWitness.Configure(scene);
        scene.Camera.OrthoSize = 3.5f;
        scene.UseGpuSkinning = gpu;
        scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        SkinnedGltfMesh stableMesh = DqMotionWitness.Patch(SkinningProfile.LinearBlend);
        SkinnedMeshHandle stable = scene.LoadSkinnedMesh(stableMesh);
        MotionKey stableKey = MotionKey.From(30), actor = MotionKey.From(10);
        Matrix4x4 stableWorld = Matrix4x4.CreateTranslation(5, 0, 0);
        Matrix4x4 oldJoint = Matrix4x4.CreateTranslation(0, .25f, 0);
        h.Begin();
        CoverageSurfaceBounds? oldFinal = null;
        if (covered)
        {
            var frame = h.Stage(Matrix4x4.CreateTranslation(-2, 0, 0));
            scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            oldFinal = frame.Preparation!.WorldEnvelope;
        }
        else scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], Matrix4x4.CreateTranslation(-2, 0, 0))
        { Motion = actor }, h.Palette);
        scene.DrawSkinned(new SkinnedInstanceDraw(stable, stableWorld) { Motion = stableKey }, [oldJoint, oldJoint]);
        h.Render(160, 80);

        var committed = (CoverageMotionHistory)DqGpuReadback.Field(scene, "_coverageMotion");
        if (covered)
        {
            var surface = Assert.Single(committed.PreviousSurfaces.ToArray());
            Assert.Equal(actor, surface.Actor);
            Assert.Equal(oldFinal!.Value, surface.Bounds);
            Assert.True(surface.Presented);
        }
        else Assert.Empty(committed.PreviousSurfaces.ToArray());

        SkinnedGltfMesh replacement = DqMotionWitness.Patch(dq ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend);
        SkinnedMeshHandle next = scene.LoadSkinnedMesh(replacement);
        Matrix4x4 world = Matrix4x4.CreateTranslation(.5f, 0, 0);
        Matrix4x4 joint = Matrix4x4.CreateTranslation(.125f, 0, 0);
        h.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(next, world) { Motion = actor }, [joint, joint]);
        scene.DrawSkinned(new SkinnedInstanceDraw(stable, stableWorld) { Motion = stableKey },
            [Matrix4x4.CreateTranslation(0, -.25f, 0), Matrix4x4.CreateTranslation(0, -.25f, 0)]);
        h.Render(160, 80);

        var stableIdentity = new SkinnedMotionIdentity(stable.Index, stable.Generation,
            ((ulong)(uint)stable.Generation << 32) | (uint)stable.Index, stableMesh.Profile);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(stableKey, stableIdentity,
            out Matrix4x4 retainedWorld, out var retainedPalette, out _));
        Assert.Equal(stableWorld, retainedWorld);
        Assert.Equal(oldJoint, retainedPalette[0]);
        var (stableX, stableY) = Site(new Vector3(5, 0, 0));
        Vector2[] ordinary = RejectionWitness.Vertices(h.Record);
        Assert.Equal(0, RejectionWitness.At(ordinary, new Vector2(stableX / 80f - 1, 1 - stableY / 40f)));

        if (covered)
        {
            Assert.True(oldFinal!.Value.Weight.IsFinite);
            var current = Assert.IsType<CoverageSurfaceBounds>(typeof(Scene3D)
                .GetMethod("UncoveredCoverageBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scene, [actor]));
            Assert.True(current.Weight.IsFinite, "An admitted ordinary replacement must retain finite restoration bounds.");
            Assert.Equal(scene.CurrentFrameView.RenderOrigin, current.Origin);
            Assert.InRange(current.Weight.Min, .999, 1);
            Assert.InRange(current.Weight.Max, 1, 1.001);
            Assert.True(scene.ActiveMotionHistory.TryGetCurrentBounds(actor, out MeshBounds admitted));
            MeshBounds restored = current.ToWorld();
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.True(float.IsFinite(restored.Min[axis]) && float.IsFinite(restored.Max[axis]));
                if (dq) Assert.True(restored.Min[axis] <= admitted.Min[axis] && restored.Max[axis] >= admitted.Max[axis]);
                foreach (var vertex in replacement.Vertices)
                    Assert.InRange((vertex.Position + new Vector3(.625f, 0, 0))[axis], restored.Min[axis], restored.Max[axis]);
            }
            var rectangles = scene.CoverageTransitionForTests.Rectangles;
            Assert.NotEmpty(rectangles);
            Assert.DoesNotContain(rectangles, r => r.X == 0 && r.Y == 0 && r.Right == 160 && r.Bottom == 80);
            Assert.DoesNotContain(rectangles, r => Contains(r, stableX, stableY));
            foreach (Vector3 position in new[] { new Vector3(-2, 0, 0), new Vector3(.625f, 0, 0) })
            {
                var (x, y) = Site(position);
                Assert.Contains(rectangles, r => Contains(r, x, y));
            }
        }
        else Assert.Empty(scene.CoverageTransitionForTests.Rectangles);

        if (gpu)
        {
            string vertex = dq ? ShaderSources.DqSkinnedModelMotionVert : ShaderSources.SkinnedModelMotionVert;
            var binding = h.Record.Bindings.First(b => b.Pipeline is FakePipeline { Request: { } p }
                && p.VertexGlsl == vertex);
            var previous = binding.Sets[3];
            var range = (GpuBufferRange)Assert.IsType<FakeResourceSet>(previous.Set).Resources[1];
            byte[] bytes = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer)).Data!;
            int offset = (int)(range.Offset + previous.DynamicOffset);
            Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(bytes.AsSpan(offset, 64)));
            if (dq)
            {
                var palette = MemoryMarshal.Cast<byte, RigidDualQuaternion>(bytes.AsSpan(offset + 64, 64));
                Assert.All(palette.ToArray(), value =>
                {
                    Assert.Equal(Vector4.UnitW, value.Real);
                    Assert.Equal(new Vector4(.0625f, 0, 0, -0f), value.Dual);
                });
            }
            else Assert.Equal(joint, MemoryMarshal.Read<Matrix4x4>(bytes.AsSpan(offset + 64, 64)));
        }
        else
        {
            var previous = (List<Vector3>)DqGpuReadback.Field(scene, "_cpuSkinnedPrevious");
            Assert.Equal(replacement.Vertices.Select(v => v.Position + new Vector3(.625f, 0, 0)), previous.Take(4));
            Assert.Equal(stableMesh.Vertices.Select(v => v.Position + new Vector3(5, .25f, 0)), previous.Skip(4));
        }

        (int X, int Y) Site(Vector3 p)
        {
            Vector4 clip = Vector4.Transform(new Vector4(p - scene.CurrentFrameView.RenderOrigin, 1),
                scene.CurrentFrameView.ViewProjection);
            return ((int)((clip.X / clip.W * .5f + .5f) * 160), (int)((.5f - clip.Y / clip.W * .5f) * 80));
        }
    }

    [Fact]
    public void Uncertain_restoration_bounds_still_reject_the_whole_view()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1);
        mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(CoverageSurfaceBounds.FromHomogeneousRender(default, Vector3.Zero,
            CoverageIntervals.Invalid), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, RejectionWitness.View());
        Assert.True(mask.Rejects(0, 0));
        Assert.True(mask.Rejects(99, 99));
    }

    static bool Contains(CoverageTransitionMask.Rectangle r, int x, int y) =>
        x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
}
