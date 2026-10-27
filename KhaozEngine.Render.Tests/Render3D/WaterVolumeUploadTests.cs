using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterVolumeUploadTests
{
    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly Scene3D Scene;
        internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList()) { CapturePayloads = true };
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal ModelRenderer Renderer => (ModelRenderer)typeof(Scene3D).GetField("_model",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Scene)!;
        internal IGpuBuffer Buffer => Assert.Single(Factory.Buffers, b => b.SizeInBytes == 652800);
        internal byte[] UploadedFrame
        {
            get
            {
                object buffer = typeof(ModelRenderer).GetField("_ubo", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Renderer)!;
                return Commands.Uploads.Last(u => ReferenceEquals(u.Buffer, buffer)).Data!;
            }
        }
        internal float Count => MemoryMarshal.Read<Vector4>(UploadedFrame.AsSpan(1328)).W;

        internal Host()
        {
            Scene = new Scene3D(Device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
            Scene.Post.Starfield = false;
            Scene.Post.Water.SwellAmplitude = 0f;
            Scene.RenderOrigin = Vector3.Zero;
        }

        internal void Render()
        {
            Scene.PrepareFrame(96, 64);
            using IGpuFramebuffer target = Factory.CreateFramebuffer(null, Scene.ResolvedDepthTarget);
            Scene.RenderInternal(Commands, 96, 64, target);
        }

        internal RecordingGpuCommandList.Upload Upload() =>
            Assert.Single(Commands.Uploads, u => ReferenceEquals(u.Buffer, Buffer));

        public void Dispose() { Commands.Dispose(); Scene.Dispose(); Device.Dispose(); }
    }

    [Fact]
    public void OptedInPlanesBecomeRenderRelativeRecords()
    {
        using var h = new Host();
        const float far = 4194304f;
        var origin = new Vector3(far, 128f, -far);
        h.Scene.RenderOrigin = origin;
        h.Scene.Camera.Target = origin;
        h.Scene.Post.Water.UnderwaterSunlight = true;
        h.Scene.Post.Water.AbsorptionPerMetre = new Color(0.2f, 0.3f, 0.4f);
        h.Scene.Post.Water.UnderwaterPenumbraPerMetre = 0.04f;
        WaterOpticsHandle field = h.Scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(far + 2f, 130f, -far + 3f, 1f, 2f, floorY: 126f));
        h.Scene.DrawWater(new WaterPlane(far - 2f, 132f, -far - 3f, 2f, look: new WaterLook
        {
            AbsorptionPerMetre = new Color(0.6f, 0.7f, 0.8f),
            UnderwaterPenumbraPerMetre = 0.09f,
            WaveSource = WaterWaveSource.FftOcean,
        }));
        h.Scene.DrawWater(new WaterPlane(far, 128f, -far, 2f,
            optics: new WaterOpticsBinding(field, origin - new Vector3(2f, 0f, 2f), new Vector2(4f)), floorY: 124f));
        h.Scene.DrawWater(new WaterPlane(far, 128f, -far, 2f,
            look: new WaterLook { UnderwaterSunlight = false }));

        h.Render();

        float[] words = MemoryMarshal.Cast<byte, float>(h.Upload().Data!.AsSpan()).ToArray();
        Assert.Equal(new[] { 1f, 1f, 3f, 5f, 2f, -2f, 0.2f, 0.3f, 0.4f, 0.04f, 0f, 0f },
            words.AsSpan(114048, 12).ToArray());
        Assert.Equal(new[] { -4f, -5f, 0f, -1f, 4f, float.MinValue, 0.6f, 0.7f, 0.8f, 0.09f, 0f, 0f },
            words.AsSpan(114060, 12).ToArray());
        Assert.Equal(new[] { -2f, -2f, 2f, 2f, 0f, -4f, 0.2f, 0.3f, 0.4f, 0.04f, 0f, 0f },
            words.AsSpan(114072, 12).ToArray());
        Assert.Equal(3f, h.Count);
        Assert.Equal(3, h.Scene.WaterVolumeClusters.RecordCount);
    }

    [Theory]
    [InlineData(false, false, 0f)]
    [InlineData(false, false, 4194304f)]
    [InlineData(true, false, 0f)]
    [InlineData(true, false, 4194304f)]
    [InlineData(false, true, 0f)]
    [InlineData(false, true, 4194304f)]
    public void ColumnOptInImpliesSunlightAndUploadsDerivedExtinction(bool bound, bool fft, float far)
    {
        using var h = new Host();
        var origin = new Vector3(far, far == 0f ? 0f : 128f, -far);
        h.Scene.RenderOrigin = origin;
        h.Scene.Camera.Target = origin;
        h.Scene.Post.Water.AbsorptionPerMetre = new Color(0.2f, 0.3f, 0.4f);
        h.Scene.Post.Water.UnderwaterPenumbraPerMetre = 0.04f;
        var column = new WaterLook
        {
            WaterColumnLight = true, UnderwaterSunlight = false,
            WaterColor = new Color(0.10f, 0.13f, 0.10f), Clarity = 2f,
            AbsorptionPerMetre = new Color(9f, 8f, 7f),
            WaveSource = fft ? WaterWaveSource.FftOcean : WaterWaveSource.Procedural,
        };
        WaterOpticsBinding optics = default;
        if (bound)
        {
            WaterOpticsHandle field = h.Scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            optics = new WaterOpticsBinding(field, origin + new Vector3(1f, 0f, 1f), new Vector2(2f, 4f));
        }
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(origin.X + 2f, origin.Y + 2f, origin.Z + 3f, 1f, 2f,
            column, optics, origin.Y - 2f));
        h.Scene.DrawWater(new WaterPlane(origin.X + 2f, origin.Y + 4f, origin.Z + 3f, 1f, 2f,
            look: new WaterLook { UnderwaterSunlight = true }));
        h.Scene.DrawWater(new WaterPlane(origin.X, origin.Y, origin.Z, 2f,
            look: new WaterLook { WaterColumnLight = false, UnderwaterSunlight = false }));

        h.Render();

        Assert.Equal(2f, h.Count);
        RecordingGpuCommandList.Upload upload = h.Upload();
        float[] words = MemoryMarshal.Cast<byte, float>(upload.Data!.AsSpan()).ToArray();
        Assert.InRange(MathF.Abs(words[114054] - 1.5075f), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(words[114055] - 1.485f), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(words[114056] - 1.5075f), 0f, 0.000001f);
        Assert.Equal(new[] { 0.2f, 0.3f, 0.4f }, words.AsSpan(114060 + 6, 3).ToArray());
        Assert.Equal(40, Marshal.SizeOf<WaterVolumeRecord>());
        Assert.Equal<uint>((114048 + 2 * 12) * 4, upload.Bytes);
        Assert.Equal(new[] { 1f, 1f, 3f, 5f, 2f, -2f }, words.AsSpan(114048, 6).ToArray());
        Assert.Equal(new[] { 0.04f, 0f, 0f }, words.AsSpan(114048 + 9, 3).ToArray());
        Assert.Equal(new[] { 1f, 1f, 3f, 5f, 4f, float.MinValue, 0.2f, 0.3f, 0.4f, 0.04f, 0f, 0f },
            words.AsSpan(114060, 12).ToArray());
        Assert.Equal(2, h.Scene.WaterVolumeClusters.RecordCount);
        Assert.False(h.Scene.Post.Water.UnderwaterSunlight);
        Assert.False(column.UnderwaterSunlight);

        int uploads = h.Commands.Uploads.Count;
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(origin.X, origin.Y, origin.Z, 2f));
        h.Render();
        Assert.Equal(0f, h.Count);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.RecordCount);
        Assert.DoesNotContain(h.Commands.Uploads.Skip(uploads), u => ReferenceEquals(u.Buffer, h.Buffer));
    }

    [Fact]
    public void MixedColumnRecordsDoNotLeakAcrossCulledOrEmptyFrames()
    {
        using var h = new Host();
        h.Scene.Post.Water.WaterColumnLight = true;
        h.Scene.Post.Water.WaterColor = new Color(0.10f, 0.13f, 0.10f);
        h.Scene.Post.Water.Clarity = 2f;
        h.Scene.Post.Water.AbsorptionPerMetre = new Color(0.2f, 0.3f, 0.4f);
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(1024f, 0f, 0f, 2f, look: new WaterLook
        {
            WaterColor = Color.White, Clarity = 1f,
        }));
        h.Scene.DrawWater(new WaterPlane(1024f, 0f, 0f, 2f, look: new WaterLook
        {
            WaterColumnLight = false, UnderwaterSunlight = false,
            WaterColor = new Color(float.NaN, float.NaN, float.NaN), Clarity = float.NaN,
        }));
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 2f, look: new WaterLook
        {
            WaterColumnLight = false, UnderwaterSunlight = true,
        }));
        h.Scene.DrawWater(new WaterPlane(0f, 1f, 0f, 2f));

        h.Render();

        Assert.Equal(WaterRenderer.PlaneRoute.Culled, h.Scene.WaterRouteForTests(0));
        Assert.Equal(WaterRenderer.PlaneRoute.Culled, h.Scene.WaterRouteForTests(1));
        Assert.Equal(3f, h.Count);
        float[] words = MemoryMarshal.Cast<byte, float>(h.Upload().Data!.AsSpan()).ToArray();
        Assert.Equal(new[] { 3f, 3f, 3f }, words.AsSpan(114048 + 6, 3).ToArray());
        Assert.Equal(new[] { 0.2f, 0.3f, 0.4f }, words.AsSpan(114060 + 6, 3).ToArray());
        Assert.InRange(MathF.Abs(words[114072 + 6] - 1.5075f), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(words[114072 + 7] - 1.485f), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(words[114072 + 8] - 1.5075f), 0f, 0.000001f);
        Assert.False(h.Scene.Post.Water.UnderwaterSunlight);

        int uploads = h.Commands.Uploads.Count;
        h.Scene.Begin();
        h.Render();
        Assert.Equal(0f, h.Count);
        Assert.Equal(default, h.Scene.WaterVolumeClusters);
        Assert.DoesNotContain(h.Commands.Uploads.Skip(uploads), u => ReferenceEquals(u.Buffer, h.Buffer));

        h.Scene.Post.Water.WaterColumnLight = false;
        h.Scene.Post.Water.UnderwaterSunlight = true;
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 2f));
        h.Render();
        Assert.Equal(1f, h.Count);
        RecordingGpuCommandList.Upload later = Assert.Single(h.Commands.Uploads.Skip(uploads),
            u => ReferenceEquals(u.Buffer, h.Buffer));
        float[] laterWords = MemoryMarshal.Cast<byte, float>(later.Data!.AsSpan()).ToArray();
        Assert.Equal(new[] { 0.2f, 0.3f, 0.4f }, laterWords.AsSpan(114048 + 6, 3).ToArray());
        Assert.Equal<uint>((114048 + 12) * 4, later.Bytes);
    }

    [Fact]
    public void LookCanOptInOverTheSceneDefault()
    {
        using var h = new Host();
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, look: new WaterLook { UnderwaterSunlight = true }));
        h.Render();
        Assert.Equal(1f, h.Count);
        Assert.Equal(1, h.Scene.WaterVolumeClusters.RecordCount);
        h.Upload();
    }

    [Fact]
    public void FrameWithoutOptedInWaterUploadsNothingAndCountsZero()
    {
        using var h = new Host();
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f));
        h.Render();
        Assert.DoesNotContain(h.Commands.Uploads, u => ReferenceEquals(u.Buffer, h.Buffer));
        Assert.Equal(0f, h.Count);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.RecordCount);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.DroppedRecords);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.ReferenceCount);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.OverflowedClusters);
    }

    [Fact]
    public void OptOutAfterAnOptedInFrameZeroesTheCount()
    {
        using var h = new Host();
        var look = new WaterLook { UnderwaterSunlight = true };
        var plane = new WaterPlane(0f, 0f, 0f, 5f, look: look);
        h.Scene.Begin();
        h.Scene.DrawWater(plane);
        h.Render();
        Assert.Equal(1f, h.Count);
        int uploads = h.Commands.Uploads.Count;
        look.UnderwaterSunlight = false;
        h.Scene.Begin();
        h.Scene.DrawWater(plane);
        h.Render();
        Assert.Equal(0f, h.Count);
        Assert.Equal(0, h.Scene.WaterVolumeClusters.RecordCount);
        Assert.DoesNotContain(h.Commands.Uploads.Skip(uploads), u => ReferenceEquals(u.Buffer, h.Buffer));
    }

    [Fact]
    public void WaterOnlyFrameStillPreparesTheClusterGrid()
    {
        using var h = new Host();
        h.Scene.Post.Water.UnderwaterSunlight = true;
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f));
        h.Render();
        Assert.True(MemoryMarshal.Read<Vector4>(h.UploadedFrame.AsSpan(1312)).W >= 0f);
        Assert.Equal(0, h.Scene.PointLightClusters.SubmittedLightCount);
        Assert.Equal(0f, MemoryMarshal.Read<ModelRenderer.FrameUbo>(h.UploadedFrame).Params.Y);
        Assert.True(h.Scene.WaterVolumeClusters.ReferenceCount > 0);
    }

    [Fact]
    public void LightAssignmentFailurePreservesTheValidWaterGrid()
    {
        using var h = new Host();
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1e-5f, 256f);
        ModelRenderer.PointLightData[] lights =
            [new() { PosRadius = new Vector4(0f, 0f, -1f, 2f), ColorIntensity = Vector4.One }];
        WaterVolumeRecord[] water = [new(-2f, -2f, 2f, 0f, 0f, -2f, Vector3.One, 0.03f)];
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(projection, Vector3.Zero, -Vector3.UnitZ, projection, Vector3.Zero));

        h.Renderer.BuildAndUploadLightingClusters(h.Commands, lights, water, projection, Vector3.Zero,
            -Vector3.UnitZ, projection, Vector3.Zero);
        h.Renderer.SetFrameUniforms(h.Commands, projection, Vector3.Zero, h.Scene.Post, lights);

        Assert.Equal(1, h.Renderer.WaterVolumeDiagnostics.RecordCount);
        Assert.True(h.Renderer.WaterVolumeDiagnostics.ReferenceCount > 0);
        h.Upload();
        Assert.Equal(grid.Depth, MemoryMarshal.Read<Vector4>(h.UploadedFrame.AsSpan(1312)));
        Assert.Equal(new Vector4(0f, 0f, -1f, 1f), MemoryMarshal.Read<Vector4>(h.UploadedFrame.AsSpan(1328)));
        Assert.Equal(1f, MemoryMarshal.Read<ModelRenderer.FrameUbo>(h.UploadedFrame).Params.Y);
        Assert.True(h.Renderer.PointLightClusterDiagnostics(lights.Length).UsesFullFallback);
        RecordingGpuCommandList.Upload lightUpload = Assert.Single(h.Commands.Uploads,
            u => ReferenceEquals(u.Buffer, h.Renderer.PointLightClusterBuffer));
        uint[] headers = MemoryMarshal.Cast<byte, uint>(lightUpload.Data!.AsSpan()).ToArray();
        Assert.Equal(3456, headers.Length);
        Assert.All(headers, header => Assert.Equal(255u, header));
    }

    [Fact]
    public void LightAssignmentFailureWithoutWaterKeepsTheLegacyFrameBytes()
    {
        using var h = new Host();
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 1e-5f, 256f);
        ModelRenderer.PointLightData[] lights =
            [new() { PosRadius = new Vector4(0f, 0f, -1f, 2f), ColorIntensity = Vector4.One }];

        h.Renderer.BuildAndUploadPointLightClusters(h.Commands, lights, projection, Vector3.Zero,
            -Vector3.UnitZ, projection, Vector3.Zero);
        h.Renderer.SetFrameUniforms(h.Commands, projection, Vector3.Zero, h.Scene.Post, lights);

        Vector4[] expected = [new(0f, 0f, 0f, -1f), Vector4.Zero];
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), h.UploadedFrame.AsSpan(1312, 32).ToArray());
        Assert.DoesNotContain(h.Commands.Uploads, u => ReferenceEquals(u.Buffer, h.Buffer));
    }

    [Fact]
    public void UsedPrefixUploadOnly()
    {
        using var h = new Host();
        h.Scene.Post.Water.UnderwaterSunlight = true;
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f));
        h.Render();
        RecordingGpuCommandList.Upload upload = h.Upload();
        Assert.Equal(0u, upload.Offset);
        Assert.Equal(456240u, upload.Bytes);
        Assert.True(upload.Bytes < h.Buffer.SizeInBytes);
    }
}
