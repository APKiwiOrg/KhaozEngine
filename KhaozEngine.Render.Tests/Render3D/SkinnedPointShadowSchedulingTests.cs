using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowSchedulingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PoseOnlyStaticFramesRedrawTransientWithoutDirtyingRigidBase(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.WarmStaticBaseAndTransient();
        ShadowPassDiagnostics first = rig.RenderPose(0.15f);
        ShadowPassDiagnostics second = rig.RenderPose(0.65f);

        Assert.Equal(0, first.PointStaticRebuilds);
        Assert.Equal(0, second.PointStaticRebuilds);
        Assert.Equal(1, first.PointTransientRowsRendered);
        Assert.Equal(1, second.PointTransientRowsRendered);
        Assert.True(second.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.Equal(0, second.PointDynamicSkinnedDrawCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DynamicSkinnedCasterUsesBaseRowAndNeverTransientRow(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.RenderPose(0f, dynamicLight: true);
        ShadowPassDiagnostics diagnostics = rig.RenderPose(0.3f, dynamicLight: true);

        Assert.Equal(1, diagnostics.PointDynamicRenders);
        Assert.True(diagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.Equal(0, diagnostics.PointTransientRowsRendered);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeverRenderedStaticBaseRowPublishesNeitherBaseNorTransient(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;
        rig.RenderPose(0f);
        ShadowPassDiagnostics diagnostics = rig.RenderPose(0.4f);

        Assert.Equal(0, rig.Scene.PointShadowedLights);
        Assert.Equal(0, diagnostics.PointTransientDemand);
        Assert.Equal(0, diagnostics.PointTransientRowsRendered);
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirtyPreviouslyRenderedStaticRowDeferredByRigidBudgetStillRendersCurrentTransient(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.WarmStaticBaseAndTransient();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 0;

        ShadowPassDiagnostics diagnostics = rig.RenderPose(0.45f,
            rigidWorld: Matrix4x4.CreateTranslation(0.3f, 0f, 0f));

        Assert.Equal(0, diagnostics.PointStaticRebuilds);
        Assert.Equal(1, diagnostics.PointTransientRowsRendered);
        Assert.Equal(1, rig.Scene.PointShadowedLights);
        Assert.True(diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DynamicPastBudgetDoesNotRetainPointOnlyCaster(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.Settings.PointShadows.MaxDynamicLightsPerFrame = 1;
        rig.RenderOffCameraDynamicPastBudget();

        ShadowPassDiagnostics diagnostics = rig.RenderOffCameraDynamicPastBudget();

        Assert.Equal(0, diagnostics.PointDynamicSkinnedDrawCalls);
        Assert.Equal(0L, rig.Scene.LastFrameStats.SkinnedUploadBytes);
        Assert.Equal(0L, rig.Scene.LastFrameStats.SkinnedUniformUploadBytes);
        Assert.Equal(1, rig.Scene.CulledSkinnedInstances);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CastsShadowsFalseDoesNotRetainAnOffCameraPointOnlyDraw(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.RenderOffCameraOptedOut();

        ShadowPassDiagnostics diagnostics = rig.RenderOffCameraOptedOut();

        Assert.Equal(0, diagnostics.PointDynamicSkinnedDrawCalls);
        Assert.Equal(0L, rig.Scene.LastFrameStats.SkinnedUploadBytes);
        Assert.Equal(0L, rig.Scene.LastFrameStats.SkinnedUniformUploadBytes);
        Assert.Equal(1, rig.Scene.CulledSkinnedInstances);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstStaticSkinnedDemandWaitsForTransientAllocationAtBegin(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.RenderPose(0f);
        ShadowPassDiagnostics pending = rig.RenderPose(0.1f);

        Assert.Equal(1, pending.PointTransientDemand);
        Assert.Equal(0, pending.PointTransientRowsRendered);
        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TransientAtlasRows);

        ShadowPassDiagnostics active = rig.RenderPose(0.2f);
        Assert.Equal(1, active.PointTransientRowsRendered);
        Assert.Equal(1, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialNearRadiusOverlapKeepsTheSkinnedCaster(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        float sphereRadius = rig.CasterSphere.Radius;
        rig.RenderWithPolicy(LightShadow.DynamicWithNearRadius(sphereRadius * 2f));
        ShadowPassDiagnostics whollyInside = rig.RenderWithPolicy(
            LightShadow.DynamicWithNearRadius(sphereRadius * 2f));
        ShadowPassDiagnostics partial = rig.RenderWithPolicy(
            LightShadow.DynamicWithNearRadius(sphereRadius * 0.5f));

        Assert.Equal(0, whollyInside.PointDynamicSkinnedDrawCalls);
        Assert.True(partial.PointDynamicSkinnedDrawCalls > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialExclusionBoxOverlapKeepsTheSkinnedCaster(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        PointShadowCasterSphere sphere = rig.CasterSphere;
        Vector3 wide = new(sphere.Radius * 2f);
        Vector3 narrow = new(sphere.Radius * 0.5f);
        LightShadow full = LightShadow.Dynamic.WithExclusionBox(
            sphere.Center - wide, sphere.Center + wide);
        LightShadow partial = LightShadow.Dynamic.WithExclusionBox(
            sphere.Center - narrow, sphere.Center + narrow);
        rig.RenderWithPolicy(full);
        ShadowPassDiagnostics whollyInside = rig.RenderWithPolicy(full);
        ShadowPassDiagnostics partialOverlap = rig.RenderWithPolicy(partial);

        Assert.Equal(0, whollyInside.PointDynamicSkinnedDrawCalls);
        Assert.True(partialOverlap.PointDynamicSkinnedDrawCalls > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeOriginKeepsAbsoluteSelectionAndPacksRenderRelativeFaceData(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        Vector3 origin = new(100_000f, 2f, -100_000f);
        rig.RenderAtLargeOrigin(origin);

        (ShadowPassDiagnostics diagnostics, byte[]? faceBytes) = rig.RenderAtLargeOrigin(origin);

        Assert.True(rig.Scene.RenderOriginActive);
        Assert.True(diagnostics.PointDynamicSkinnedDrawCalls > 0);
        byte[] bytes = Assert.IsType<byte[]>(faceBytes);
        Assert.Equal(0f, BitConverter.ToSingle(bytes, 64));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 68));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 72));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecondStaticBaseRowMapsToDenseTransientRowZero(bool gpuSkinning)
    {
        using var rig = new SchedulingRig(gpuSkinning);
        rig.RenderTwoStaticLights();
        rig.RenderTwoStaticLights();
        ShadowPassDiagnostics diagnostics = rig.RenderTwoStaticLights();

        Assert.Equal(0, rig.Scene.PointShadowBaseSlotForLight(0));
        Assert.Equal(1, rig.Scene.PointShadowBaseSlotForLight(1));
        Assert.Equal(-1, rig.Scene.PointShadowTransientSlotForLight(0));
        Assert.Equal(0, rig.Scene.PointShadowTransientSlotForLight(1));
        Assert.Equal(1, diagnostics.PointTransientRowsRendered);
    }

    sealed class SchedulingRig : IDisposable
    {
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        readonly SkinnedGltfMesh _tube;
        readonly SkinnedMeshHandle _tubeHandle;
        readonly MeshHandle _box;

        internal SchedulingRig(bool gpuSkinning)
        {
            Device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)Device.Factory;
            _targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = factory.CreateFramebuffer(null, _targetTexture);
            Settings = new ShadowSettings { Mode = ShadowMode.Off };
            Scene = new Scene3D(Device, _target.Outputs, Settings);
            Scene.UseGpuSkinning = gpuSkinning;
            _tube = SkinnedMeshBuilder.BuildTube(0.35f, 2f, 4, 6, 3, Axis.Z);
            _tubeHandle = Scene.LoadSkinnedMesh(_tube);
            _box = Scene.LoadMesh(MeshPrimitives.Box(0.5f));
        }

        internal FakeGpuDevice Device { get; }
        internal ShadowSettings Settings { get; }
        internal Scene3D Scene { get; }
        internal PointShadowCasterSphere CasterSphere => PointShadowCasterSphere.FromRestBounds(
            MeshBounds.FromVertices(_tube.Vertices), Matrix4x4.Identity, Scene3D.SkinnedCullSafetyFactor);

        internal void WarmStaticBaseAndTransient()
        {
            RenderPose(0f);
            RenderPose(0f);
            RenderPose(0f);
        }

        internal ShadowPassDiagnostics RenderPose(float angle, bool dynamicLight = false,
            Matrix4x4? rigidWorld = null)
        {
            Scene.Begin();
            Matrix4x4[] pose = (Matrix4x4[])_tube.RestPose.Clone();
            pose[1] = Matrix4x4.CreateRotationY(angle) * pose[1];
            Scene.Draw(_box, rigidWorld ?? Matrix4x4.Identity);
            Scene.DrawSkinned(_tubeHandle, pose, Matrix4x4.Identity, Color.White);
            Scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f,
                dynamicLight ? LightShadow.Dynamic : LightShadow.Static(7));
            Scene.PrepareFrame();
            using IGpuCommandList commands = Device.Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        internal ShadowPassDiagnostics RenderOffCameraDynamicPastBudget()
        {
            Scene.Begin();
            Vector3 eye = Scene.Camera.Eye;
            Vector3 far = eye + new Vector3(100f, 0f, 0f);
            Scene.DrawSkinned(_tubeHandle, _tube.RestPose, Matrix4x4.CreateTranslation(far), Color.White);
            Scene.AddLight(eye + new Vector3(0f, 0f, 2f), Color.White, 5f, 1f, LightShadow.Dynamic);
            Scene.AddLight(far, Color.White, 5f, 1f, LightShadow.Dynamic);
            Scene.PrepareFrame();
            using IGpuCommandList commands = Device.Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        internal ShadowPassDiagnostics RenderOffCameraOptedOut()
        {
            Scene.Begin();
            Vector3 far = Scene.Camera.Eye + new Vector3(100f, 0f, 0f);
            Scene.DrawSkinned(_tubeHandle, _tube.RestPose, Matrix4x4.CreateTranslation(far),
                Color.White, Material.None, castsShadows: false);
            Scene.AddLight(far, Color.White, 5f, 1f, LightShadow.Dynamic);
            Scene.PrepareFrame();
            using IGpuCommandList commands = Device.Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        internal ShadowPassDiagnostics RenderWithPolicy(LightShadow shadow)
        {
            Scene.Begin();
            Scene.DrawSkinned(_tubeHandle, _tube.RestPose, Matrix4x4.Identity, Color.White);
            Scene.AddLight(CasterSphere.Center, Color.White, 10f, 1f, shadow);
            Scene.PrepareFrame();
            using IGpuCommandList commands = Device.Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        internal (ShadowPassDiagnostics Diagnostics, byte[]? FaceBytes) RenderAtLargeOrigin(Vector3 origin)
        {
            Scene.RenderOrigin = origin;
            Scene.FrustumCulling = false;
            Scene.Begin();
            Scene.DrawSkinned(_tubeHandle, _tube.RestPose, Matrix4x4.CreateTranslation(origin), Color.White);
            Scene.AddLight(origin + new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
            Scene.PrepareFrame();
            using IGpuCommandList inner = Device.Factory.CreateCommandList();
            using var commands = new RecordingGpuCommandList(inner) { CapturePayloads = true };
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            byte[]? faceBytes = commands.Uploads
                .FirstOrDefault(upload => upload.Buffer.SizeInBytes == PointShadowRenderer.FaceSlotBytes * 6)
                .Data;
            return (Scene.LastShadowPassDiagnostics, faceBytes);
        }

        internal ShadowPassDiagnostics RenderTwoStaticLights()
        {
            Scene.Begin();
            Scene.DrawSkinned(_tubeHandle, _tube.RestPose, Matrix4x4.Identity, Color.White);
            Scene.AddLight(new Vector3(-20f, 1f, 1f), Color.White, 5f, 1f, LightShadow.Static(1));
            Scene.AddLight(new Vector3(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Static(2));
            Scene.PrepareFrame();
            using IGpuCommandList commands = Device.Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
            return Scene.LastShadowPassDiagnostics;
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            Device.Dispose();
        }
    }
}
