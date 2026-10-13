using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageShadowSelectionTests
{
    [Fact]
    public void FoliagePresenceAndRemovalForceAFramePass()
    {
        Assert.True(FoliageShadowSelection.NeedsDepthPass(true, false));
        Assert.True(FoliageShadowSelection.NeedsDepthPass(true, true));
        Assert.True(FoliageShadowSelection.NeedsDepthPass(false, true));
        Assert.False(FoliageShadowSelection.NeedsDepthPass(false, false));
    }

    [Fact]
    public void CascadeSelectionUsesAbsoluteBoundsAndPancakesTheNearPlane()
    {
        FrustumPlanes cascade = FrustumPlanes.Extract(Matrix4x4.Identity);
        Assert.True(FoliageShadowSelection.IntersectsCascade(
            new MeshBounds(new Vector3(-.2f, -.2f, .2f), new Vector3(.2f, .2f, .8f)), cascade));
        Assert.True(FoliageShadowSelection.IntersectsCascade(
            new MeshBounds(new Vector3(-.2f, -.2f, -3f), new Vector3(.2f, .2f, -2f)), cascade));
        Assert.False(FoliageShadowSelection.IntersectsCascade(
            new MeshBounds(new Vector3(3f, 0f, 0f), new Vector3(4f, 1f, 1f)), cascade));
        Assert.False(FoliageShadowSelection.IntersectsCascade(
            new MeshBounds(new Vector3(0f, 0f, 2f), new Vector3(1f, 1f, 3f)), cascade));
        var shifted = new MeshBounds(new Vector3(4095.8f, -.2f, .2f), new Vector3(4096.2f, .2f, .8f));
        Assert.False(FoliageShadowSelection.IntersectsCascade(shifted, cascade));
        FrustumPlanes absolute = FrustumPlanes.Extract(Matrix4x4.CreateTranslation(-4096f, 0f, 0f));
        Assert.True(FoliageShadowSelection.IntersectsCascade(shifted, absolute));
    }

    [Fact]
    public void DepthProgramsUseTheSharedDisplacementAndCameraFade()
    {
        Assert.Contains(ShaderSources.FoliageUniformBlockGlsl, ShaderSources.FoliageDepthVert);
        Assert.Contains(ShaderSources.FoliageDisplacementGlsl, ShaderSources.FoliageDepthVert);
        Assert.Contains("WindFade.y, CameraViewProj, false", ShaderSources.FoliageDepthVert);
        ShaderValidation.ValidatePair(ShaderSources.FoliageDepthVert, ShaderSources.ShadowDepthFrag, "FoliageDepth");
        ShaderValidation.ValidatePair(ShaderSources.FoliageDepthVert, ShaderSources.FoliageDepthCutoutFrag, "FoliageDepthCutout");
    }

    [Fact]
    public void FourInteractorsStayInsideTheApprovedBounds()
    {
        var motion = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        Matrix4x4 transform = Matrix4x4.CreateScale(2f, 3f, 2f) * Matrix4x4.CreateRotationZ(.5f);
        var local = new MeshBounds(new Vector3(-.5f, 0f, -.5f), new Vector3(.5f, 2f, .5f));
        FoliagePatch patch = Assert.Single(FoliagePatchLayout.Build(
            [new FoliageInstance(new MeshHandle(4, 1), transform, .1f, motion, true)], _ => local).Patches);
        var instance = new ModelRenderer.FoliageInstanceData
        {
            Model = transform, Motion = motion.ToVector4(), Parameters = new Vector4(.1f, 0f, .5f, 0f),
        };
        FoliageInteractor[] actors =
        [
            new(new Vector3(-.1f, 0f, 0f), 2f, 1f, 1f),
            new(new Vector3(-.2f, 0f, 0f), 2f, 1f, 1f),
            new(new Vector3(-.3f, 0f, 0f), 2f, 1f, 1f),
            new(new Vector3(-.4f, 0f, 0f), 2f, 1f, 1f),
        ];
        var uniforms = ModelRenderer.FoliageUniforms.Build(Vector3.Zero,
            new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f, WindStrength = .5f }, actors, 1f);
        MeshBounds padded = patch.AnimatedBounds(.5f);
        for (int corner = 0; corner < 8; corner++)
        {
            var position = new Vector3((corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z);
            Vector3 world = FoliageWindMirror.VertexWorld(uniforms, instance, position,
                Matrix4x4.Identity, Vector3.Zero, false, out bool rejected);
            Assert.False(rejected);
            Assert.InRange(world.X, padded.Min.X, padded.Max.X);
            Assert.InRange(world.Y, padded.Min.Y, padded.Max.Y);
            Assert.InRange(world.Z, padded.Min.Z, padded.Max.Z);
            Assert.True(Vector3.Distance(world, padded.Center) <= padded.Radius);
        }
    }

    [Fact]
    public void DisposedAndStaleGenerationsAreExcludedFromColourAndShadows()
    {
        using var device = new FakeGpuDevice();
        using IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer framebuffer = device.Factory.CreateFramebuffer(null, target);
        using var scene = new Scene3D(device, framebuffer.Outputs,
            new ShadowSettings { ShadowMapResolution = 256, ShadowCascadeCount = 1 });
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        scene.Post.Starfield = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Tile(.8f, 2f));
        using FoliageBatch stale = scene.CreateFoliageBatch([new(mesh, Matrix4x4.Identity, .1f, CastsShadows: true)]);
        using FoliageBatch disposed = scene.CreateFoliageBatch([new(mesh, Matrix4x4.Identity, .1f, CastsShadows: true)]);
        scene.Begin();
        var settings = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f };
        scene.DrawFoliage(stale, Vector3.Zero, settings);
        scene.DrawFoliage(disposed, Vector3.Zero, settings);
        disposed.Dispose();
        scene.UnloadMesh(mesh);
        MeshHandle replacement = scene.LoadMesh(MeshPrimitives.Tile(.8f, 2f));
        Assert.Equal(mesh.Index, replacement.Index);
        Assert.NotEqual(mesh.Generation, replacement.Generation);
        scene.PrepareFrame();
        scene.RenderInternal(commands, 64, 64, framebuffer);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.UniformUploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.SubmittedPatches);
        Assert.Equal(0, scene.FoliageShadowDrawCalls);
    }

    [Fact]
    public void GlobalCastingCoverWithoutAModelPolicyProducesCastingPatches()
    {
        var mesh = new MeshHandle(4, 1);
        var cover = new GroundCoverBatch(
            [new GroundCoverInstance("shrub", Vector3.Zero, Matrix4x4.Identity, .1f)]);
        var bindings = new GroundCoverGpuBindings(cover);
        bindings.Refresh(new Dictionary<string, IReadOnlyList<MeshHandle>> { ["shrub"] = [mesh] },
            new GroundCoverRenderOptions { CastsShadows = true });
        FoliagePatchLayout layout = FoliagePatchLayout.Build(bindings.Expand(),
            _ => new MeshBounds(Vector3.Zero, Vector3.One));
        Assert.True(Assert.Single(layout.Patches).CastsShadows);
    }
}
