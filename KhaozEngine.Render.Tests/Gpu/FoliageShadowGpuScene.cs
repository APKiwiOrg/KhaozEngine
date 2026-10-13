using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A lazy, shared preview for colour and cascade readbacks, with a receive-only floor.</summary>
public sealed class FoliageShadowGpuScene : IDisposable
{
    public const int Width = 320, Height = 240;
    GpuDeviceContext? _gpu;
    Render3DPreview? _preview;
    public MeshHandle Shrub { get; private set; }
    public MeshHandle Floor { get; private set; }
    public static GltfMesh ShrubMesh() => MeshPrimitives.Tile(.8f, 2f);

    public Scene3D Scene
    {
        get
        {
            if (_preview is not null) return _preview.Scene;
            _gpu = GpuDeviceContext.CreateHeadless();
            _preview = new Render3DPreview(_gpu.GpuDevice, Width, Height,
                new ShadowSettings { ShadowMapResolution = 512, ShadowCascadeCount = 3 });
            Shrub = _preview.Scene.LoadMesh(ShrubMesh());
            Floor = _preview.Scene.LoadMesh(MeshPrimitives.Tile(60f, .1f));
            return _preview.Scene;
        }
    }

    public byte[] Capture(Action<Scene3D> draw)
    {
        Scene3D scene = Scene;
        scene.CameraOverride = null;
        scene.RenderOrigin = Vector3.Zero;
        scene.EffectTimeSeconds = 1f;
        scene.Post.TransparentBackground = false;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Quality.AntiAliasing = default;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Quality.Shadows.ShadowNearDistance = 20f;
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(0f, -.5f, .87f));
        scene.Post.BackgroundColor = new Color(.08f, .1f, .14f);
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.Azimuth = .6f;
        scene.Camera.Elevation = .95f;
        scene.Camera.Zoom = 1f;
        scene.Camera.Frame(new Vector3(0f, 0f, 3f), new Vector3(14f, 6f, 14f));
        _preview!.Capture(s =>
        {
            s.Draw(Floor, Matrix4x4.Identity, Color.White, Material.None, false);
            draw(s);
        });
        return _preview.ReadbackRgba();
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _gpu?.Dispose();
    }
}
