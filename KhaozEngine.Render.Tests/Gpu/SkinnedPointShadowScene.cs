using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy native scene for skinned point-shadow pixel and cache proofs.</summary>
public sealed class SkinnedPointShadowScene : IDisposable
{
    public const int Width = 192;
    public const int Height = 192;
    static readonly Vector3 Light = new(0f, 2.4f, 0f);
    static readonly Vector3 RigidProbe = new(5f, 0f, 0f);
    static readonly Vector3 SkinnedProbe = new(3f, 0f, 5f);
    static readonly Vector3 SkinnedLitProbe = new(-3f, 0f, -5f);
    static readonly Vector3 LitProbe = new(-5f, 0f, 0f);
    static readonly Vector3 BodyProbe = new(0f, 1.1f, 2.2f);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _floor;
    MeshHandle _box;
    SkinnedGltfMesh? _tube;
    SkinnedMeshHandle _tubeHandle;
    Matrix4x4[]? _bentPose;

    public sealed class Shot(byte[] rgba, ShadowPassDiagnostics diagnostics, PointShadowResolution resolved,
        int baseRow, int transientRow, int rigidShadowLuminance, int skinnedShadowLuminance,
        int litLuminance, int skinnedLitLuminance, int bodyLuminance, int drawnSkinnedInstances)
    {
        public byte[] Rgba { get; } = rgba;
        public ShadowPassDiagnostics Diagnostics { get; } = diagnostics;
        public PointShadowResolution Resolved { get; } = resolved;
        public int BaseRow { get; } = baseRow;
        public int TransientRow { get; } = transientRow;
        public int RigidShadowLuminance { get; } = rigidShadowLuminance;
        public int SkinnedShadowLuminance { get; } = skinnedShadowLuminance;
        public int LitLuminance { get; } = litLuminance;
        public int SkinnedLitLuminance { get; } = skinnedLitLuminance;
        public int BodyLuminance { get; } = bodyLuminance;
        public int DrawnSkinnedInstances { get; } = drawnSkinnedInstances;
    }

    public readonly record struct ByteIdentityPair(byte[] BaseOnly, byte[] Candidate,
        int TransientRows, int DynamicSkinnedDrawCalls, int TransientRow);

    public IReadOnlyList<Shot> StaticRigidAndBentSkinned()
    {
        Reset();
        Capture(dynamicLight: false, gpuSkinning: true, bent: false);
        return
        [
            Capture(dynamicLight: false, gpuSkinning: true, bent: false),
            Capture(dynamicLight: false, gpuSkinning: true, bent: true),
            Capture(dynamicLight: false, gpuSkinning: true, bent: true),
        ];
    }

    public Shot DynamicRigidAndSkinned(bool gpuSkinning)
    {
        Reset();
        Capture(dynamicLight: true, gpuSkinning, bent: true);
        return Capture(dynamicLight: true, gpuSkinning, bent: true);
    }

    public Shot BaseOneTransientZero(bool gpuSkinning)
    {
        Reset();
        CaptureTwoLights(gpuSkinning);
        CaptureTwoLights(gpuSkinning);
        return CaptureTwoLights(gpuSkinning);
    }

    public (Shot Solid, Shot Half, Shot OptedOut) PolicyVariants(bool gpuSkinning)
    {
        Reset();
        Capture(dynamicLight: false, gpuSkinning, bent: true);
        Capture(dynamicLight: false, gpuSkinning, bent: true);
        Capture(dynamicLight: false, gpuSkinning, bent: true);
        Shot solid = Capture(dynamicLight: false, gpuSkinning, bent: true);
        Shot half = Capture(dynamicLight: false, gpuSkinning, bent: true, dissolve: 0.5f);
        Shot optedOut = Capture(dynamicLight: false, gpuSkinning, bent: true, castsShadows: false);
        return (solid, half, optedOut);
    }

    public (Shot Shadowed, Shot Unshadowed) OffCameraSkinnedCaster(bool gpuSkinning)
    {
        Reset();
        CaptureOffCamera(gpuSkinning, castsShadows: true);
        CaptureOffCamera(gpuSkinning, castsShadows: true);
        Shot shadowed = CaptureOffCamera(gpuSkinning, castsShadows: true);
        Shot unshadowed = CaptureOffCamera(gpuSkinning, castsShadows: false);
        return (shadowed, unshadowed);
    }

    public (Shot Solid, Shot Partial, Shot Excluded) ClearanceVariants(bool gpuSkinning,
        bool exclusionBox)
    {
        Reset();
        Capture(dynamicLight: true, gpuSkinning, bent: true, drawRigid: false);
        Shot solid = Capture(dynamicLight: true, gpuSkinning, bent: true, drawRigid: false);
        LightShadow partial = exclusionBox
            ? LightShadow.Dynamic.WithExclusionBox(new Vector3(-0.5f, 0f, 1.5f),
                new Vector3(0.5f, 2.2f, 2.5f))
            : LightShadow.DynamicWithNearRadius(3f);
        LightShadow full = exclusionBox
            ? LightShadow.Dynamic.WithExclusionBox(new Vector3(-5f), new Vector3(5f))
            : LightShadow.DynamicWithNearRadius(10f);
        Shot partialShot = Capture(dynamicLight: true, gpuSkinning, bent: true,
            drawRigid: false, shadowOverride: partial);
        Shot excluded = Capture(dynamicLight: true, gpuSkinning, bent: true,
            drawRigid: false, shadowOverride: full);
        return (solid, partialShot, excluded);
    }

    public (Shot Origin, Shot Far) OriginPair(bool gpuSkinning, bool dynamicLight, float dissolve)
    {
        Vector3 far = new(100_000f, 0f, -100_000f);
        if (dissolve > 0f)
        {
            // The dissolve mask is anchored to absolute world position. Keep that position fixed while rebasing.
            Shot first = CaptureTranslatedScenario(far, far, gpuSkinning, dynamicLight, dissolve);
            Shot rebased = CaptureTranslatedScenario(far, far + new Vector3(16f, 0f, 0f),
                gpuSkinning, dynamicLight, dissolve);
            return (first, rebased);
        }
        return (CaptureTranslatedScenario(Vector3.Zero, Vector3.Zero, gpuSkinning, dynamicLight, dissolve),
            CaptureTranslatedScenario(far, far, gpuSkinning, dynamicLight, dissolve));
    }

    public ByteIdentityPair DynamicSkinnedWithIdleTransient()
    {
        Reset();
        Capture(dynamicLight: true, gpuSkinning: true, bent: true);
        Shot baseOnly = Capture(dynamicLight: true, gpuSkinning: true, bent: true);

        Reset();
        Capture(dynamicLight: false, gpuSkinning: true, bent: true);
        Capture(dynamicLight: false, gpuSkinning: true, bent: true);
        Capture(dynamicLight: false, gpuSkinning: true, bent: true);
        Capture(dynamicLight: true, gpuSkinning: true, bent: true);
        Shot candidate = Capture(dynamicLight: true, gpuSkinning: true, bent: true);
        return new ByteIdentityPair(baseOnly.Rgba, candidate.Rgba,
            candidate.Resolved.TransientAtlasRows,
            candidate.Diagnostics.PointDynamicSkinnedDrawCalls, candidate.TransientRow);
    }

    public ByteIdentityPair NoSkinnedColdScene()
    {
        byte[] baseOnly;
        using (var fresh = new SkinnedPointShadowScene())
            baseOnly = fresh.CaptureNoSkinned(queueFarBody: false).Rgba;
        Shot candidate = CaptureNoSkinned(queueFarBody: true);
        return new ByteIdentityPair(baseOnly, candidate.Rgba,
            candidate.Resolved.TransientAtlasRows,
            candidate.Diagnostics.PointDynamicSkinnedDrawCalls, candidate.TransientRow);
    }

    public (Shot Aged, Shot Fresh) ReusedSceneMatchesFreshAfterConfigurationChanges()
    {
        StaticRigidAndBentSkinned();
        DynamicRigidAndSkinned(gpuSkinning: false);
        PolicyVariants(gpuSkinning: true);
        BaseOneTransientZero(gpuSkinning: false);
        OriginPair(gpuSkinning: true, dynamicLight: false, dissolve: 0.5f);
        Shot aged = ThreeStaticCandidates();
        using var freshFixture = new SkinnedPointShadowScene();
        Shot fresh = freshFixture.ThreeStaticCandidates();
        return (aged, fresh);
    }

    Shot ThreeStaticCandidates()
    {
        Reset();
        for (int frame = 0; frame < 4; frame++) CaptureThreeStaticFrame();
        Shot final = CaptureThreeStaticFrame();
        if (final.Resolved.TransientAtlasRows != 3)
            throw new InvalidOperationException("the three-row parity fixture did not reach its intended atlas");
        return final;
    }

    Shot CaptureThreeStaticFrame() => CaptureFrame(gpuSkinning: true, scene =>
    {
        scene.Draw(_floor, Matrix4x4.Identity, Color.White);
        scene.Draw(_box,
            Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(2.2f, 1.5f, 0f),
            Color.White);
        scene.DrawSkinned(_tubeHandle, _bentPose!, Matrix4x4.CreateTranslation(0f, 0f, 2.2f), Color.White);
        scene.AddLight(Light + new Vector3(-1f, 0f, 0f), Color.White, 12f,
            PointShadowScene.Intensity, LightShadow.Static(1));
        scene.AddLight(Light, Color.White, 12f, PointShadowScene.Intensity, LightShadow.Static(2));
        scene.AddLight(Light + new Vector3(1f, 0f, 0f), Color.White, 12f,
            PointShadowScene.Intensity, LightShadow.Static(3));
    }, RigidProbe, SkinnedProbe, LitProbe, SkinnedLitProbe);

    Shot CaptureNoSkinned(bool queueFarBody)
    {
        Reset();
        CaptureNoSkinnedFrame(queueFarBody);
        return CaptureNoSkinnedFrame(queueFarBody);
    }

    Shot CaptureNoSkinnedFrame(bool queueFarBody) => CaptureFrame(gpuSkinning: true, scene =>
    {
        scene.Draw(_floor, Matrix4x4.Identity, Color.White);
        scene.Draw(_box,
            Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(2.2f, 1.5f, 0f),
            Color.White);
        if (queueFarBody)
            scene.DrawSkinned(_tubeHandle, _tube!.RestPose,
                Matrix4x4.CreateTranslation(1000f, 0f, 0f), Color.White);
        scene.AddLight(Light, Color.White, 12f, PointShadowScene.Intensity, LightShadow.Static(64));
    }, RigidProbe, SkinnedProbe, LitProbe, SkinnedLitProbe);

    Shot CaptureTranslatedScenario(Vector3 worldOffset, Vector3 renderOrigin,
        bool gpuSkinning, bool dynamicLight, float dissolve)
    {
        Reset();
        Scene3D scene = _scene!;
        scene.RenderOrigin = renderOrigin;
        scene.Camera.Frame(worldOffset, new Vector3(22f, 2f, 22f), margin: 1f);
        CaptureTranslatedFrame(worldOffset, gpuSkinning, dynamicLight, dissolve);
        CaptureTranslatedFrame(worldOffset, gpuSkinning, dynamicLight, dissolve);
        return CaptureTranslatedFrame(worldOffset, gpuSkinning, dynamicLight, dissolve);
    }

    Shot CaptureTranslatedFrame(Vector3 origin, bool gpuSkinning, bool dynamicLight, float dissolve) =>
        CaptureFrame(gpuSkinning, scene =>
        {
            scene.Draw(_floor, Matrix4x4.CreateTranslation(origin), Color.White);
            scene.Draw(_box, Matrix4x4.CreateScale(0.8f, 3f, 0.8f)
                * Matrix4x4.CreateTranslation(origin + new Vector3(2.2f, 1.5f, 0f)), Color.White);
            Matrix4x4 world = Matrix4x4.CreateTranslation(origin + new Vector3(0f, 0f, 2.2f));
            if (dissolve > 0f)
                scene.DrawSkinned(_tubeHandle, _bentPose!, world, Color.White, Material.None,
                    dissolve, 0f, default);
            else
                scene.DrawSkinned(_tubeHandle, _bentPose!, world, Color.White);
            scene.AddLight(origin + Light, Color.White, 12f, PointShadowScene.Intensity,
                dynamicLight ? LightShadow.Dynamic : LightShadow.Static(64));
        }, origin + RigidProbe, origin + SkinnedProbe, origin + LitProbe, origin + SkinnedLitProbe);

    public bool OnScreen(Vector3 world)
    {
        Device();
        _scene!.Camera.WorldToScreen(world, Width, Height, out Vector2 pixel);
        return pixel.X >= 0f && pixel.X < Width && pixel.Y >= 0f && pixel.Y < Height;
    }

    public int ProbeRed(Shot shot, Vector3 world)
    {
        Device();
        return PatchRed(shot.Rgba, _scene!.Camera, world);
    }

    public static long ShadowDarkening(Shot unshadowed, Shot candidate)
    {
        long darkening = 0;
        for (int i = 0; i < unshadowed.Rgba.Length; i += 4)
            darkening += Math.Max(0, unshadowed.Rgba[i] - candidate.Rgba[i]);
        return darkening;
    }

    public int FloorShadowDarkening(Shot unshadowed, Shot candidate)
    {
        Device();
        int darkening = 0;
        for (float z = 3f; z <= 6f; z += 0.5f)
            for (float x = 1.5f; x <= 4.5f; x += 0.5f)
            {
                Vector3 floor = new(x, 0f, z);
                darkening += Math.Max(0, ProbeRed(unshadowed, floor) - ProbeRed(candidate, floor));
            }
        return darkening;
    }

    void Reset()
    {
        IGpuDevice gd = Device();
        Scene3D scene = _scene!;
        scene.RenderOrigin = null;
        Setup(scene);
        scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
        scene.Begin();
        scene.PrepareFrame();
        using (GpuRecording.Open(gd, _commands!, "SkinnedPointShadowScene.Reset"))
            scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
        gd.Submit(_commands!);
        gd.WaitForIdle();
        scene.RequestPointShadowSettings(new PointShadowSettings());
    }

    Shot Capture(bool dynamicLight, bool gpuSkinning, bool bent, float dissolve = 0f,
        bool castsShadows = true, bool drawRigid = true, LightShadow? shadowOverride = null) =>
        CaptureFrame(gpuSkinning, scene =>
    {
        scene.Draw(_floor, Matrix4x4.Identity, Color.White);
        if (drawRigid)
            scene.Draw(_box,
                Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(2.2f, 1.5f, 0f),
                Color.White);
        Matrix4x4[] pose = bent ? _bentPose! : _tube!.RestPose;
        Matrix4x4 world = Matrix4x4.CreateTranslation(0f, 0f, 2.2f);
        if (dissolve > 0f)
            scene.DrawSkinned(_tubeHandle, pose, world, Color.White, Material.None,
                dissolve, 0f, default, castsShadows);
        else
            scene.DrawSkinned(_tubeHandle, pose, world, Color.White, Material.None, castsShadows);
        scene.AddLight(Light, Color.White, 12f, PointShadowScene.Intensity,
            shadowOverride ?? (dynamicLight ? LightShadow.Dynamic : LightShadow.Static(64)));
    }, RigidProbe, SkinnedProbe, LitProbe, SkinnedLitProbe);

    Shot CaptureTwoLights(bool gpuSkinning) => CaptureFrame(gpuSkinning, scene =>
    {
        scene.Draw(_floor, Matrix4x4.Identity, Color.White);
        scene.Draw(_box,
            Matrix4x4.CreateScale(0.8f, 3f, 0.8f) * Matrix4x4.CreateTranslation(-2.8f, 1.5f, 0f),
            Color.White);
        scene.DrawSkinned(_tubeHandle, _bentPose!, Matrix4x4.CreateTranslation(5f, 0f, 2.2f), Color.White);
        scene.AddLight(new Vector3(-5f, 2.4f, 0f), Color.White, 6f,
            PointShadowScene.Intensity, LightShadow.Static(1));
        scene.AddLight(new Vector3(5f, 2.4f, 0f), Color.White, 6f,
            PointShadowScene.Intensity, LightShadow.Static(2));
    }, new Vector3(-1f, 0f, 0f), new Vector3(8f, 0f, 4f),
        new Vector3(-9f, 0f, 0f), new Vector3(2f, 0f, -4f), lightIndex: 1);

    Shot CaptureOffCamera(bool gpuSkinning, bool castsShadows) => CaptureFrame(gpuSkinning, scene =>
    {
        scene.Draw(_floor, Matrix4x4.Identity, Color.White);
        scene.DrawSkinned(_tubeHandle, _tube!.RestPose, Matrix4x4.CreateTranslation(0f, 0f, 16f),
            Color.White, Material.None, castsShadows);
        scene.AddLight(new Vector3(0f, 2f, 18f), Color.White, 26f, PointShadowScene.FarIntensity,
            LightShadow.Static(108));
    }, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f),
        new Vector3(6f, 0f, 0f), new Vector3(6f, 0f, 0f));

    Shot CaptureFrame(bool gpuSkinning, Action<Scene3D> describe,
        Vector3 rigidProbe, Vector3 skinnedProbe, Vector3 litProbe, Vector3 skinnedLitProbe,
        int lightIndex = 0)
    {
        IGpuDevice gd = Device();
        Scene3D scene = _scene!;
        scene.UseGpuSkinning = gpuSkinning;
        scene.Begin();
        describe(scene);
        scene.PrepareFrame();
        using (GpuRecording.Open(gd, _commands!, "SkinnedPointShadowScene.Capture"))
            scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
        gd.Submit(_commands!);
        gd.WaitForIdle();
        byte[] rgba = GpuReadback.ToRgba(gd, _target!, Width, Height);
        return new Shot(rgba, scene.LastShadowPassDiagnostics, scene.ResolvedPointShadows,
            scene.PointShadowBaseSlotForLight(lightIndex), scene.PointShadowTransientSlotForLight(lightIndex),
            PatchRed(rgba, scene.Camera, rigidProbe),
            PatchRed(rgba, scene.Camera, skinnedProbe),
            PatchRed(rgba, scene.Camera, litProbe),
            PatchRed(rgba, scene.Camera, skinnedLitProbe),
            PatchRed(rgba, scene.Camera, BodyProbe), scene.DrawnSkinnedInstances);
    }

    static int PatchRed(byte[] rgba, IIsoCamera3D camera, Vector3 world)
    {
        camera.WorldToScreen(world, Width, Height, out Vector2 pixel);
        int cx = Math.Clamp((int)pixel.X, 1, Width - 2);
        int cy = Math.Clamp((int)pixel.Y, 1, Height - 2);
        int sum = 0;
        for (int y = cy - 1; y <= cy + 1; y++)
            for (int x = cx - 1; x <= cx + 1; x++)
                sum += rgba[(y * Width + x) * 4];
        return sum / 9;
    }

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;
        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = _gpu.GpuDevice;
        IGpuResourceFactory factory = gd.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        _scene = new Scene3D(gd, _framebuffer.Outputs);
        Setup(_scene);
        _floor = _scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        _box = _scene.LoadMesh(MeshPrimitives.Box(1f));
        _tube = SkinnedMeshBuilder.BuildTube(0.55f, 2.2f, 8, 16, 4, Axis.Y);
        _tubeHandle = _scene.LoadSkinnedMesh(_tube);
        _bentPose = BentPose(_tube);
        return gd;
    }

    static Matrix4x4[] BentPose(SkinnedGltfMesh tube)
    {
        Matrix4x4[] pose = (Matrix4x4[])tube.RestPose.Clone();
        Matrix4x4 accumulated = Matrix4x4.Identity;
        Vector3 previousRest = tube.RestPose[0].Translation;
        Vector3 jointPosition = previousRest;
        for (int bone = 0; bone < tube.BoneCount; bone++)
        {
            Vector3 restPosition = tube.RestPose[bone].Translation;
            jointPosition += Vector3.Transform(restPosition - previousRest, accumulated);
            if (bone >= tube.BoneCount - 2)
                accumulated = Matrix4x4.CreateRotationZ(0.5f) * accumulated;
            pose[bone] = Matrix4x4.CreateTranslation(-restPosition)
                * accumulated * Matrix4x4.CreateTranslation(jointPosition);
            previousRest = restPosition;
        }
        return pose;
    }

    static void Setup(Scene3D scene)
    {
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
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.35f;
        scene.Camera.Frame(Vector3.Zero, new Vector3(22f, 2f, 22f), margin: 1f);
    }

    public void Dispose()
    {
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
    }
}
