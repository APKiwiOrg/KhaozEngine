using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public enum MotionPath { Translation, Rotation }

/// <summary>Static bright blades on a textured panel, with a texture-only lower control.</summary>
internal sealed class TemporalMotionClarityStage
{
    internal const int W = 240, H = 144, Warm = 80, Moving = 96, Stopped = 256;
    internal const float Ortho = 4.5f, Distance = 50f, PixelWorld = Ortho / H;
    MeshHandle _blades, _ground;
    bool _loaded;

    internal void Setup(Scene3D scene, AntiAliasing aa, TemporalUpscale preset, int factor, TemporalHistoryDetail detail)
    {
        scene.Post.UseSmoothPreset();
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = aa;
        scene.Post.Temporal = new TemporalSettings { Upscale = preset, HistoryDetail = detail };
        scene.Post.TransparentBackground = false;
        scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
        scene.Post.AmbientColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        scene.Post.MaxRenderWidth = W * factor;
        scene.Post.MaxRenderHeight = H * factor;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Azimuth = scene.Camera.Elevation = 0f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.OrthoSize = Ortho;
        scene.Camera.Target = Vector3.Zero;
        scene.Camera.Zoom = 1f;
        scene.Camera.Distance = Distance;
        if (_loaded) return;
        // Enlarge the panel without changing its two-display-pixel texture footprint. The fastest sweep must
        // never expose a panel edge in either measured region.
        _ground = scene.LoadMesh(new FrontStage(W, H, Ortho * 2).PanelMesh(1f),
            scene.LoadTexture(FrontStage.NoiseTexture(), FrontStage.TextureSize, FrontStage.TextureSize));
        _blades = scene.LoadMesh(Blades());
        _loaded = true;
    }

    internal void Draw(Scene3D scene, int frame, MotionPath path, float speed, bool settledStill = false)
    {
        // Both paths move the world origin by exactly speed display pixels each moving frame.
        // Rotation holds the eye fixed. Its angular increment compensates the sine projection.
        float travel = (settledStill ? Moving : Math.Clamp(frame - Warm, 0, Moving)) * speed * PixelWorld;
        float angle = path == MotionPath.Rotation ? MathF.Asin(travel / Distance) : 0f;
        scene.Camera.Azimuth = angle;
        scene.Camera.Target = path == MotionPath.Rotation
            ? new Vector3(-Distance * MathF.Sin(angle), 0f, Distance * (1f - MathF.Cos(angle)))
            : new Vector3(-travel, 0f, 0f);
        scene.EffectTimeSeconds = frame / 120f;
        scene.Draw(_ground, Matrix4x4.Identity);
        scene.Draw(_blades, Matrix4x4.Identity, new Color(0.98f, 0.98f, 0.98f, 1f));
    }

    static GltfMesh Blades()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<ushort>();
        for (int i = -100; i <= 100; i++)
        {
            float x = i * 8.3f * PixelWorld;
            float width = ((i % 3 + 3) % 3) switch { 0 => 0.6f, 1 => 1.2f, _ => 2.4f };
            float low = 3f * PixelWorld, high = 90f * PixelWorld;
            float tilt = 0.23f;
            Vector3 A(float y, float side) => new(x + tilt * y + side * width * PixelWorld / 2, y, -1.9f);
            ushort first = (ushort)vertices.Count;
            foreach (Vector3 p in new[] { A(low, -1), A(low, 1), A(high, 1), A(high, -1) })
                vertices.Add(new ModelVertex(p, Vector3.UnitZ, Vector4.One, Vector2.Zero));
            foreach (int offset in new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 })
                indices.Add((ushort)(first + offset));
        }
        return new GltfMesh(vertices.ToArray(), indices.ToArray());
    }
}
