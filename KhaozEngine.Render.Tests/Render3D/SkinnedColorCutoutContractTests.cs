using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedColorCutoutContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Skinned_colour_keeps_cutoff_independent_of_dissolve(bool gpu, bool dissolving)
    {
        using var harness = new Harness(gpu);
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.22f, 1.5f, 6, 4, 3, Axis.Y);
        Scene3D.TextureHandle albedo = harness.Scene.LoadTexture([255, 255, 255, 255], 1, 1);
        SkinnedMeshHandle handle = harness.Scene.LoadSkinnedMesh(
            mesh, new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f));
        harness.Scene.Begin();
        if (dissolving)
            harness.Scene.DrawSkinned(handle, mesh.RestPose, Matrix4x4.Identity, Color.White,
                Material.None, 0.65f, 0.14f, Color.White);
        else
            harness.Scene.DrawSkinned(handle, mesh.RestPose, Matrix4x4.Identity, Color.White);

        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        harness.Scene.PrepareFrame();
        harness.Scene.RenderInternal(commands, 32, 24, harness.Target);
        Vector2 expectedDissolve = dissolving ? new Vector2(0.65f, 0.14f) : Vector2.Zero;
        if (gpu)
        {
            RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads,
                item => item.Buffer.SizeInBytes == ModelRenderer.SkinnedMainSlotBytes * 8);
            ReadOnlySpan<Matrix4x4> header = MemoryMarshal.Cast<byte, Matrix4x4>(upload.Data!.AsSpan(0, 128));
            Assert.Equal(0.5f, header[1].M33);
            Assert.Equal(expectedDissolve, new Vector2(header[1].M43, header[1].M44));
        }
        else
        {
            RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads,
                item => item.Bytes == ModelRenderer.InstanceData.SizeInBytes);
            ModelRenderer.InstanceData instance = MemoryMarshal.Read<ModelRenderer.InstanceData>(upload.Data!);
            Assert.Equal(0.5f, instance.SpecParams.Z);
            Assert.Equal(expectedDissolve, instance.Dissolve);
        }
    }

    [Fact]
    public void Gpu_shadow_keeps_the_dissolve_threshold_when_colour_has_a_cutoff()
    {
        using var harness = new Harness(gpu: true, shadows: true);
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
        Scene3D.TextureHandle albedo = harness.Scene.LoadTexture([255, 255, 255, 255], 1, 1);
        SkinnedMeshHandle handle = harness.Scene.LoadSkinnedMesh(
            mesh, new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f));
        harness.Scene.Begin();
        harness.Scene.DrawSkinned(handle, mesh.RestPose, Matrix4x4.CreateTranslation(0f, 0.6f, 0f),
            Color.White, Material.None, 0.65f, 0.14f, Color.White);

        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        harness.Scene.PrepareFrame();
        harness.Scene.RenderInternal(commands, 32, 24, harness.Target);

        Assert.True(harness.Scene.LastShadowPassDiagnostics.Rendered);
        Assert.Contains(commands.Uploads.Where(upload => upload.Data is not null), upload =>
        {
            if (upload.Data!.Length < ShadowMapRenderer.SkinnedDissolveParamsOffset + 16) return false;
            Vector4 parameters = MemoryMarshal.Read<Vector4>(
                upload.Data.AsSpan(ShadowMapRenderer.SkinnedDissolveParamsOffset));
            return MathF.Abs(parameters.Y - 0.65f) < 1e-5f;
        });
    }

    [Fact]
    public void Dissolve_fragments_keep_cutoff_and_dissolve_in_separate_varyings()
    {
        for (int location = 0; location <= 9; location++)
        {
            Assert.Contains($"layout(location={location}) out ", ShaderSources.SkinnedModelVert);
            Assert.Contains($"layout(location={location}) in ", ShaderSources.ModelDissolveFrag);
            Assert.Contains($"layout(location={location}) in ", ShaderSources.SkinnedModelDissolveFrag);
        }
        Assert.Contains("layout(location=9) in vec2 vDissolve;", ShaderSources.ModelDissolveFrag);
        Assert.Contains("layout(location=9) out vec2 vDissolve;", ShaderSources.SkinnedModelVert);
        Assert.Contains("vDissolve = P[3].zw;", ShaderSources.SkinnedModelVert);
        Assert.Contains("layout(location=9) in vec2 vDissolve;", ShaderSources.SkinnedModelDissolveFrag);
        Assert.Contains("texRgba.a < vSpecParams.z", ShaderSources.ModelDissolveFrag);
        Assert.Contains("texRgba.a < vSpecParams.z", ShaderSources.SkinnedModelDissolveFrag);
        Assert.Contains("float threshold = clamp(vDissolve.x", ShaderSources.ModelDissolveFrag);
        Assert.Contains("float threshold = clamp(vDissolve.x", ShaderSources.SkinnedModelDissolveFrag);
        Assert.Contains("float edgeW = max(vDissolve.y", ShaderSources.ModelDissolveFrag);
        Assert.Contains("float edgeW = max(vDissolve.y", ShaderSources.SkinnedModelDissolveFrag);

        AssertCutoutBeforeDissolve(ShaderSources.ModelDissolveFrag);
        AssertCutoutBeforeDissolve(ShaderSources.SkinnedModelDissolveFrag);
    }

    [Fact]
    public void Fixed_gpu_header_sizes_do_not_change()
    {
        Assert.Equal(128u, ModelRenderer.SkinnedHeaderBytes);
        Assert.Equal(256u, ModelRenderer.SkinnedMainSlotBytes);
    }

    static void AssertCutoutBeforeDissolve(string source)
    {
        int alphaTest = source.IndexOf("texRgba.a < vSpecParams.z", StringComparison.Ordinal);
        int dissolveTest = source.IndexOf("mask < threshold", StringComparison.Ordinal);
        Assert.True(alphaTest >= 0 && dissolveTest > alphaTest,
            "Alpha cutoff must run before the dissolve discard.");
    }

    sealed class Harness : IDisposable
    {
        readonly IGpuTexture _targetTexture;
        public IGpuFramebuffer Target { get; }
        public Scene3D Scene { get; }

        public Harness(bool gpu, bool shadows = false)
        {
            var device = new FakeGpuDevice();
            _targetTexture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                32, 24, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Target = device.Factory.CreateFramebuffer(null, _targetTexture);
            Scene = new Scene3D(device, Target.Outputs);
            Scene.UseGpuSkinning = gpu;
            Scene.Post.Starfield = false;
            Scene.Post.Quality.Shadows.Mode = shadows ? ShadowMode.ShadowMap : ShadowMode.Off;
            Scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            Scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
        }

        public void Dispose()
        {
            Scene.Dispose();
            Target.Dispose();
            _targetTexture.Dispose();
        }
    }
}
