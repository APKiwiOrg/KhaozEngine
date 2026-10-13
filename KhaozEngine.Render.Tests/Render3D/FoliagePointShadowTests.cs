using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Casting animated foliage reaches point-light shadows as rest-pose rigid proxies (ruling S5). Headless over the
/// fake device, so proxy selection, the static signature and the stream lifetime are pinned on every CI leg.
/// </summary>
public sealed class FoliagePointShadowTests
{
    static readonly FoliageMotionProfile Shrub = new(.3f, .15f, .4f, .035f);
    static readonly MeshBounds UnitBox = new(new Vector3(-.5f, 0f, -.5f), new Vector3(.5f, 1f, .5f));
    static readonly Vector3 Light = new(0f, 2f, 0f);
    const float Radius = 6f;

    [Fact]
    public void PointProxyUsesRestTransformsAndIgnoresClockAgitationAndProfile()
    {
        var shrubMesh = new MeshHandle(3, 2);
        var bladeMesh = new MeshHandle(4, 1);
        Matrix4x4 authored = Matrix4x4.CreateScale(1.5f, 2f, 1.5f) * Matrix4x4.CreateRotationY(.4f)
            * Matrix4x4.CreateTranslation(1.5f, 0f, .25f);
        FoliageInstance[] items =
        [
            new(bladeMesh, Matrix4x4.CreateTranslation(-1f, 0f, 0f), .2f),
            new(shrubMesh, authored, .1f, Shrub, true),
        ];
        using FoliagePointShadowStream stream = FoliagePointShadowStream.Build(items, _ => UnitBox);

        Assert.Equal(1, stream.Casters.Length);
        FoliagePointShadowCaster caster = stream.Casters[0];
        Assert.Equal(authored, caster.Transform);
        Assert.Equal(1, caster.SourceIndex);
        Assert.Equal(0u, caster.Slot);
        Assert.Equal(shrubMesh.Index, caster.Mesh.Index);
        Assert.Equal(shrubMesh.Generation, caster.Mesh.Generation);
        PointShadowCasterSphere sphere = PointShadowCasterSphere.FromRestBounds(UnitBox, authored, 1f);
        Assert.Equal(new Vector4(sphere.Center, sphere.Radius), caster.Sphere);
        Assert.Null(stream.Buffer);

        var runs = new List<FoliagePointShadowRun>();
        FoliagePointShadowRange[] candidates = [new FoliagePointShadowRange(0, 2)];
        stream.Query(candidates, Light, Radius, 0f, default, default, runs);
        FoliagePointShadowRun run = Assert.Single(runs);
        Assert.Equal(shrubMesh.Index, run.Mesh.Index);
        Assert.Equal(0u, run.Start);
        Assert.Equal(1u, run.Count);

        // The blade's range alone holds no proxy.
        stream.Query([new FoliagePointShadowRange(0, 1)], Light, Radius, 0f, default, default, runs);
        Assert.Empty(runs);

        Vector3 boxMin = sphere.Center - new Vector3(sphere.Radius + .01f);
        Vector3 boxMax = sphere.Center + new Vector3(sphere.Radius + .01f);
        stream.Query(candidates, Light, Radius, 0f, boxMin, boxMax, runs);
        Assert.Empty(runs);
        stream.Query(candidates, sphere.Center, Radius, sphere.Radius + .01f, default, default, runs);
        Assert.Empty(runs);

        // Every shell shape answers exactly what the shared skinned-caster predicate answers.
        (Vector3 Position, float Radius, float Near, Vector3 Min, Vector3 Max)[] lights =
        [
            (Light, Radius, 0f, default, default),
            (new Vector3(20f, 0f, 0f), 3f, 0f, default, default),
            (new Vector3(20f, 0f, 0f), 30f, 0f, default, default),
            (sphere.Center, 4f, sphere.Radius * .5f, default, default),
            (sphere.Center, 4f, sphere.Radius + .01f, default, default),
            (Light, Radius, 0f, boxMin, boxMax),
            (Light, Radius, 0f, boxMin, sphere.Center),
        ];
        foreach (var light in lights)
        {
            stream.Query(candidates, light.Position, light.Radius, light.Near, light.Min, light.Max, runs);
            bool expected = sphere.TouchesShadowingShell(light.Position, light.Radius, light.Near,
                light.Min, light.Max);
            Assert.Equal(expected ? 1 : 0, runs.Count);
        }
    }

    [Fact]
    public void QueryCoalescesConsecutiveSlotsOfOneMeshAndHonoursEachRange()
    {
        var a = new MeshHandle(1, 1);
        var b = new MeshHandle(2, 1);
        FoliageInstance[] items =
        [
            new(a, Matrix4x4.CreateTranslation(0f, 0f, 0f), .1f, CastsShadows: true),
            new(a, Matrix4x4.CreateTranslation(1f, 0f, 0f), .2f, CastsShadows: true),
            new(a, Matrix4x4.CreateTranslation(2f, 0f, 0f), .3f),
            new(a, Matrix4x4.CreateTranslation(3f, 0f, 0f), .4f, CastsShadows: true),
            new(b, Matrix4x4.CreateTranslation(4f, 0f, 0f), .1f, CastsShadows: true),
            new(b, Matrix4x4.CreateTranslation(5f, 0f, 0f), .2f, CastsShadows: true),
        ];
        using FoliagePointShadowStream stream = FoliagePointShadowStream.Build(items, _ => UnitBox);
        Assert.Equal(5, stream.Casters.Length);

        var runs = new List<FoliagePointShadowRun>();
        stream.Query([new FoliagePointShadowRange(0, 6)], new Vector3(2.5f, 1f, 0f), 20f, 0f, default, default, runs);
        // Slots 0..2 are one mesh and contiguous because the non-casting item takes no slot.
        Assert.Equal(2, runs.Count);
        Assert.Equal((a.Index, 0u, 3u), (runs[0].Mesh.Index, runs[0].Start, runs[0].Count));
        Assert.Equal((b.Index, 3u, 2u), (runs[1].Mesh.Index, runs[1].Start, runs[1].Count));

        // A prefix range keeps only the instances it names, by original layout index.
        stream.Query([new FoliagePointShadowRange(0, 2), new FoliagePointShadowRange(4, 1)],
            new Vector3(2.5f, 1f, 0f), 20f, 0f, default, default, runs);
        Assert.Equal(2, runs.Count);
        Assert.Equal((a.Index, 0u, 2u), (runs[0].Mesh.Index, runs[0].Start, runs[0].Count));
        Assert.Equal((b.Index, 3u, 1u), (runs[1].Mesh.Index, runs[1].Start, runs[1].Count));
    }

    [Fact]
    public void StreamUploadsOnceUntilTheOriginMovesAndRetiresItsBuffer()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using GpuRetireQueue retired = GpuRetireQueue.Create(device);
        using IGpuCommandList commands = factory.CreateCommandList();
        FoliageInstance[] items =
        [
            new(new MeshHandle(1, 1), Matrix4x4.CreateTranslation(300f, 0f, 0f), .1f, CastsShadows: true),
            new(new MeshHandle(1, 1), Matrix4x4.CreateTranslation(301f, 0f, 0f), .2f, CastsShadows: true),
        ];
        FoliagePointShadowStream stream = FoliagePointShadowStream.Build(items, _ => UnitBox);
        Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes,
            stream.EnsureUploaded(device, commands, Vector3.Zero, retired));
        IGpuBuffer buffer = Assert.IsAssignableFrom<IGpuBuffer>(stream.Buffer);
        Assert.Equal(0, stream.EnsureUploaded(device, commands, Vector3.Zero, retired));
        Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes,
            stream.EnsureUploaded(device, commands, new Vector3(256f, 0f, 0f), retired));
        Assert.Same(buffer, stream.Buffer);
        // The CPU proxies stay absolute, only the uploaded copy is rebased.
        Assert.Equal(300f, stream.Casters[0].Transform.M41);

        Assert.Equal(0, retired.PendingCount);
        stream.Dispose();
        Assert.Equal(1, retired.PendingCount);
        Assert.Null(stream.Buffer);
        stream.Dispose();
        Assert.Equal(1, retired.PendingCount);
    }

    [Fact]
    public void StaticSignatureFollowsRestProxiesOnly()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        var settings = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .5f };
        FoliageInteractor[] still = [new(new Vector3(1f, 0f, 0f), 2f, 1f, 0f)];
        FoliageInteractor[] rustling = [new(new Vector3(1f, 0f, 0f), 2f, 1f, 1f)];
        Matrix4x4 rest = Matrix4x4.CreateTranslation(1.5f, 0f, 0f);
        FoliageBatch batch = scene.CreateFoliageBatch([new(box, rest, .1f, Shrub, true)]);

        ShadowPassDiagnostics Frame(FoliageBatch? foliage, FoliageRenderSettings s,
            FoliageInteractor[] actors, float time = 1f)
        {
            scene.EffectTimeSeconds = time;
            return rig.RenderFrame(sc =>
            {
                if (foliage is not null) sc.DrawFoliage(foliage, Vector3.Zero, s, actors);
                sc.AddLight(Light, Color.White, Radius, 1f, LightShadow.Static(1));
            });
        }

        Frame(batch, settings, still);
        ShadowPassDiagnostics first = Frame(batch, settings, still);
        Assert.Equal(1, first.PointStaticRebuilds);
        Assert.True(first.PointFaceDrawCalls > 0);
        Assert.Equal(first.PointFaceDrawCalls, scene.FoliagePointShadowDrawCalls);
        Assert.Equal(ModelRenderer.InstanceData.SizeInBytes, scene.LastFrameStats.InstanceUploadBytes);

        // Clock, agitation, wind and the motion profile never reach the static map.
        Assert.Equal(0, Frame(batch, settings, rustling, 7.5f).PointStaticRebuilds);
        Assert.Equal(0, Frame(batch, settings with { WindStrength = 1f }, rustling, 9f).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        batch.Dispose();
        batch = scene.CreateFoliageBatch([new(box, rest, .1f, new FoliageMotionProfile(.9f, .05f, 1f, .2f), true)]);
        Assert.Equal(0, Frame(batch, settings, rustling).PointStaticRebuilds);

        // Moving the rest transform rebuilds once.
        batch.Dispose();
        Matrix4x4 moved = Matrix4x4.CreateTranslation(MathF.BitIncrement(1.5f), 0f, 0f);
        batch = scene.CreateFoliageBatch([new(box, moved, .1f, Shrub, true)]);
        Assert.Equal(1, Frame(batch, settings, still).PointStaticRebuilds);
        Assert.Equal(0, Frame(batch, settings, still).PointStaticRebuilds);

        // Dropping casting removes the proxy, restoring it brings the map back.
        batch.Dispose();
        batch = scene.CreateFoliageBatch([new(box, moved, .1f, Shrub)]);
        ShadowPassDiagnostics silent = Frame(batch, settings, still);
        Assert.Equal(1, silent.PointStaticRebuilds);
        Assert.Equal(0, silent.PointFaceDrawCalls);
        batch.Dispose();
        batch = scene.CreateFoliageBatch([new(box, moved, .1f, Shrub, true)]);
        Assert.Equal(1, Frame(batch, settings, still).PointStaticRebuilds);

        // A new mesh generation at the same slot is a different caster.
        batch.Dispose();
        scene.UnloadMesh(box);
        MeshHandle reloaded = scene.LoadMesh(MeshPrimitives.Box(1f));
        Assert.Equal(box.Index, reloaded.Index);
        Assert.NotEqual(box.Generation, reloaded.Generation);
        batch = scene.CreateFoliageBatch([new(reloaded, moved, .1f, Shrub, true)]);
        Assert.Equal(1, Frame(batch, settings, still).PointStaticRebuilds);
        Assert.Equal(0, Frame(batch, settings, still).PointStaticRebuilds);

        // The batch disappearing clears its proxies from the map and retires its stream.
        Assert.Equal(1, scene.FoliagePointShadowStreamCount);
        batch.Dispose();
        ShadowPassDiagnostics removed = Frame(null, settings, still);
        Assert.Equal(1, removed.PointStaticRebuilds);
        Assert.Equal(0, removed.PointFaceDrawCalls);
        Assert.Equal(0, scene.FoliagePointShadowStreamCount);
        Assert.Equal(0, Frame(null, settings, still).PointStaticRebuilds);
    }

    [Fact]
    public void RetainedGlobalCastingCoverCastsPointShadowsAtRest()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        var cover = new GroundCoverBatch([new GroundCoverInstance("shrub", Vector3.Zero, Matrix4x4.Identity, .1f)]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["shrub"] = [box] };
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            CastsShadows = true,
            DistantDensity = 1f,
            DrawRadius = 100f,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["shrub"] = Shrub },
        };
        ShadowPassDiagnostics Frame(float time)
        {
            scene.EffectTimeSeconds = time;
            return rig.RenderFrame(s =>
            {
                s.DrawGroundCover(cover, meshes, Vector3.Zero, options);
                s.AddLight(Light, Color.White, Radius, 1f, LightShadow.Static(1));
            });
        }
        try
        {
            Frame(1f);
            ShadowPassDiagnostics drawn = Frame(1f);
            Assert.Equal(1, drawn.PointStaticRebuilds);
            // Every point draw is a rest proxy, so the retained cover no longer rides the rigid CPU path.
            Assert.True(drawn.PointFaceDrawCalls > 0);
            Assert.Equal(drawn.PointFaceDrawCalls, scene.FoliagePointShadowDrawCalls);
            Assert.Equal(0, Frame(4f).PointStaticRebuilds);
        }
        finally { scene.ReleaseGroundCover(cover); }
    }

    [Fact]
    public void SubmittedPrefixesSelectProxiesIndependentlyOfTheCamera()
    {
        using var rig = new PointShadowRig();
        Scene3D scene = rig.Scene;
        scene.FrustumCulling = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        // Two ranks in one patch. The density prefix keeps only the first, and the camera looks away from both.
        using FoliageBatch batch = scene.CreateFoliageBatch(
        [
            new(box, Matrix4x4.CreateTranslation(1.5f, 0f, 0f), .1f, Shrub, true),
            new(box, Matrix4x4.CreateTranslation(1.5f, 0f, 1f), .9f, Shrub, true),
        ]);
        var thin = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = .5f, QualityDensity = .5f };
        var full = thin with { DistantDensity = 1f, QualityDensity = 1f };
        ShadowPassDiagnostics Frame(FoliageRenderSettings s) => rig.RenderFrame(sc =>
        {
            sc.Camera.Frame(new Vector3(500f, 0f, 500f), new Vector3(1f));
            sc.DrawFoliage(batch, Vector3.Zero, s);
            sc.AddLight(Light, Color.White, Radius, 1f, LightShadow.Static(1));
        });
        Frame(thin);
        Assert.Equal(1, Frame(thin).PointStaticRebuilds);
        Assert.Equal(0, scene.LastFoliageStats.SubmittedPatches);
        Assert.True(scene.FoliagePointShadowDrawCalls > 0);
        Assert.Equal(0, Frame(thin).PointStaticRebuilds);
        Assert.Equal(1, Frame(full).PointStaticRebuilds);
        Assert.Equal(0, Frame(full).PointStaticRebuilds);
    }
}
