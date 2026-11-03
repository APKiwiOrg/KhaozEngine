using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliagePointIndexedFrameTests
{
    static readonly Vector3 Light = new(0f, 2f, 0f);
    static readonly FoliageRenderSettings Full = new() { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .5f };
    static readonly FoliageMotionProfile Motion = new(.3f, .15f, .4f, .035f);

    [Fact]
    public void Empty_frame_removes_prior_membership()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = Batch(scene, mesh);
        Frame(rig, batch, Full);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        Assert.Equal(0, Frame(rig, batch, Full).PointStaticRebuilds);

        // Omission leaves the batch alive but must remove its selected proxies from this frame.
        Assert.Equal(1, Frame(rig, null, Full).PointStaticRebuilds);
        AssertEmptyIndex(scene);
        Assert.Equal(0, scene.FoliagePointShadowDrawCalls);
        Assert.Equal(1, scene.FoliagePointShadowStreamCount);
        Assert.Equal(0, Frame(rig, null, Full).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));

        // Disposal after submission is rejected during preparation, including the cached stream.
        Assert.Equal(1, rig.RenderFrame(s =>
        {
            s.DrawFoliage(batch, Vector3.Zero, Full);
            batch.Dispose();
            AddLight(s);
        }).PointStaticRebuilds);
        AssertEmptyIndex(scene);
        Assert.Equal(0, scene.FoliagePointShadowStreamCount);
        Assert.Equal(0, scene.FoliagePointShadowDrawCalls);
    }

    [Fact]
    public void Changed_prefix_or_rest_pose_dirties_the_static_row()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = Batch(scene, mesh);
        var thin = Full with { QualityDensity = .5f, DistantDensity = .5f };
        Frame(rig, batch, Full);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        Assert.Equal(0, Frame(rig, batch, Full).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, batch, thin).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 1));
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Equal(0, Frame(rig, batch, thin).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, batch, Full with { QualityDensity = 0f }).PointStaticRebuilds);
        AssertEmptyIndex(scene);
        Assert.Equal(0, scene.FoliagePointShadowDrawCalls);

        using FoliageBatch moved = Batch(scene, mesh, MathF.BitIncrement(1.5f));
        Assert.Equal(1, Frame(rig, moved, Full).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        Assert.Equal(0, Frame(rig, moved, Full).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        Assert.Equal(0, Frame(rig, batch, Full).PointStaticRebuilds);
    }

    [Fact]
    public void Camera_and_motion_only_changes_preserve_rest_shadow_state()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        scene.FrustumCulling = true;
        scene.RenderOrigin = Vector3.Zero;
        scene.Camera.Frame(new Vector3(500f, 0f, 500f), new Vector3(1f));
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = Batch(scene, mesh);
        Frame(rig, batch, Full);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFoliageStats.SubmittedPatches);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Equal(0, Frame(rig, batch, Full).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);

        scene.Camera.Frame(new Vector3(-500f, 0f, -500f), new Vector3(1f));
        scene.EffectTimeSeconds = 15f;
        Assert.Equal(0, Frame(rig, batch, Full with { WindStrength = 1f }).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        using FoliageBatch motionOnly = Batch(scene, mesh, motion: new(.9f, .05f, 1f, .2f));
        Assert.Equal(0, Frame(rig, motionOnly, Full).PointStaticRebuilds);
        // A cached row needs no proxy upload. Its signature must still include these not-yet-uploaded proxies.
        Assert.Equal(scene.LastFoliageStats.InstanceUploadBytes, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Null(scene.FoliagePointBufferForTests(motionOnly));
        Assert.Equal(0, Frame(rig, motionOnly, Full).PointStaticRebuilds);

        scene.RenderOrigin = new Vector3(256f, 0f, 0f);
        Assert.Equal(0, Frame(rig, motionOnly, Full).PointStaticRebuilds);
        Assert.Equal(new Vector3(256f, 0f, 0f), scene.RenderOrigin);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Null(scene.FoliagePointBufferForTests(motionOnly));
        Assert.Equal(0, Frame(rig, motionOnly, Full).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        // Rebuild at the rebased origin to verify off-camera face runs as well as cached signatures.
        Assert.Equal(1, Frame(rig, null, Full).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, motionOnly, Full).PointStaticRebuilds);
        Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes, scene.LastFrameStats.InstanceUploadBytes);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        IGpuBuffer buffer = Assert.IsAssignableFrom<IGpuBuffer>(scene.FoliagePointBufferForTests(motionOnly));

        // A dynamic row requests a draw at the next origin. Rebase the same buffer without dirtying the static row.
        scene.RenderOrigin = new Vector3(512f, 0f, 0f);
        Assert.Equal(0, rig.RenderFrame(s =>
        {
            s.DrawFoliage(motionOnly, Vector3.Zero, Full);
            AddLight(s);
            s.AddLight(Light, Color.White, 6f, 1f, LightShadow.Dynamic);
        }).PointStaticRebuilds);
        Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Same(buffer, scene.FoliagePointBufferForTests(motionOnly));
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2));
        Assert.Equal(0, Frame(rig, motionOnly, Full).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
    }

    [Fact]
    public void Mesh_generation_reuse_cannot_retain_a_stale_source()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch old = Batch(scene, mesh);
        Frame(rig, old, Full);
        Assert.Equal(1, Frame(rig, old, Full).PointStaticRebuilds);
        scene.UnloadMesh(mesh);
        Assert.Equal(1, Frame(rig, old, Full).PointStaticRebuilds);
        AssertEmptyIndex(scene);
        Assert.Equal(0, scene.FoliagePointShadowDrawCalls);

        MeshHandle replacement = scene.LoadMesh(MeshPrimitives.Box(1f));
        Assert.Equal(mesh.Index, replacement.Index);
        Assert.NotEqual(mesh.Generation, replacement.Generation);
        Assert.Equal(0, Frame(rig, old, Full).PointStaticRebuilds);
        AssertEmptyIndex(scene);
        using FoliageBatch current = Batch(scene, replacement);
        Assert.Equal(1, Frame(rig, current, Full).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(replacement, 0, 2));
        Assert.Equal(0, Frame(rig, current, Full).PointStaticRebuilds);
    }

    [Fact]
    public void Sources_keep_separate_buffers_and_submission_order()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch first = Batch(scene, mesh);
        using FoliageBatch second = Batch(scene, mesh, 3f);
        ShadowPassDiagnostics Render(bool reverse) => rig.RenderFrame(s =>
        {
            s.DrawFoliage(reverse ? second : first, Vector3.Zero, Full);
            s.DrawFoliage(reverse ? first : second, Vector3.Zero, Full);
            // Duplicate submissions retain the existing union, not duplicate casters.
            s.DrawFoliage(first, Vector3.Zero, Full);
            AddLight(s);
        });
        Render(false);
        Assert.Equal(1, Render(false).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2), new FoliagePointShadowRun(mesh, 0, 2));
        List<IGpuBuffer> buffers = FaceBuffers(scene);
        Assert.NotSame(buffers[0], buffers[1]);
        Assert.Equal(0, Render(false).PointStaticRebuilds);
        Assert.Equal(1, Render(true).PointStaticRebuilds);
        AssertRuns(scene, new FoliagePointShadowRun(mesh, 0, 2), new FoliagePointShadowRun(mesh, 0, 2));
        List<IGpuBuffer> reversed = FaceBuffers(scene);
        Assert.Same(buffers[0], reversed[1]);
        Assert.Same(buffers[1], reversed[0]);
        Assert.Equal(0, Render(true).PointStaticRebuilds);
    }

    [Fact]
    public void Face_runs_keep_mesh_boundaries_and_gaps_in_one_source()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle a = scene.LoadMesh(MeshPrimitives.Box(1f));
        MeshHandle b = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = scene.CreateFoliageBatch(
        [
            new(a, Matrix4x4.CreateTranslation(1.5f, 0f, .1f), .1f, Motion, true),
            new(a, Matrix4x4.CreateTranslation(1.5f, 0f, .2f), .9f, Motion, true),
            new(b, Matrix4x4.CreateTranslation(2.5f, 0f, .1f), .1f, Motion, true),
            new(b, Matrix4x4.CreateTranslation(2.5f, 0f, .2f), .9f, Motion, true),
            new(a, Matrix4x4.CreateTranslation(3.5f, 0f, .1f), .1f, Motion, true),
            new(a, Matrix4x4.CreateTranslation(3.5f, 0f, .2f), .9f, Motion, true),
        ], patchSize: 1f);
        Frame(rig, batch, Full);
        Assert.Equal(1, Frame(rig, batch, Full).PointStaticRebuilds);
        AssertRuns(scene, new(a, 0, 2), new(b, 2, 2), new(a, 4, 2));
        Assert.Equal(0, Frame(rig, batch, Full).PointStaticRebuilds);
        Assert.Equal(1, Frame(rig, batch, Full with { QualityDensity = .5f, DistantDensity = .5f }).PointStaticRebuilds);
        AssertRuns(scene, new(a, 0, 1), new(b, 2, 1), new(a, 4, 1));
    }

    static FoliageBatch Batch(Scene3D scene, MeshHandle mesh, float x = 1.5f, FoliageMotionProfile? motion = null) =>
        scene.CreateFoliageBatch(
        [
            new(mesh, Matrix4x4.CreateTranslation(x, 0f, 0f), .1f, motion ?? Motion, true),
            new(mesh, Matrix4x4.CreateTranslation(x, 0f, 1f), .9f, motion ?? Motion, true),
        ]);

    static void AddLight(Scene3D scene) => scene.AddLight(Light, Color.White, 6f, 1f, LightShadow.Static(1));

    static ShadowPassDiagnostics Frame(PointShadowRig rig, FoliageBatch? batch, FoliageRenderSettings settings) =>
        rig.RenderFrame(s =>
        {
            if (batch is not null) s.DrawFoliage(batch, Vector3.Zero, settings);
            AddLight(s);
        });

    static void AssertEmptyIndex(Scene3D scene)
    {
        var result = scene.QueryFoliagePointIndexForTests(Light, 6f);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.TouchTests);
    }

    static void AssertRuns(Scene3D scene, params FoliagePointShadowRun[] expected)
    {
        Assert.Equal(expected, scene.FoliagePointFaceRunsForTests.Select(entry => entry.Run));
        Assert.Equal(expected.Length * PointShadowMath.FaceCount, scene.FoliagePointShadowDrawCalls);
    }

    static List<IGpuBuffer> FaceBuffers(Scene3D scene) =>
        scene.FoliagePointFaceRunsForTests.Select(entry => entry.Buffer).ToList();
}
