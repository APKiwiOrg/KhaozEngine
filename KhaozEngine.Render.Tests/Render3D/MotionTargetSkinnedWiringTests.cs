using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The skinned paths through the real <see cref="Scene3D"/> frame once it allocates the motion target: what
/// the CPU-skinned draws bind at the scene's bind sites, and what each GPU-skinned caster's last-frame slot holds.</summary>
public sealed class MotionTargetSkinnedWiringTests
{
    static RecordingGpuCommandList Frame(MotionTestScene harness, Action<Scene3D> queue)
    {
        harness.Scene.Begin();
        queue(harness.Scene);
        harness.Scene.PrepareFrame();
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true, CaptureBindings = true };
        harness.Scene.RenderInternal(cl, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        return cl;
    }

    static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [ShaderSources.ModelVert] = nameof(ShaderSources.ModelVert),
        [ShaderSources.ModelFrag] = nameof(ShaderSources.ModelFrag),
        [ShaderSources.ModelDissolveFrag] = nameof(ShaderSources.ModelDissolveFrag),
        [ShaderSources.ModelMotionVert] = nameof(ShaderSources.ModelMotionVert),
        [ShaderSources.ModelMotionFrag] = nameof(ShaderSources.ModelMotionFrag),
        [ShaderSources.ModelCpuSkinnedMotionVert] = nameof(ShaderSources.ModelCpuSkinnedMotionVert),
        [ShaderSources.ModelDissolveMotionFrag] = nameof(ShaderSources.ModelDissolveMotionFrag),
    };

    // The program a recorded draw's pipeline was built from, as "vertex + fragment", or null for another program.
    static string? Program(IGpuPipeline? pipeline)
    {
        if (pipeline is not FakePipeline { Request: { } request }) return null;
        return Names.TryGetValue(request.VertexGlsl, out string? vertex)
            ? vertex + " + " + Names.GetValueOrDefault(request.FragmentGlsl, "another fragment")
            : null;
    }

    // The model-pass draws that went through a rigid or CPU-skinned program. The frames below queue no rigid instance,
    // so these are exactly the CPU-skinned draws.
    static RecordingGpuCommandList.DrawBindings[] ModelDraws(RecordingGpuCommandList cl) =>
        cl.Bindings.Where(d => Program(d.Pipeline) is not null).ToArray();

    [Fact]
    public void CpuSkinnedDrawsBindTheirTemporalVariantsThroughTheScenesBindSitesAndTheBasePipelinesOtherwise()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = false;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        // Plain, dissolving, plain: the pass binds before its loop, switches to dissolve, then switches back, so all
        // three of its bind sites run.
        void Queue(Scene3D s)
        {
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = MotionKey.From(1) }, mesh.RestPose);
            var dissolving = new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(.5f, 0f, 0f))
            {
                Dissolve = .5f,
                DissolveEdgeWidth = .1f,
                Motion = MotionKey.From(2),
            };
            s.DrawSkinned(dissolving, mesh.RestPose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(-.5f, 0f, 0f)), mesh.RestPose);
        }

        using RecordingGpuCommandList off = Frame(harness, Queue);
        RecordingGpuCommandList.DrawBindings[] baseDraws = ModelDraws(off);
        Assert.Equal(new[] { "ModelVert + ModelFrag", "ModelVert + ModelDissolveFrag", "ModelVert + ModelFrag" },
            baseDraws.Select(d => Program(d.Pipeline)).ToArray());
        Assert.All(baseDraws, draw =>
        {
            Assert.Equal(new uint[] { 0 }, draw.Sets.Keys.Order().ToArray());
            Assert.Equal(new uint[] { 0, 1 }, draw.VertexBuffers.Keys.Order().ToArray());
        });

        scene.ForceTemporalForTests = true;
        using RecordingGpuCommandList on = Frame(harness, Queue);
        ModelMotionResources motion = scene.MotionResourcesForTests!;
        RecordingGpuCommandList.DrawBindings[] temporalDraws = ModelDraws(on);
        Assert.Equal(new[]
        {
            "ModelCpuSkinnedMotionVert + ModelMotionFrag", "ModelCpuSkinnedMotionVert + ModelDissolveMotionFrag",
            "ModelCpuSkinnedMotionVert + ModelMotionFrag",
        }, temporalDraws.Select(d => Program(d.Pipeline)).ToArray());
        Assert.All(temporalDraws, draw =>
        {
            Assert.Equal(new uint[] { 0, 1 }, draw.Sets.Keys.Order().ToArray());
            Assert.Same(motion.FrameSet, draw.Sets[1].Set);
            Assert.Equal(new uint[] { 0, 1, 2 }, draw.VertexBuffers.Keys.Order().ToArray());
            Assert.Same(motion.CpuPrevious, draw.VertexBuffers[2]);
        });
    }

    // Slot s of the last-frame image: its world matrix, then its first `bones` palette entries.
    static (Matrix4x4 World, Matrix4x4[] Bones) Slot(byte[] image, uint slot, int bones)
    {
        ReadOnlySpan<byte> window = image.AsSpan((int)(slot * SkinnedMotionPalette.SlotBytes));
        return (MemoryMarshal.Read<Matrix4x4>(window),
            MemoryMarshal.Cast<byte, Matrix4x4>(window.Slice(64, bones * 64)).ToArray());
    }

    static Matrix4x4[] Composed(Matrix4x4[] pose, SkinnedGltfMesh mesh) =>
        pose.Select((joint, b) => SkinningMath.Compose(joint, mesh.InverseBind[b])).ToArray();

    [Fact]
    public void EachGpuSkinnedCasterPacksLastFrameAtItsOwnSlotReducedAgainstThisFramesOrigin()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);   // three bones
        SkinnedGltfMesh fourBoneMesh = SkinnedMeshBuilder.BuildTube(0.22f, 1.5f, 6, 4, 4, Axis.Y);
        SkinnedMeshHandle fourBones = scene.LoadSkinnedMesh(fourBoneMesh);
        MotionKey body = MotionKey.From(21), moved = MotionKey.From(22);
        Matrix4x4 lastWorld = Matrix4x4.CreateTranslation(.5f, 0f, -.25f), world = Matrix4x4.CreateTranslation(.75f, 0f, -.25f);
        Matrix4x4[] lastPose = MotionTestScene.Bent(mesh, .2f), pose = MotionTestScene.Bent(mesh, .6f);
        Matrix4x4[] fourBonePose = MotionTestScene.Bent(fourBoneMesh, .4f);

        scene.RenderOrigin = new Vector3(128f, 0f, 0f);
        Frame(harness, s =>
        {
            s.DrawSkinned(new SkinnedInstanceDraw(tube, lastWorld) { Motion = body }, lastPose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, lastWorld) { Motion = moved }, lastPose);
        }).Dispose();

        // One grid cell on, which history carries across, so the pack must reduce against THIS frame's origin.
        scene.RenderOrigin = new Vector3(256f, 0f, 0f);
        using RecordingGpuCommandList second = Frame(harness, s =>
        {
            // Far above the view and casting no shadow: culled, so submission 0 takes no slot and the keyed draws'
            // slots differ from their submission indices.
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(0f, 500f, 0f)), lastPose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, world) { Motion = body }, pose);
            // The key now draws a mesh with another bone count, so its last palette no longer fits.
            s.DrawSkinned(new SkinnedInstanceDraw(fourBones, world) { Motion = moved }, fourBonePose);
        });
        Assert.True(scene.RenderOriginActive);
        Assert.NotNull(scene.PreviousFrameView);
        Assert.Equal(1, scene.CulledSkinnedInstances);
        Assert.Equal((body, moved), (scene.GpuSkinnedMotionForTests(0), scene.GpuSkinnedMotionForTests(1)));

        var range = (GpuBufferRange)((FakeResourceSet)scene.MotionResourcesForTests!.SkinnedPalette.Set).Resources[1];
        RecordingGpuCommandList.Upload upload = Assert.Single(second.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer));
        byte[] image = upload.Data!;

        (Matrix4x4 bodyWorld, Matrix4x4[] bodyBones) = Slot(image, 0, 3);
        Assert.Equal(Matrix4x4.CreateTranslation(.5f - 256f, 0f, -.25f), bodyWorld);   // last world, this origin
        Assert.Equal(Composed(lastPose, mesh), bodyBones);                               // last palette
        Assert.NotEqual(Composed(pose, mesh), bodyBones);

        (Matrix4x4 movedWorld, Matrix4x4[] movedBones) = Slot(image, 1, 4);
        Assert.Equal(Matrix4x4.CreateTranslation(.75f - 256f, 0f, -.25f), movedWorld);  // this frame's own world
        Assert.Equal(Composed(fourBonePose, fourBoneMesh), movedBones);                 // and its own palette
    }
}
