using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A lazy shared real-device scene for the coarse-texel shadow measurement. No window or clock.</summary>
public sealed class GameShadowGrainScene : IDisposable
{
    internal const int W = 384, H = 256, Warm = 80, Count = 16, Anchor = Warm + Count / 2;
    internal const float Ortho = 0.56f, Pixel = Ortho / H, Radius = 0.23f, WallZ = -1.95f;
    const float Reach = 12f, Tilt = 15f * MathF.PI / 180f;
    const int Sides = 16;
    TemporalFixture? _fixture;
    MeshHandle _torso, _box;
    long _begins;

    internal sealed record Capture(byte[][] Frames, bool[] Refits);

    internal string Backend => Fixture.Device.Backend.ToString();

    TemporalFixture Fixture
    {
        get
        {
            if (_fixture != null) return _fixture;
            _fixture = new TemporalFixture(W, H);
            _torso = _fixture.Scene.LoadMesh(Torso());
            _box = _fixture.Scene.LoadMesh(MeshPrimitives.Box(1f));
            return _fixture;
        }
    }

    internal static float Sway(int n, bool sway) => sway ? 2f * MathF.Sin(0.1f * n) * Pixel : 0f;
    internal static float Tangent(int n, float rate) =>
        1f + (n - Anchor) * rate * Pixel / (Reach * MathF.Cos(Tilt));
    internal static Vector3 Light(int n, float rate) => Vector3.Normalize(new Vector3(0f, -Tangent(n, rate), -1f));

    // A 16-sided vertical prism. Each face owns four vertices and one constant normal and colour.
    static GltfMesh Torso()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<ushort>();
        for (int i = 0; i < Sides; i++)
        {
            float a = (i - 0.5f) * MathF.Tau / Sides, b = (i + 0.5f) * MathF.Tau / Sides;
            var normal = new Vector3(MathF.Sin((a + b) / 2f), 0f, MathF.Cos((a + b) / 2f));
            var colour = new Vector4(0.63f, 0.39f, 0.27f, 1f);
            ushort start = (ushort)vertices.Count;
            foreach ((float angle, float y) in new[] { (a, -0.4f), (b, -0.4f), (b, 0.4f), (a, 0.4f) })
                vertices.Add(new ModelVertex(new Vector3(Radius * MathF.Sin(angle), y,
                    WallZ - Radius * MathF.Cos(MathF.PI / Sides) + Radius * MathF.Cos(angle)),
                    normal, colour, Vector2.Zero));
            foreach (int offset in new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }) indices.Add((ushort)(start + offset));
        }
        return new GltfMesh(vertices.ToArray(), indices.ToArray());
    }

    static void Setup(Scene3D s, AntiAliasing aa, TemporalUpscale preset, float hold, int factor)
    {
        s.Post.UseSmoothPreset();
        s.Post.RenderScale = RenderScale.MatchViewport;
        s.Post.Quality.AntiAliasing = aa;
        s.Post.Temporal.Upscale = preset;
        s.Post.Temporal.Sharpness = 0.25f;
        s.Post.MaxRenderWidth = W * factor;
        s.Post.MaxRenderHeight = H * factor;
        s.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
        s.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
        s.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        s.Post.Quality.Shadows.ShadowLightHoldTexels = hold;
        s.Camera.Azimuth = 0f;
        s.Camera.Elevation = 0f;
        s.Camera.Distance = 6f;
        s.Camera.OrthoSize = Ortho;
        s.Camera.Zoom = 1f;
        s.Camera.AspectRatio = (float)W / H;
        s.Camera.Target = Vector3.Zero;
        s.DebugView = SceneDebugView.None;
    }

    void Draw(Scene3D s, int n, float rate, bool sway)
    {
        s.Post.LightDirection = Light(n, rate);
        s.Draw(new RigidInstanceDraw(_torso, Matrix4x4.CreateTranslation(Sway(n, sway), 0f, 0f))
        {
            Motion = MotionKey.From(73),
        });
        s.Draw(_box, Matrix4x4.CreateScale(40f, 4f, 2f) * Matrix4x4.CreateTranslation(0f, 2f, -1f)
            * Matrix4x4.CreateRotationZ(Tilt) * Matrix4x4.CreateTranslation(0f, Reach, WallZ + Reach),
            new Color(0.3f, 0.3f, 0.3f, 1f));
    }

    /// <summary>Supersampled spatial reference, live light and no temporal resolve, box filtered on the CPU.
    /// All captures reuse one scene, with an off frame resetting held light and history, and aligned jitter phases.</summary>
    internal Capture Run(float rate, float hold, AntiAliasing aa, TemporalUpscale preset, bool sway,
        bool reference = false, bool releaseMask = false)
    {
        TemporalFixture fx = Fixture;
        const int ReferenceFactor = 4;
        int factor = reference ? ReferenceFactor : 1;
        if (fx.DisplayWidth != W * factor) fx.Resize(W * factor, H * factor);
        Setup(fx.Scene, AntiAliasing.Off, TemporalUpscale.Native, 0f, factor);
        fx.Frame((s, _) => Draw(s, 0, rate, sway));
        _begins++;
        Setup(fx.Scene, reference ? AntiAliasing.Off : aa, preset, reference ? 0f : hold, factor);
        fx.Scene.CameraCut();
        int phases = TemporalJitter.PhaseCount(preset == TemporalUpscale.Quality ? 1.5f : 1f);
        int pad = (int)((phases - _begins % phases) % phases);
        fx.SkipFrames(pad);
        _begins += pad;
        if (releaseMask) fx.Scene.DebugView = SceneDebugView.Reactive;
        if (!reference)
            for (int n = 0; n < Warm; n++)
            {
                int frame = n;
                fx.Frames(1, (s, _) => Draw(s, frame, rate, sway));
                _begins++;
            }
        var frames = new byte[Count][];
        var refits = new bool[Count];
        for (int i = 0; i < Count; i++)
        {
            int frame = Warm + i;
            byte[] rgba = fx.Frame((s, _) => Draw(s, frame, rate, sway));
            _begins++;
            frames[i] = reference ? Rgba8Stats.BoxDownsample(rgba, W * factor, H * factor, factor) : rgba;
            refits[i] = fx.Scene.LastShadowPassDiagnostics.LightMatrixChanged;
        }
        return new Capture(frames, refits);
    }

    // Receiver interior, excluding silhouette and a four-pixel guard at every faceted normal seam.
    internal static bool Receiver(int frame, int x, bool sway)
    {
        float wx = (x + 0.5f - W / 2f) * Pixel - Sway(frame, sway);
        if (MathF.Abs(wx) > 0.17f) return false;
        for (int i = -4; i <= 4; i++)
            if (MathF.Abs(wx - Radius * MathF.Sin((i + 0.5f) * MathF.Tau / Sides)) < 4f * Pixel) return false;
        return true;
    }

    /// <summary>The default cascade fit, calculated with the same slice sphere and light as the renderer.</summary>
    internal static (int Cascade, float TexelPixels, float HoldPixels) Geometry()
    {
        var camera = new IsoCamera3D
        {
            Azimuth = 0f, Elevation = 0f, Distance = 6f, Target = Vector3.Zero,
            OrthoSize = Ortho, Zoom = 1f, AspectRatio = (float)W / H,
        };
        var defaults = new ShadowSettings();
        Span<Vector3> corners = stackalloc Vector3[8];
        if (!ShadowMapMath.FrustumCornersWorld(camera.ViewProjection, corners))
            throw new InvalidOperationException("The measurement camera has no frustum.");
        Vector3 near = (corners[0] + corners[1] + corners[2] + corners[3]) / 4f;
        Vector3 far = (corners[4] + corners[5] + corners[6] + corners[7]) / 4f;
        float camNear = Vector3.Dot(near - camera.Eye, camera.Forward);
        float camFar = Vector3.Dot(far - camera.Eye, camera.Forward);
        ShadowMapMath.SliceBoundingSphere(corners, 0f, (defaults.ShadowNearDistance - camNear) / (camFar - camNear),
            out Vector3 centre, out float radius);
        Matrix4x4 matrix = ShadowMapMath.BuildLightViewProj(Light(Anchor, 1f), centre, radius,
            defaults.ShadowMapResolution);
        int selected = ShadowMapMath.SelectCascade(new[] { matrix }, 1, new Vector3(0f, 0f, WallZ));
        float texel = ShadowMapMath.TexelWorldSize(radius, defaults.ShadowMapResolution);
        float threshold = ShadowLightHold.ThresholdRadians(radius, defaults.ShadowMapResolution,
            ShadowLightHold.SinElevation(Light(Anchor, 1f)), defaults.ShadowLightHoldCasterHeight, 1f);
        // Light-space V foreshortens on the front face. Report its projected row pitch, not world texel size.
        float projectedRow = texel / (Pixel * MathF.Abs(Light(Anchor, 1f).Z));
        return (selected, projectedRow, threshold * 2f * Reach * MathF.Cos(Tilt) / Pixel);
    }

    public void Dispose() => _fixture?.Dispose();
}
