using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// A real <see cref="Scene3D"/> over <see cref="FakeGpuDevice"/> for the draw-descriptor and motion-key tests. It needs
/// no device, so the queues, the bone palette and the motion history are read directly. Shadows and the starfield are
/// off, so a rendered frame records only what a test queues, the same setup the fake-device render tests use
/// (<c>SkinnedColorCutoutContractTests</c>).
/// </summary>
internal sealed class MotionTestScene : IDisposable
{
    public const int Width = 32, Height = 24;

    readonly FakeGpuDevice _device = new();
    readonly IGpuTexture _colour;

    public MotionTestScene()
    {
        _colour = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        Target = _device.Factory.CreateFramebuffer(null, _colour);
        Scene = new Scene3D(_device, Target.Outputs);
        Scene.Post.Starfield = false;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
    }

    public Scene3D Scene { get; }

    public IGpuFramebuffer Target { get; }

    /// <summary>The fake device, so a test can read what the scene created on it.</summary>
    public FakeGpuDevice Device => _device;

    /// <summary>Upload a three-bone tube. <paramref name="mesh"/> carries its rest pose and inverse-bind.</summary>
    public SkinnedMeshHandle LoadTube(out SkinnedGltfMesh mesh)
    {
        mesh = SkinnedMeshBuilder.BuildTube(0.22f, 1.5f, 6, 4, 3, Axis.Y);
        return Scene.LoadSkinnedMesh(mesh);
    }

    /// <summary>A pose that bends every bone after the root, so the composed palette differs from the rest pose.</summary>
    public static Matrix4x4[] Bent(SkinnedGltfMesh mesh, float radians)
    {
        var bones = (Matrix4x4[])mesh.RestPose.Clone();
        for (int b = 1; b < bones.Length; b++) bones[b] = Matrix4x4.CreateRotationZ(radians * b) * bones[b];
        return bones;
    }

    /// <summary>Prepare and record the queued frame, the way the fake-device render tests do.</summary>
    public void Render()
    {
        Scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        Scene.RenderInternal(commands, Width, Height, Target);
    }

    public void Dispose()
    {
        Scene.Dispose();
        Target.Dispose();
        _colour.Dispose();
        _device.Dispose();
    }
}
