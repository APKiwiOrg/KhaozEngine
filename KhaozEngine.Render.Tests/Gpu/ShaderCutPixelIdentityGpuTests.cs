using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class ShaderCutPixelIdentityGpuTests
{
    const int Width = 128, Height = 96, Frames = 3;

    // #1677. A wrong cached slot or companion must change a real attachment, including temporal history.
    [GpuFact]
    public void ShaderCutsPreserveEveryMainAttachmentAndTemporalHistoryBit()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        int restored = 0;
        using var baseline = new ShaderOverrideGpuDevice(context.GpuDevice, source =>
        {
            string old = ShaderCutBaseline.RestoreTileSamples(source);
            if (old != source) restored++;
            return old;
        });
        byte[][] expected = Render(baseline);
        byte[][] actual = Render(context.GpuDevice);
        byte[][] control = Render(context.GpuDevice, changeMaterial: true);
        Assert.True(restored > 0, "The baseline must compile the old tile-ground arithmetic.");
        Assert.Equal(Frames * 9, expected.Length);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
        Assert.False(actual[0].SequenceEqual(control[0]), "The repeated-slot material must contribute HDR pixels.");
    }

    internal static byte[][] Render(IGpuDevice device, bool changeMaterial = false,
        Func<GltfMesh, Func<MeshHandle>, MeshHandle>? upload = null,
        (GltfMesh Mesh, Matrix4x4 Transform)[]? geometry = null)
    {
        using var display = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, display);
        using var scene = new Scene3D(device, target.Outputs,
            new ShadowSettings { ShadowMapResolution = 256, ShadowCascadeCount = 2 }, requestLitVisibility: true);
        scene.ForceTemporalForTests = true;
        scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Hdr.Enabled = true;
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Background = BackgroundMode.Solid;
        scene.Post.Outline = false;
        scene.Post.CelBands = 0;
        scene.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
        scene.Post.FillLightColor = Color.Black;
        scene.Post.LightDirection = -Vector3.UnitY;
        scene.Post.LightColor = Color.White;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.4f;
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.OrthoSize = 10f;
        scene.Camera.Target = Vector3.Zero;
        TileGroundLayerImage first = Layer(changeMaterial ? (byte)35 : (byte)140, 80, 45);
        first.Steep = new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 10, ToDegrees = 70 };
        var material = scene.LoadTileGroundMaterial(4, 4, [first, Layer(40, 110, 65)], baseSpecStrength: 0f);
        // All roles active, a skipped first occurrence, and the hard-path one-hot fallback.
        MeshHandle[] quads = geometry is not null ? [] :
        [
            Ground(scene, material, upload, -4, -1.4f, Vector3.UnitY, new Vector4(0.2f), overlay: 1),
            Ground(scene, material, upload, -1.3f, 1.3f, Vector3.Normalize(new Vector3(0.5f, 1f, 0.3f)),
                new Vector4(0.0005f, 0.3f, 0.3f, 0.3f), overlay: 1),
            Ground(scene, material, upload, 1.4f, 4, Vector3.Normalize(new Vector3(0.9f, 0.5f, 0.4f)),
                new Vector4(0.2f), overlay: 1),
            Ground(scene, material, upload, -4, 4, Vector3.UnitY, Vector4.Zero, overlay: 0, z: 3),
        ];
        MeshHandle Load(GltfMesh mesh) => upload is null ? scene.LoadMesh(mesh, material)
            : upload(mesh, () => scene.LoadMesh(mesh, material));
        (MeshHandle Mesh, Matrix4x4 Transform)[] ground = geometry is null
            ? quads.Select(mesh => (mesh, Matrix4x4.Identity)).ToArray()
            : geometry.Select(item => (Load(item.Mesh), item.Transform)).ToArray();
        using var commands = device.Factory.CreateCommandList();
        var frames = new List<byte[]>();
        for (int frame = 0; frame < Frames; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = frame / 60f;
            if (geometry is not null)
            {
                if (frame == 2) scene.CameraCut();
                scene.Camera.Target = new Vector3(frame * .03f, 0, 0);
                scene.Post.Atmosphere.Enabled = frame == 2;
                scene.Post.Quality.Shadows.Mode = frame == 1 ? ShadowMode.ShadowMap : ShadowMode.Off;
            }
            foreach (var item in ground) scene.Draw(item.Mesh, item.Transform, Color.White,
                Material.None, castsShadows: geometry is not null);
            scene.PrepareFrame(Width, Height);
            using (GpuRecording.Open(device, commands, nameof(ShaderCutPixelIdentityGpuTests)))
                scene.RenderInternal(commands, Width, Height, target);
            device.Submit(commands);
            device.WaitForIdle();
            TemporalHistory history = scene.TemporalHistory;
            Assert.Equal((Width, Height, Width, Height),
                (history.InternalWidth, history.InternalHeight, history.DisplayWidth, history.DisplayHeight));
            frames.AddRange(new[]
            {
                scene.TemporalPostTargetsForTests!.OpaqueColor, scene.ResolvedNormalTarget, scene.ResolvedDepthTarget,
                scene.MotionTargetForTests!, scene.ResolvedVisibilityTarget!, display,
                history.Color(history.WriteIndex), history.Confidence(history.WriteIndex),
                history.PreviousDepth(history.WriteIndex),
            }.Select(texture => PointShadowZeroNdlGpuTests.Read(device, texture)));
            if (geometry is not null)
                frames.Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(scene.DebugReadShadowMap(out _, out _).AsSpan()).ToArray());
        }
        Assert.Contains(Enumerable.Range(0, Width * Height), pixel => BitConverter.ToSingle(frames[2], pixel * 4) < 1f);
        return frames.ToArray();
    }

    static MeshHandle Ground(Scene3D scene, Scene3D.TileGroundMaterialHandle material,
        Func<GltfMesh, Func<MeshHandle>, MeshHandle>? upload, float left, float right,
        Vector3 normal, Vector4 weights, float overlay, float z = -3)
    {
        // Corner slots (0, 0, 1, 0), brightness one and an optional overlay of slot zero.
        var tangent = new Vector4(1, 0, 1, overlay);
        ModelVertex[] vertices =
        [
            new(new(left, 0, z), normal, weights, Vector2.Zero, tangent),
            new(new(right, 0, z), normal, weights, Vector2.Zero, tangent),
            new(new(right, 0, z + 2.5f), normal, weights, Vector2.Zero, tangent),
            new(new(left, 0, z + 2.5f), normal, weights, Vector2.Zero, tangent),
        ];
        int[] order = [0, 1, 2, 0, 2, 3];
        var mesh = new GltfMesh(order.Select(index => vertices[index]).ToArray(), new ushort[] { 0, 1, 2, 3, 4, 5 });
        return upload is null ? scene.LoadMesh(mesh, material) : upload(mesh, () => scene.LoadMesh(mesh, material));
    }

    static TileGroundLayerImage Layer(byte r, byte g, byte b)
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(r + i % 7);
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return new() { AlbedoRgba = pixels, TilesPerMetre = 0.7f };
    }
}
