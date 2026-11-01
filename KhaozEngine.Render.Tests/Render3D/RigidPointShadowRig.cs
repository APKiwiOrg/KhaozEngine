using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

internal sealed class RigidPointShadowRig : IDisposable
{
    readonly IGpuTexture _texture;
    readonly IGpuFramebuffer _target;
    readonly SkinnedGltfMesh _tube;
    readonly SkinnedMeshHandle _skinned;

    internal RigidPointShadowRig()
    {
        Device = new FakeGpuDevice();
        Factory = (FakeGpuResourceFactory)Device.Factory;
        _texture = Factory.CreateTexture(GpuTextureDescription.Texture2D(16, 16,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = Factory.CreateFramebuffer(null, _texture);
        Settings = new ShadowSettings { Mode = ShadowMode.Off };
        Scene = new Scene3D(Device, _target.Outputs, Settings);
        Box = Scene.LoadMesh(MeshPrimitives.Box(0.5f));
        _tube = SkinnedMeshBuilder.BuildTube(0.35f, 2f, 4, 6, 3, Axis.Z);
        _skinned = Scene.LoadSkinnedMesh(_tube);
    }

    internal FakeGpuDevice Device { get; }
    internal FakeGpuResourceFactory Factory { get; }
    internal ShadowSettings Settings { get; }
    internal Scene3D Scene { get; }
    internal MeshHandle Box { get; }
    internal List<RecordingGpuCommandList.Command> Commands { get; } = new();
    internal byte[]? InstanceUpload { get; private set; }

    internal ShadowPassDiagnostics Frame(float x = 0f, int lights = 2, bool moving = true,
        ShadowCasterMobility mobility = ShadowCasterMobility.Dynamic, bool skinned = false,
        bool dynamicLights = false, bool casts = true, bool shadowOnly = false, float dissolve = 0f,
        MeshHandle? actorMesh = null, Vector3 origin = default, long firstKey = 7, float sceneryX = -2f, MotionKey motion = default)
    {
        Scene.Begin();
        Scene.Draw(Box, Matrix4x4.CreateTranslation(origin + new Vector3(sceneryX, 0f, 0f)));
        if (moving)
            Scene.Draw(new RigidInstanceDraw(actorMesh ?? Box,
                Matrix4x4.CreateTranslation(origin + new Vector3(x, 0f, 0f)))
            {
                ShadowMobility = mobility,
                Motion = motion,
                CastsShadows = casts,
                ShadowOnly = shadowOnly,
                Dissolve = dissolve,
            });
        if (skinned) Scene.DrawSkinned(_skinned, _tube.RestPose, Matrix4x4.CreateTranslation(origin), Color.White);
        for (int i = 0; i < lights; i++)
            Scene.AddLight(origin + new Vector3(i * 2f, 1f, 1f), Color.White, 10f, 1f,
                dynamicLights ? LightShadow.Dynamic : LightShadow.Static(firstKey + i));
        return Render();
    }

    internal ShadowPassDiagnostics Render()
    {
        Scene.PrepareFrame();
        using IGpuCommandList inner = Factory.CreateCommandList();
        using var commands = new RecordingGpuCommandList(inner) { CommandTrace = Commands, CapturePayloads = true, CaptureBindings = true };
        Commands.Clear();
        commands.Begin();
        Scene.RenderInternal(commands, 16, 16, _target);
        commands.End();
        var binding = commands.Bindings.FirstOrDefault(draw => IsRigidPointPipeline(draw.Pipeline));
        InstanceUpload = binding is null ? null : commands.Uploads.FirstOrDefault(upload =>
            ReferenceEquals(upload.Buffer, binding.VertexBuffers[1])).Data;
        return Scene.LastShadowPassDiagnostics;
    }

    internal RecordingGpuCommandList.Command[] RigidPointDraws() => Commands.Where(command =>
        command.Kind == "draw" && IsRigidPointPipeline(command.Pipeline))
        .ToArray();

    static bool IsRigidPointPipeline(IGpuPipeline? pipeline) => pipeline is FakePipeline { Request: { } request }
        && request.FragmentGlsl.Contains("LightPosRadius") && request.VertexGlsl.Contains("IModel0");

    internal void Warm()
    {
        Frame();
        Frame();
        Frame();
    }

    public void Dispose()
    {
        Scene.Dispose();
        _target.Dispose();
        _texture.Dispose();
        Device.Dispose();
    }
}
