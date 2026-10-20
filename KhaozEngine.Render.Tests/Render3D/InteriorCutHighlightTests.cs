using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutHighlightTests
{
    [Fact]
    public void HighlightBindingsFollowTextureIdentityAndBorrowItsLifetime()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var outline = new TargetOutlineRenderer(device,
            new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        using var silhouette = new SilhouetteRenderer(device, new GpuOutputDescription(null,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R16G16B16A16Float));
        using var first = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        using var second = factory.CreateTexture(GpuTextureDescription.Texture2D(2, 2,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        outline.SetInteriorCut(first, default);
        silhouette.SetInteriorCut(first, default);
        FakeResourceSet[] borrowers = factory.ResourceSets.Where(set => set.Resources.Contains(first)).ToArray();
        Assert.Equal(2, borrowers.Length);
        int count = factory.ResourceSets.Count;
        outline.SetInteriorCut(first, default);
        silhouette.SetInteriorCut(first, default);
        Assert.Equal(count, factory.ResourceSets.Count);
        outline.SetInteriorCut(second, default);
        silhouette.SetInteriorCut(second, default);
        Assert.All(borrowers, set => Assert.True(set.Disposed));
        Assert.False(((FakeTexture)first).Disposed);
        Assert.Equal(count + 2, factory.ResourceSets.Count);
    }

    [Fact]
    public void HighlightPayloadsKeepOffsets()
    {
        using var device = new FakeGpuDevice();
        using var outline = new TargetOutlineRenderer(device,
            new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        using var silhouette = new SilhouetteRenderer(device, new GpuOutputDescription(null,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R16G16B16A16Float));
        using var vertices = device.Factory.CreateBuffer(new GpuBufferDescription(1024, GpuBufferUsage.VertexBuffer));
        using var indices = device.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.IndexBuffer));
        using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        var uniforms = new InteriorCutUniforms(new Vector4(3, 4, 5, 6), new Vector4(7, 8, 0, 0));
        outline.SetInteriorCut(texture, uniforms);
        silhouette.SetInteriorCut(texture, uniforms);
        var vp = Matrix4x4.CreateScale(2);
        var world = Matrix4x4.CreateTranslation(11, 12, 13);
        outline.EnsureCapacity(3);
        outline.BeginFrame();
        outline.BeginGroup(vp);
        outline.EnqueueRigid(vertices, indices, 3, GpuIndexFormat.UInt16, null, 0, world, .25f, .4f, true, new Vector3(10, 20, 30), true);
        outline.EnqueueSkinnedGpu(vertices, indices, 3, GpuIndexFormat.UInt16, null,
            new[] { Matrix4x4.Identity }, 1, world, .25f, .4f, true, new Vector3(10, 20, 30));
        outline.EnqueueSkinnedCpu(Array.Empty<SkinnedVertex>(), indices, 3, GpuIndexFormat.UInt16, null,
            new[] { Matrix4x4.Identity }, 2, world, .25f, .4f, true, new Vector3(10, 20, 30));
        byte[] image = HighlightTestAccess.Field<byte[]>(outline, "_drawImage");
        Assert.Equal(vp, Read<Matrix4x4>(image, 0));
        Assert.Equal(world, Read<Matrix4x4>(image, 64));
        Assert.Equal(new Vector4(.25f, .4f, 1, 1), Read<Vector4>(image, 128));
        Assert.Equal(new Vector4(10, 20, 30, 0), Read<Vector4>(image, 144));
        Assert.Equal(uniforms.Window, Read<Vector4>(image, 160));
        Assert.Equal(uniforms.Heights, Read<Vector4>(image, 176));
        foreach (int slot in new[] { 1, 2 })
        {
            Assert.Equal(new Vector4(.25f, .4f, 1, 0), Read<Vector4>(image, slot * 256 + 128));
            Assert.Equal(Vector4.Zero, Read<Vector4>(image, slot * 256 + 160));
            Assert.Equal(Vector4.Zero, Read<Vector4>(image, slot * 256 + 176));
        }
        silhouette.EnsureCapacity(1);
        silhouette.BeginFrame(vp);
        silhouette.Enqueue(vertices, vertices, indices, 3, GpuIndexFormat.UInt16, 0, world, Color.White, .05f, true);
        byte[] hull = HighlightTestAccess.Field<byte[]>(silhouette, "_image");
        Assert.Equal(vp, Read<Matrix4x4>(hull, 0));
        Assert.Equal(world, Read<Matrix4x4>(hull, 64));
        Assert.Equal(Color.White.ToVector4(), Read<Vector4>(hull, 128));
        Assert.Equal(new Vector4(.05f, 1, 0, 0), Read<Vector4>(hull, 144));
        Assert.Equal(uniforms.Window, Read<Vector4>(hull, 160));
        Assert.Equal(uniforms.Heights, Read<Vector4>(hull, 176));
        foreach (var (renderer, size, stride) in new (object, string, string)[]
        { (outline, "DrawPayloadBytes", "DrawSlotBytes"), (silhouette, "PayloadBytes", "SlotBytes") })
        {
            Assert.Equal(192, Constant(renderer, size));
            Assert.Equal(256, Constant(renderer, stride));
            Assert.Equal(192, Marshal.SizeOf(renderer.GetType().GetNestedType("DrawUbo", BindingFlags.NonPublic)!));
        }
    }

    [Fact]
    public void SkinnedOutlineStringsAndLayoutStayUncut()
    {
        string[] hashes = ["15d621e885b974b81bf3a1c0aae39072d7d7ced5385c8fc1f8e90d913ae5d7d7",
            "f9d5eca8d61be1ac33e3e3896e6c9dcea192e41f8fec23230f84b32e6abd3200"];
        string[] fragments = [ShaderSources.TargetOutlineFullMaskFrag, ShaderSources.TargetOutlineVisibleMaskFrag];
        for (int i = 0; i < fragments.Length; i++)
        {
            string fragment = fragments[i];
            Assert.DoesNotContain("interiorCut", fragment);
            Assert.Equal(hashes[i], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fragment.ReplaceLineEndings("\n")))));
            var pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.TargetOutlineSkinnedMaskVert, fragment);
            Assert.Equal(3, pair.Reflection.ResourceLayouts.Length);
            Assert.Equal(GpuResourceKind.UniformBuffer, Assert.Single(pair.Reflection.ResourceLayouts[2].Elements).Kind);
        }
        foreach (string fragment in new[] { ShaderSources.TargetOutlineRigidFullMaskFrag, ShaderSources.TargetOutlineRigidVisibleMaskFrag })
        {
            Assert.Contains(ShaderSources.InteriorCutGlsl, fragment);
            Assert.Contains("if (Params.w > 0.5 && interiorCut(vWorldPos)) discard;", fragment);
            var pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.TargetOutlineMaskVert, fragment);
            Assert.Equal(new[] { GpuResourceKind.TextureReadOnly, GpuResourceKind.Sampler },
                pair.Reflection.ResourceLayouts[2].Elements.Select(element => element.Kind));
        }
        Assert.Contains("if (Params.y > 0.5 && interiorCut(vWorldPos)) discard;", ShaderSources.SilhouetteFrag);
        Assert.Contains("if (Params.y > 0.5 && interiorCut(vWorldPos)) discard;", ShaderSources.SilhouetteMotionFrag);
        Assert.Contains("vWorldPos = world.xyz;", ShaderSources.SilhouetteVert);
    }

    static T Read<T>(byte[] image, int offset) where T : unmanaged => MemoryMarshal.Read<T>(image.AsSpan(offset));
    static int Constant(object owner, string name) => (int)owner.GetType().GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
}

internal static class HighlightTestAccess
{
    internal static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
