using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A still receiver and caster from the shadow-edge rig, under a constant angular daylight rate.</summary>
internal sealed class DaylightShadowStage(bool ridges, bool moving = true)
{
    internal const int W = 320, H = 180, Warm = 80, Count = 129;
    internal const float DaylightDegreesPerFrame = 0.2f / 60f;
    readonly ShadowEdgeStage _edge = new(W, H, 0f, Warm);
    MeshHandle _receiver;
    internal static readonly Vector2 Normal = new(-MathF.Sin(Tilt), -MathF.Cos(Tilt));
    internal static readonly Vector2 Along = new(MathF.Cos(Tilt), -MathF.Sin(Tilt));
    const float Tilt = ShadowEdgeStage.TiltDegrees * MathF.PI / 180f;

    internal void Setup(Scene3D scene, AntiAliasing aa, TemporalUpscale preset, float hold)
    {
        _edge.Setup(scene, aa, preset, hold);
        scene.Post.AmbientColor = new Color(0.03f, 0.03f, 0.03f, 1f);
        if (ridges)
            _receiver = scene.LoadMesh(_edge.Stage.PanelMesh(0.8f), scene.LoadTexture(
                TexturedShadowGrainCase.RidgeTexture(), FrontStage.TextureSize, FrontStage.TextureSize));
    }

    internal object Geometry()
    {
        IsoCamera3D camera = _edge.Camera();
        Span<Vector3> corners = stackalloc Vector3[8];
        if (!ShadowMapMath.FrustumCornersWorld(camera.ViewProjection, corners))
            throw new InvalidOperationException("The receiver camera has no frustum.");
        float near = Vector3.Dot((corners[0] + corners[1] + corners[2] + corners[3]) / 4 - camera.Eye, camera.Forward);
        float far = Vector3.Dot((corners[4] + corners[5] + corners[6] + corners[7]) / 4 - camera.Eye, camera.Forward);
        ShadowMapMath.SliceBoundingSphere(corners, 0, (16 - near) / (far - near), out Vector3 centre, out float radius);
        Vector3 light = Vector3.Normalize(new Vector3(0, -1, -1));
        Matrix4x4 matrix = ShadowMapMath.BuildLightViewProj(light, centre, radius, 2048);
        int selected = ShadowMapMath.SelectCascade(new[] { matrix }, 1, new Vector3(0, 0, ShadowEdgeStage.WallZ));
        float texel = ShadowMapMath.TexelWorldSize(radius, 2048);
        float threshold = ShadowLightHold.ThresholdRadians(radius, 2048, MathF.Abs(light.Y), 12, 1);
        return new { Cascade = selected, Resolution = 2048, Radius = radius,
            ProjectedTexelPixels = texel / (_edge.Stage.PixelWorld * MathF.Abs(light.Z)),
            HoldStepPixels = threshold * 24 * MathF.Cos(Tilt) / _edge.Stage.PixelWorld,
            PredictedInterval = threshold / (DaylightDegreesPerFrame * MathF.PI / 180) };
    }

    internal void Draw(Scene3D scene, int n)
    {
        float angle = MathF.PI / 4f + (moving ? n - Warm : 0) * DaylightDegreesPerFrame * MathF.PI / 180f;
        scene.Post.LightDirection = new Vector3(0f, -MathF.Sin(angle), -MathF.Cos(angle));
        if (ridges) scene.Draw(_receiver, Matrix4x4.Identity);
        else _edge.Stage.Wall(scene);
        _edge.DrawCaster(scene);
    }
}
