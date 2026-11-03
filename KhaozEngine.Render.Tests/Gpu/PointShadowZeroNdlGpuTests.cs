using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class PointShadowZeroNdlGpuTests
{
    const int W = 128, H = 96, Bands = 4;
    static readonly Vector3 Lamp = new(0f, 0.6f, 0f);

    // #1676. Compare real Render3D attachments against the ungated arithmetic, not a reimplemented light equation.
    [GpuFact]
    public void BandedZeroNdlKeepsEveryMainAttachmentBitIdentical()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        bool restored = false;
        using var ungated = new ShaderOverrideGpuDevice(context.GpuDevice, source =>
        {
            const string gate = "if (att > 0.0 && ndl > 0.0 && pointLight.ShadowParams.x >= 0.0)";
            if (!source.Contains(gate, StringComparison.Ordinal)) return source;
            restored = true;
            return source.Replace(gate, "if (att > 0.0 && pointLight.ShadowParams.x >= 0.0)", StringComparison.Ordinal);
        });
        byte[][] expected = Render(ungated, true, out int zero, out int contributing);
        byte[][] actual = Render(context.GpuDevice, true, out _, out _);
        byte[][] control = Render(context.GpuDevice, false, out _, out _);
        Assert.True(restored, "The control must restore the old filter condition.");
        Assert.True(zero > 0, "Receivers inside a shadowed light must reach banded NdotL exactly zero.");
        Assert.True(contributing > 0, "The same light must also contribute to visible receivers.");
        Assert.Equal(7, expected.Length); // Six main attachments and the sampled cascade colour atlas.
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
        Assert.False(expected[0].SequenceEqual(control[0]), "Removing the casters must change HDR bytes.");
    }

    internal static byte[][] Render(IGpuDevice device, bool casters, out int zero, out int contributing)
    {
        using var display = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(W, H,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, display);
        using var scene = new Scene3D(device, target.Outputs,
            new ShadowSettings { ShadowCascadeCount = 2, ShadowMapResolution = 128 }, requestLitVisibility: true);
        scene.ForceTemporalForTests = true;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Hdr.Enabled = true;
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.Outline = false;
        scene.Post.CelBands = Bands;
        scene.Post.AmbientColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        scene.Post.FillLightColor = Color.Black;
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f));
        scene.Post.LightColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        scene.Post.Quality.Shadows.PointShadows.Filter = PointShadowFilter.Soft;
        scene.Camera.Azimuth = 0.4f;
        scene.Camera.Elevation = 1.1f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.Target = Vector3.Zero;
        scene.Camera.OrthoSize = 12f;
        MeshHandle plane = scene.LoadMesh(MeshPrimitives.Plane());
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box());
        using var commands = device.Factory.CreateCommandList();
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = frame / 60f;
            scene.Draw(plane, Matrix4x4.CreateScale(24f, 1f, 24f), new Color(0.7f, 0.7f, 0.7f, 1f), Material.Shiny(0.5f, 16f), castsShadows: false);
            if (casters) scene.Draw(box, Matrix4x4.CreateScale(1f, 2f, 1f) * Matrix4x4.CreateTranslation(2f + frame * 0.1f, 1f, 0f), Color.White);
            scene.AddLight(Lamp, new Color(1f, 0.8f, 0.6f, 1f), 9f, 3f, LightShadow.Static(1));
            scene.PrepareFrame(W, H);
            using (GpuRecording.Open(device, commands, nameof(PointShadowZeroNdlGpuTests))) scene.RenderInternal(commands, W, H, target);
            device.Submit(commands);
            device.WaitForIdle();
        }
        Assert.True(scene.LastShadowPassDiagnostics.PointShadowedLights > 0);
        byte[] normal = Read(device, scene.ResolvedNormalTarget);
        byte[] depth = Read(device, scene.ResolvedDepthTarget);
        Assert.True(Matrix4x4.Invert(scene.CurrentFrameView.ViewProjection, out Matrix4x4 inverse));
        zero = contributing = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int at = (y * W + x) * 4;
                float z = BitConverter.ToSingle(depth, at);
                if (z >= 1f) continue;
                Vector3 n = Vector3.Normalize(new Vector3(normal[at], normal[at + 1], normal[at + 2]) / 255f * 2f - Vector3.One);
                Vector4 world = Vector4.Transform(new Vector4((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, z, 1f), inverse);
                Vector3 toLight = Lamp - new Vector3(world.X, world.Y, world.Z) / world.W;
                if (toLight.Length() >= 9f) continue;
                if (MathF.Floor(MathF.Max(Vector3.Dot(n, Vector3.Normalize(toLight)), 0f) * Bands + 0.5f) == 0f) zero++;
                else contributing++;
            }
        float[] atlas = scene.DebugReadShadowMap(out _, out _);
        return [Read(device, scene.TemporalPostTargetsForTests!.OpaqueColor), normal, depth,
            Read(device, scene.MotionTargetForTests!), Read(device, scene.ResolvedVisibilityTarget!), Read(device, display),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(atlas.AsSpan()).ToArray()];
    }

    internal static unsafe byte[] Read(IGpuDevice device, IGpuTexture source)
    {
        int texel = source.Format switch { GpuPixelFormat.R16G16B16A16Float => 8, GpuPixelFormat.R8UNorm => 1, _ => 4 };
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(source.Width, source.Height, source.Format, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, nameof(Read))) commands.CopyTexture(source, staging);
        device.Submit(commands);
        device.WaitForIdle();
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            int row = checked((int)source.Width * texel);
            byte[] bytes = new byte[row * source.Height];
            for (int y = 0; y < source.Height; y++) new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, row).CopyTo(bytes.AsSpan(y * row));
            return bytes;
        }
        finally { device.Unmap(staging); }
    }
}
