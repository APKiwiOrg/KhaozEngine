using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Headless coverage for static point-shadow capacity that is owned by stable keys rather than camera
/// proximity. The fake device exposes atlas shape and pass diagnostics without requiring pixel readback.</summary>
public sealed class PointShadowResidencyTests
{
    [Fact]
    public void EveryKeyedStaticLightKeepsItsRowAcrossCameraAndQueueReordering()
    {
        using var rig = new ResidencyRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 32;

        void Queue(Scene3D scene, bool reverse)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            for (int n = 0; n < 20; n++)
            {
                int light = reverse ? 19 - n : n;
                scene.AddLight(new Vector3(light - 10f, 2f, 0f), Color.White, 4f, 1f,
                    LightShadow.Static(1_000 + light));
            }
        }

        rig.Scene.Camera.Target = new Vector3(-100f, 0f, 0f);
        rig.Scene.Camera.Azimuth = MathF.PI / 2f;
        rig.RenderFrame(s => Queue(s, reverse: false));
        rig.RenderFrame(s => Queue(s, reverse: false));

        Assert.Equal(24, rig.Scene.PointShadowRows);
        Assert.Equal(20, rig.Scene.PointShadowedLights);
        Assert.Equal(20, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        IGpuTexture? atlas = rig.Scene.PointShadowTexture;

        rig.Scene.Camera.Target = new Vector3(100f, 0f, 0f);
        rig.RenderFrame(s => Queue(s, reverse: true));

        Assert.Same(atlas, rig.Scene.PointShadowTexture);
        Assert.Equal(24, rig.Scene.PointShadowRows);
        Assert.Equal(20, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
    }

    [Fact]
    public void DynamicEffectsAppearAndDisappearInsideAStableReservedLayout()
    {
        using var rig = new ResidencyRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 16;

        void Queue(Scene3D scene, bool dynamic)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            for (int light = 0; light < 6; light++)
                scene.AddLight(new Vector3(light, 2f, 0f), Color.White, 4f, 1f, LightShadow.Static(light + 1));
            if (dynamic)
                scene.AddLight(new Vector3(0f, 3f, 0f), Color.White, 4f, 1f, LightShadow.Dynamic);
        }

        rig.RenderFrame(s => Queue(s, dynamic: false));
        rig.RenderFrame(s => Queue(s, dynamic: false));
        Assert.Equal(10, rig.Scene.PointShadowRows);
        Assert.Equal(6, rig.Scene.PointShadowedLights);
        IGpuTexture? atlas = rig.Scene.PointShadowTexture;

        rig.RenderFrame(s => Queue(s, dynamic: true));
        Assert.Same(atlas, rig.Scene.PointShadowTexture);
        Assert.Equal(10, rig.Scene.PointShadowRows);
        Assert.Equal(7, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointDynamicRenders);

        rig.RenderFrame(s => Queue(s, dynamic: false));
        Assert.Same(atlas, rig.Scene.PointShadowTexture);
        Assert.Equal(10, rig.Scene.PointShadowRows);
        Assert.Equal(6, rig.Scene.PointShadowedLights);
        Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
    }

    [Fact]
    public void StaticCapacityLowersFaceResolutionBeforeDroppingRows()
    {
        using var rig = new ResidencyRig();
        rig.Settings.PointShadows.FaceResolution = 384;
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 64;

        void Queue(Scene3D scene)
        {
            scene.Draw(rig.Mesh, Matrix4x4.Identity);
            for (int light = 0; light < 50; light++)
                scene.AddLight(new Vector3(light, 2f, 0f), Color.White, 4f, 1f, LightShadow.Static(light + 1));
        }

        rig.RenderFrame(Queue);
        rig.RenderFrame(Queue);

        Assert.Equal(54, rig.Scene.PointShadowRows);
        Assert.Equal(303, rig.Scene.PointShadowFaceResolution);
        Assert.Equal(50, rig.Scene.PointShadowedLights);
        Assert.Equal(new PointShadowResolution(true, 303, 54, false, null), rig.Scene.ResolvedPointShadows);
    }

    [Fact]
    public void DynamicReserveShrinksBeforeAnyStaticRowIsLost()
    {
        using var rig = new ResidencyRig();

        void Queue(Scene3D scene)
        {
            for (int light = 0; light < 253; light++)
                scene.AddLight(new Vector3(light, 2f, 0f), Color.White, 4f, 1f, LightShadow.Static(light + 1));
        }

        rig.RenderFrame(Queue);
        rig.RenderFrame(Queue);

        Assert.NotNull(rig.Scene.PointShadowTexture);
        Assert.Equal(256, rig.Scene.PointShadowRows);
        Assert.Equal(PointShadowSettings.MinFaceResolution, rig.Scene.PointShadowFaceResolution);
        Assert.False(rig.Scene.ResolvedPointShadows.Degraded);
    }

    [Fact]
    public void StaticCapacityBeyondTheMinimumResolutionIsRefusedWithoutTrimming()
    {
        using var rig = new ResidencyRig();

        void Queue(Scene3D scene)
        {
            for (int light = 0; light < 257; light++)
                scene.AddLight(new Vector3(light, 2f, 0f), Color.White, 4f, 1f, LightShadow.Static(light + 1));
        }

        rig.RenderFrame(Queue);
        rig.RenderFrame(Queue);

        Assert.Null(rig.Scene.PointShadowTexture);
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.Contains("257 static", rig.Scene.ResolvedPointShadows.Reason, StringComparison.Ordinal);
        Assert.Contains("256", rig.Scene.ResolvedPointShadows.Reason, StringComparison.Ordinal);
        Assert.Equal(0, rig.Scene.PointShadowedLights);
        Assert.Single(rig.Logger.Errors);
    }

    sealed class ResidencyRig : IDisposable
    {
        readonly FakeGpuDevice _device;
        readonly FakeGpuResourceFactory _factory;
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;

        internal ResidencyRig()
        {
            _device = new FakeGpuDevice();
            _factory = (FakeGpuResourceFactory)_device.Factory;
            _targetTexture = _factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _factory.CreateFramebuffer(null, _targetTexture);
            Settings = new ShadowSettings { Mode = ShadowMode.Off };
            Logger = new RecordingLogger();
            Scene = new Scene3D(_device, _target.Outputs, Settings, Logger);
            Mesh = Scene.LoadMesh(MeshPrimitives.Box(1f));
        }

        internal ShadowSettings Settings { get; }
        internal RecordingLogger Logger { get; }
        internal Scene3D Scene { get; }
        internal MeshHandle Mesh { get; }

        internal void RenderFrame(Action<Scene3D> describe)
        {
            Scene.Begin();
            describe(Scene);
            Scene.PrepareFrame();
            using IGpuCommandList commands = _factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
        }

        public void Dispose()
        {
            Scene.Dispose();
            _target.Dispose();
            _targetTexture.Dispose();
            _device.Dispose();
        }
    }
}
