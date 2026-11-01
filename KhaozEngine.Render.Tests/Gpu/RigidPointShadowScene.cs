using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A lazy split scene and a separate complete-current-redraw scene on the same native device.</summary>
public sealed class RigidPointShadowScene : IDisposable
{
    public const int Width = 256, Height = 256;
    public readonly record struct Pose(bool Actor = true, float Z = 1.5f,
        ShadowCasterMobility Mobility = ShadowCasterMobility.Dynamic, bool Skinned = true,
        bool ActorCasts = true, bool SceneryCasts = true, bool SkinnedCasts = true)
    {
        public Pose() : this(true, 1.5f, ShadowCasterMobility.Dynamic, true, true, true, true) { }
    }
    public sealed record Shot(byte[] Rgba, ShadowPassDiagnostics Work, PointShadowResolution Resolution,
        int[] BaseRows, int[] TransientRows);
    public sealed record Pair(Shot Split, Shot Full);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Head? _split, _full;

    sealed class Head(Scene3D scene)
    {
        public Scene3D Scene { get; } = scene;
        public MeshHandle Floor { get; } = scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        public MeshHandle Box { get; } = scene.LoadMesh(MeshPrimitives.Box(1f));
        public SkinnedGltfMesh Tube { get; } = SkinnedMeshBuilder.BuildTube(0.55f, 2.2f, 8, 16, 4, Axis.Y);
        public SkinnedMeshHandle Skin;
        public Matrix4x4[] Bent = [];
    }

    public void Reset(PointShadowFilter filter)
    {
        Device();
        foreach (Head head in new[] { _split!, _full! })
        {
            head.Scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
            Render(head, null, full: false);
            head.Scene.RequestPointShadowSettings(new PointShadowSettings
            {
                // Three static owners plus the four-row dynamic reserve make the split base seven rows.
                // Match its height in the redraw oracle so atlas-coordinate rounding is controlled.
                Filter = filter,
                FaceResolution = 256,
                MaxShadowedLights = head == _full ? 7 : 4,
                MaxStaticRebuildsPerFrame = 4,
                MaxDynamicLightsPerFrame = 4,
            });
        }
    }

    public void FreezeOrdinaryRefresh() => _split!.Scene.Post.Quality.Shadows.PointShadows.MaxStaticRebuildsPerFrame = 0;

    public Pair Capture(Pose pose) => new(Render(_split!, pose, full: false), Render(_full!, pose, full: true));

    public Shot FullControl(Pose pose) => Render(_full!, pose, full: true);

    public int Red(Shot shot, Vector3 world)
    {
        _split!.Scene.Camera.WorldToScreen(world, Width, Height, out Vector2 pixel);
        int cx = (int)pixel.X, cy = (int)pixel.Y;
        if (cx < 1 || cx >= Width - 1 || cy < 1 || cy >= Height - 1)
            throw new InvalidOperationException("the receiver probe is outside the image");
        int sum = 0;
        for (int y = cy - 1; y <= cy + 1; y++)
            for (int x = cx - 1; x <= cx + 1; x++) sum += shot.Rgba[(y * Width + x) * 4];
        return sum / 9;
    }

    Shot Render(Head head, Pose? pose, bool full)
    {
        IGpuDevice gpu = Device();
        Scene3D scene = head.Scene;
        scene.Begin();
        if (pose is Pose current) Draw(head, current, full);
        scene.PrepareFrame();
        using (GpuRecording.Open(gpu, _commands!, "RigidPointShadowScene.Capture"))
            scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
        gpu.Submit(_commands!);
        gpu.WaitForIdle();
        return new Shot(GpuReadback.ToRgba(gpu, _target!, Width, Height), scene.LastShadowPassDiagnostics,
            scene.ResolvedPointShadows,
            [scene.PointShadowBaseSlotForLight(0), scene.PointShadowBaseSlotForLight(1), scene.PointShadowBaseSlotForLight(2)],
            [scene.PointShadowTransientSlotForLight(0), scene.PointShadowTransientSlotForLight(1), scene.PointShadowTransientSlotForLight(2)]);
    }

    static void Draw(Head head, Pose pose, bool full)
    {
        Scene3D scene = head.Scene;
        scene.Draw(head.Floor, Matrix4x4.Identity, Color.White);
        // Interleave stationary scenery with the multipart actor, all using the same uploaded box mesh.
        if (pose.Actor) Box(new Vector3(7.2f, 1.4f, pose.Z), new Vector3(0.7f, 2.8f, 0.8f), actor: true);
        Box(new Vector3(-2.8f, 1.5f, 0f), new Vector3(0.8f, 3f, 2f));
        if (pose.Actor) Box(new Vector3(7.2f, 2f, pose.Z + 0.8f), new Vector3(0.6f, 0.5f, 1f), actor: true);
        Box(new Vector3(3f, 1.5f, 2.5f), new Vector3(0.8f, 3f, 0.8f));
        if (pose.Actor) Box(new Vector3(7.2f, 0.5f, pose.Z - 0.5f), new Vector3(0.6f, 1f, 0.5f), actor: true);
        Box(new Vector3(7.2f, 1.5f, -7f), new Vector3(0.8f, 3f, 1.2f));
        if (pose.Skinned)
            scene.DrawSkinned(head.Skin, head.Bent, Matrix4x4.CreateTranslation(5f, 0f, 2.2f),
                Color.White, Material.None, pose.SkinnedCasts);
        // Static requests sort by key, dynamic requests by distance. Match base row placement in the oracle.
        AddLight(new Vector3(-5f, 2.4f, 0f), 6f, 102);
        AddLight(new Vector3(5f, 2.4f, 0f), 7f, 101);
        AddLight(new Vector3(5f, 2.4f, -7f), 6f, 103);

        void Box(Vector3 center, Vector3 scale, bool actor = false) => scene.Draw(new RigidInstanceDraw(head.Box,
            Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(center))
        {
            ShadowMobility = full || !actor ? ShadowCasterMobility.Static : pose.Mobility,
            CastsShadows = actor ? pose.ActorCasts : pose.SceneryCasts,
            Tint = actor ? new Color(0.6f, 0.8f, 0.7f, 1f) : Color.White,
        });

        // The oracle never samples or schedules a transient layer. Every light clears and draws all current
        // rigid and skinned geometry on every frame, independent of mobility and cached signatures.
        void AddLight(Vector3 at, float radius, long key) => scene.AddLight(at, Color.White, radius,
            PointShadowScene.Intensity, full ? LightShadow.Dynamic : LightShadow.Static(key));
    }

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;
        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gpu = _gpu.GpuDevice;
        _target = gpu.Factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = gpu.Factory.CreateFramebuffer(null, _target);
        _commands = gpu.Factory.CreateCommandList();
        _split = CreateHead();
        _full = CreateHead();
        return gpu;

        Head CreateHead()
        {
            var head = new Head(new Scene3D(gpu, _framebuffer.Outputs));
            head.Skin = head.Scene.LoadSkinnedMesh(head.Tube);
            head.Bent = SkinnedPointShadowScene.BentPose(head.Tube);
            Scene3D scene = head.Scene;
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.TransparentBackground = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.CelBands = 0;
            scene.Post.Hdr.Enabled = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            scene.EffectTimeSeconds = 0f;
            scene.Camera.AspectRatio = 1f;
            scene.Camera.Azimuth = 0f;
            scene.Camera.Elevation = 1.35f;
            scene.Camera.Frame(new Vector3(1f, 0f, -1f), new Vector3(24f, 2f, 24f), margin: 1f);
            return head;
        }
    }

    public void Dispose()
    {
        _commands?.Dispose();
        _split?.Scene.Dispose();
        _full?.Scene.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
    }
}
