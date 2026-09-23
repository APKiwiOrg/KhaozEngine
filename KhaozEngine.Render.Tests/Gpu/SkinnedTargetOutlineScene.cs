using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed class SkinnedTargetOutlineScene : IDisposable
{
    public const int W = 480;
    public const int H = 320;
    public const float Dissolve = 0.45f;

    internal static readonly Color Background = new(0.025f, 0.03f, 0.045f, 1f);
    internal static readonly Color Body = new(0.04f, 0.88f, 0.12f, 1f);
    internal static readonly Color Rim = new(1f, 0.04f, 0.1f, 1f);
    internal static readonly Color Wall = new(0.04f, 0.12f, 1f, 1f);

    static readonly Matrix4x4 PrimaryWorld = Matrix4x4.CreateTranslation(-2.5f, 0.85f, -1.4f);
    static readonly Matrix4x4 HeldWorld = Matrix4x4.CreateScale(0.82f, 0.82f, 0.82f)
        * Matrix4x4.CreateTranslation(-5.15f, -0.45f, -0.05f);
    static readonly Matrix4x4 DissolveWorld = Matrix4x4.CreateTranslation(-2.15f, -0.85f, -1.8f);
    static readonly Matrix4x4 MixedCutoutWorld = Matrix4x4.CreateScale(0.72f)
        * Matrix4x4.CreateTranslation(2.45f, 1.15f, -0.2f);
    static readonly Matrix4x4 OcclusionWallWorld = Matrix4x4.CreateScale(0.7f, 1.55f, 0.3f)
        * Matrix4x4.CreateTranslation(-0.5f, 1.12f, 0.2f);

    GpuDeviceContext? _gpu;
    Render3DPreview? _preview;
    SkinnedGltfMesh? _tube;
    SkinnedMeshHandle _tubeHandle;
    SkinnedMeshHandle _cutoutHandle;
    MeshHandle _boxHandle;
    Matrix4x4[]? _bentPose;
    Matrix4x4[]? _cutoutPose;

    internal enum PoseCase
    {
        RestBody,
        BentBody,
        BentOutlineOnly,
        RestBodyBentOutline,
    }

    internal enum DissolveCase
    {
        SolidBaseline,
        PartialBody,
        PartialSceneDepthOutline,
        PartialThroughGeometryOutline,
    }

    internal enum MixedPartCase
    {
        SkinnedBody,
        RigidBody,
    }

    internal static SkinnedGltfMesh Tube() =>
        SkinnedMeshBuilder.BuildTube(0.42f, 3.8f, 12, 12, 6, Axis.Z);

    internal static Matrix4x4[] BentPose(SkinnedGltfMesh tube, float perJoint = 0.38f)
    {
        Matrix4x4[] bent = (Matrix4x4[])tube.RestPose.Clone();
        Matrix4x4 accumulated = Matrix4x4.Identity;
        Vector3 previousRest = tube.RestPose[0].Translation;
        Vector3 jointPosition = previousRest;
        for (int bone = 0; bone < tube.BoneCount; bone++)
        {
            Vector3 restPosition = tube.RestPose[bone].Translation;
            jointPosition += Vector3.Transform(restPosition - previousRest, accumulated);
            accumulated = Matrix4x4.CreateRotationY(perJoint) * accumulated;
            bent[bone] = Matrix4x4.CreateTranslation(-restPosition)
                * accumulated
                * Matrix4x4.CreateTranslation(jointPosition);
            previousRest = restPosition;
        }
        return bent;
    }

    internal byte[] CaptureMixed(bool gpuSkinning, bool outlined) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            DrawBody(scene, _bentPose!, PrimaryWorld);
            DrawRigidBody(scene, HeldWorld);
            DrawDissolvedBody(scene, _bentPose!, DissolveWorld);
            DrawCutoutBody(scene, MixedCutoutWorld);

            if (outlined)
            {
                MeshOutlineGroup mixed = scene.BeginMeshOutline(Rim, 2f);
                scene.DrawSkinnedOutline(mixed, _tubeHandle, _bentPose!, PrimaryWorld);
                scene.DrawMeshOutline(mixed, _boxHandle, HeldWorld);

                MeshOutlineGroup dissolved = scene.BeginMeshOutline(Rim, 2f);
                scene.DrawSkinnedOutlineDissolved(
                    dissolved, _tubeHandle, _bentPose!, DissolveWorld, Dissolve, dissolveComplement: false);

                MeshOutlineGroup cutout = scene.BeginMeshOutline(Rim, 2f);
                scene.DrawSkinnedOutline(cutout, _cutoutHandle, _cutoutPose!, MixedCutoutWorld);
            }

            DrawWall(scene);
        });

    internal byte[] CaptureMixedPart(bool gpuSkinning, MixedPartCase partCase) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            if (partCase == MixedPartCase.SkinnedBody) DrawBody(scene, _bentPose!, PrimaryWorld);
            else DrawRigidBody(scene, HeldWorld);
        });

    internal byte[] CapturePose(bool gpuSkinning, PoseCase poseCase) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            switch (poseCase)
            {
                case PoseCase.RestBody:
                    DrawBody(scene, _tube!.RestPose, PrimaryWorld);
                    break;
                case PoseCase.BentBody:
                    DrawBody(scene, _bentPose!, PrimaryWorld);
                    break;
                case PoseCase.BentOutlineOnly:
                    DrawOutline(scene, _bentPose!, PrimaryWorld);
                    break;
                case PoseCase.RestBodyBentOutline:
                    DrawBody(scene, _tube!.RestPose, PrimaryWorld);
                    DrawOutline(scene, _bentPose!, PrimaryWorld);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(poseCase));
            }
        });

    internal byte[] CaptureOcclusion(bool gpuSkinning, bool outlined) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            DrawBody(scene, _bentPose!, PrimaryWorld);
            if (outlined) DrawOutline(scene, _bentPose!, PrimaryWorld);
            DrawWall(scene);
        });

    internal byte[] CaptureDissolve(bool gpuSkinning, DissolveCase dissolveCase) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            switch (dissolveCase)
            {
                case DissolveCase.SolidBaseline:
                    DrawBody(scene, _bentPose!, DissolveWorld);
                    break;
                case DissolveCase.PartialBody:
                    DrawDissolvedBody(scene, _bentPose!, DissolveWorld);
                    break;
                case DissolveCase.PartialSceneDepthOutline:
                    DrawDissolvedBody(scene, _bentPose!, DissolveWorld);
                    DrawDissolvedOutline(scene, _bentPose!, DissolveWorld, MeshOutlineOcclusion.SceneDepth);
                    break;
                case DissolveCase.PartialThroughGeometryOutline:
                    DrawDissolvedBody(scene, _bentPose!, DissolveWorld);
                    DrawDissolvedOutline(scene, _bentPose!, DissolveWorld, MeshOutlineOcclusion.None);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(dissolveCase));
            }
        });

    internal byte[] CaptureSolidDissolveOutline(bool gpuSkinning) =>
        Capture(gpuSkinning, cutoutView: false, scene =>
        {
            DrawBody(scene, _bentPose!, DissolveWorld);
            DrawOutline(scene, _bentPose!, DissolveWorld);
        });

    internal byte[] CaptureCutout(bool gpuSkinning, bool outlined) =>
        Capture(gpuSkinning, cutoutView: true, scene =>
        {
            DrawCutoutBody(scene, Matrix4x4.Identity);
            if (outlined)
                scene.DrawSkinnedOutline(_cutoutHandle, _cutoutPose!, Matrix4x4.Identity, Rim, 2f);
        });

    internal byte[] CaptureBackground(bool gpuSkinning) =>
        Capture(gpuSkinning, cutoutView: false, _ => { });

    internal static bool IsBody(byte[] pixels, int pixel)
    {
        int i = pixel * 4;
        return pixels[i + 1] > 100
            && pixels[i + 1] > pixels[i] * 2
            && pixels[i + 1] > pixels[i + 2] * 2;
    }

    internal static bool IsRim(byte[] pixels, int pixel)
    {
        int i = pixel * 4;
        return pixels[i] > 140
            && pixels[i] > pixels[i + 1] * 2
            && pixels[i] > pixels[i + 2] * 2;
    }

    internal static bool IsWall(byte[] pixels, int pixel)
    {
        int i = pixel * 4;
        return pixels[i + 2] > 100
            && pixels[i + 2] > pixels[i] * 2
            && pixels[i + 2] > pixels[i + 1] * 2;
    }

    internal static bool SamePixel(byte[] left, byte[] right, int pixel)
    {
        int i = pixel * 4;
        return left[i] == right[i]
            && left[i + 1] == right[i + 1]
            && left[i + 2] == right[i + 2]
            && left[i + 3] == right[i + 3];
    }

    internal static int CountRim(byte[] pixels)
    {
        int count = 0;
        for (int pixel = 0; pixel < pixels.Length / 4; pixel++)
            if (IsRim(pixels, pixel)) count++;
        return count;
    }

    internal static bool HasExclusiveBodyNear(
        byte[] included,
        byte[] excluded,
        int x,
        int y,
        int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || px >= W || py < 0 || py >= H) continue;
                int pixel = py * W + px;
                if (IsBody(included, pixel) && !IsBody(excluded, pixel)) return true;
            }
        return false;
    }

    internal static bool HasBodyNear(byte[] pixels, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || px >= W || py < 0 || py >= H) continue;
                if (IsBody(pixels, py * W + px)) return true;
            }
        return false;
    }

    internal static bool HasRimNear(byte[] pixels, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || px >= W || py < 0 || py >= H) continue;
                if (IsRim(pixels, py * W + px)) return true;
            }
        return false;
    }

    internal static bool IsErodedBody(byte[] pixels, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || px >= W || py < 0 || py >= H) return false;
                if (!IsBody(pixels, py * W + px)) return false;
            }
        return true;
    }

    byte[] Capture(bool gpuSkinning, bool cutoutView, Action<Scene3D> draw)
    {
        EnsureScene();
        Scene3D scene = _preview!.Scene;
        if (cutoutView) ConfigureCutoutCamera(scene);
        else ConfigureDefaultCamera(scene);
        scene.UseGpuSkinning = gpuSkinning;
        scene.EffectTimeSeconds = 0f;
        _preview.Capture(draw);
        return _preview.ReadbackRgba();
    }

    void EnsureScene()
    {
        if (_gpu is not null) return;

        _gpu = GpuDeviceContext.CreateHeadless();
        _preview = new Render3DPreview(_gpu.GpuDevice, W, H);
        Scene3D scene = _preview.Scene;
        ConfigureScene(scene);

        _tube = Tube();
        _tubeHandle = scene.LoadSkinnedMesh(_tube);
        _bentPose = BentPose(_tube);
        _boxHandle = scene.LoadMesh(MeshPrimitives.Box(1f));

        byte[] cutoutPixels = CutoutPixels();
        Scene3D.TextureHandle cutoutTexture = scene.LoadTexture(cutoutPixels, 8, 8, TextureMipPolicy.None);
        SkinnedGltfMesh cutout = CutoutQuad();
        _cutoutHandle = scene.LoadSkinnedMesh(
            cutout, new Scene3D.SurfaceMaps(cutoutTexture, alphaCutoff: 0.5f));
        _cutoutPose = cutout.RestPose;
    }

    static void ConfigureScene(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.TransparentBackground = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.BackgroundColor = Background;
        scene.Post.Hdr.Enabled = false;
        scene.Post.CelBands = 0;
        scene.EffectTimeSeconds = 0f;
    }

    static void ConfigureDefaultCamera(Scene3D scene)
    {
        scene.CameraOverride = null;
        scene.Camera.Azimuth = MathF.PI / 4f;
        scene.Camera.Elevation = 0.55f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.Zoom = 1f;
        scene.Camera.Frame(new Vector3(0f, 0.55f, -0.3f), new Vector3(7.5f, 4.8f, 5.5f), 1.05f);
    }

    static void ConfigureCutoutCamera(Scene3D scene)
    {
        scene.CameraOverride = null;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 0.001f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.Zoom = 1f;
        scene.Camera.Frame(Vector3.Zero, new Vector3(3.2f, 3.2f, 0.2f), 1f);
    }

    void DrawBody(Scene3D scene, ReadOnlySpan<Matrix4x4> pose, Matrix4x4 world) =>
        scene.DrawSkinned(_tubeHandle, pose, world, Body, Material.Glowing(Body));

    void DrawDissolvedBody(Scene3D scene, ReadOnlySpan<Matrix4x4> pose, Matrix4x4 world) =>
        scene.DrawSkinned(
            _tubeHandle, pose, world, Body, Material.Glowing(Body), Dissolve, 0f, default);

    void DrawRigidBody(Scene3D scene, Matrix4x4 world) =>
        scene.Draw(_boxHandle, world, Body, Material.Glowing(Body));

    void DrawCutoutBody(Scene3D scene, Matrix4x4 world) =>
        scene.DrawSkinned(_cutoutHandle, _cutoutPose!, world, Body, Material.Glowing(Body));

    void DrawWall(Scene3D scene) =>
        scene.Draw(_boxHandle, OcclusionWallWorld, Wall, Material.Glowing(Wall));

    void DrawOutline(Scene3D scene, ReadOnlySpan<Matrix4x4> pose, Matrix4x4 world) =>
        scene.DrawSkinnedOutline(_tubeHandle, pose, world, Rim, 2f);

    void DrawDissolvedOutline(
        Scene3D scene,
        ReadOnlySpan<Matrix4x4> pose,
        Matrix4x4 world,
        MeshOutlineOcclusion occlusion)
    {
        MeshOutlineGroup group = scene.BeginMeshOutline(Rim, 2f, occlusion);
        scene.DrawSkinnedOutlineDissolved(group, _tubeHandle, pose, world, Dissolve, dissolveComplement: false);
    }

    static SkinnedGltfMesh CutoutQuad()
    {
        Matrix4x4[] bones = [Matrix4x4.Identity];
        Vector3 normal = Vector3.UnitZ;
        Vector4 weights = Vector4.UnitX;
        Vector4 tangent = new(1f, 0f, 0f, 1f);
        SkinnedVertex[] vertices =
        [
            new() { Position = new Vector3(-1f, -1f, 0f), Normal = normal, Color = Vector4.One,
                Uv = new Vector2(0f, 1f), BoneWeights = weights, Tangent = tangent },
            new() { Position = new Vector3(1f, -1f, 0f), Normal = normal, Color = Vector4.One,
                Uv = new Vector2(1f, 1f), BoneWeights = weights, Tangent = tangent },
            new() { Position = new Vector3(1f, 1f, 0f), Normal = normal, Color = Vector4.One,
                Uv = new Vector2(1f, 0f), BoneWeights = weights, Tangent = tangent },
            new() { Position = new Vector3(-1f, 1f, 0f), Normal = normal, Color = Vector4.One,
                Uv = new Vector2(0f, 0f), BoneWeights = weights, Tangent = tangent },
        ];
        return new SkinnedGltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 }, bones, bones);
    }

    static byte[] CutoutPixels()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = x >= 2 && x <= 5 && y >= 2 && y <= 5 ? (byte)0 : (byte)255;
            }
        return pixels;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _gpu?.Dispose();
    }
}
