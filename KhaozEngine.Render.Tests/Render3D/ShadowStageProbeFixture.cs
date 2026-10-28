using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Render3D;

internal sealed class ShadowStageProbeFixture : IDisposable
{
    internal readonly FakeGpuDevice Device = new();
    readonly IGpuTexture _texture;
    internal readonly IGpuFramebuffer Target;
    internal readonly Scene3D Scene;
    internal readonly MeshHandle Mesh;
    internal readonly Frame Frame = new();
    internal readonly RecordingGpuCommandList Commands;
    long _sequence;
    internal ShadowStageProbeFixture(bool directional = false)
    {
        _texture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        Target = Device.Factory.CreateFramebuffer(null, _texture);
        Scene = new Scene3D(Device, Target.Outputs, new ShadowSettings { Mode = directional ? ShadowMode.ShadowMap : ShadowMode.Off })
        { FrustumCulling = false };
        Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        Mesh = Scene.LoadMesh(MeshPrimitives.Box(1));
        Commands = new RecordingGpuCommandList(Device.Factory.CreateCommandList()) { CommandTrace = new List<RecordingGpuCommandList.Command>() };
        Frame.Commands = Commands; Frame.Width = 32; Frame.Height = 24;
    }
    internal ShadowStageProbeSnapshot Render(ShadowStageProbeMode mode, bool point = true, bool rigid = true,
        FoliageBatch? foliage = null, bool staticLight = true, bool dynamicLight = true, bool duplicate = false,
        Action<Scene3D>? extraQueue = null, FoliageRenderSettings? foliageSettings = null)
    {
        var request = new ShadowStageProbeRequest(new Guid("c49950ca-e4a6-493d-a146-ac14c91061dc"), ++_sequence, mode);
        Frame.BeginShadowStageCallback(request);
        Scene.ConfigureShadowStageProbe(request);
        Scene.Begin();
        if (rigid) Scene.Draw(Mesh, Matrix4x4.CreateTranslation(1.5f, .5f, 0));
        if (foliage is not null) Scene.DrawFoliage(foliage, Vector3.Zero,
            foliageSettings ?? new FoliageRenderSettings { DrawRadius = 100, DistantDensity = 1 });
        extraQueue?.Invoke(Scene);
        if (point)
        {
            if (staticLight) AddStatic();
            if (duplicate) AddStatic();
            if (dynamicLight) Scene.AddLight(new Vector3(0, 2, 0), Color.White, 6, 1, LightShadow.Dynamic);
        }
        Scene.PrepareFrame();
        Commands.Clear();
        Commands.CommandTrace!.Clear();
        Commands.Begin();
        Scene.RenderInternal(Commands, 32, 24, Target);
        Commands.End();
        Scene.PublishShadowStageProbe(Frame);
        Frame.CompleteShadowStageCallback();
        return Frame.CompletedShadowStageCallback.Snapshot ?? default;
    }
    void AddStatic() => Scene.AddLight(new Vector3(0, 2, 0), Color.White, 6, 1, LightShadow.Static(1));
    internal FoliageBatch Foliage() => Scene.CreateFoliageBatch([
        new(Mesh, Matrix4x4.Identity, .1f, new FoliageMotionProfile(.3f, .15f, .4f, .035f), true),
        new(Mesh, Matrix4x4.CreateTranslation(0, 0, 1), .9f, new FoliageMotionProfile(.3f, .15f, .4f, .035f), true)]);
    internal long[] Signatures() => (long[])((long[])typeof(Scene3D)
        .GetField("_pointCasterSignatures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(Scene)!).Clone();
    internal string[] Order() => Commands.CommandTrace!.Select(c => $"{c.Kind}/{c.Slot}/{c.Value}").ToArray();
    public void Dispose() { Frame.DisposeSubmission(); Commands.Dispose(); Scene.Dispose(); Target.Dispose(); _texture.Dispose(); Device.Dispose(); }
}
