using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutContractTests
{
    [Fact]
    public void PacksOnlyTheOptedInW()
    {
        var queue = new SceneInstances();
        var mesh = new MeshHandle(0, 1);
        queue.Add(new RigidInstanceDraw(mesh, Matrix4x4.Identity) { CutInsideInteriors = true });
        queue.Add(mesh, Matrix4x4.Identity, Color.White);
        queue.Add(new RigidInstanceDraw(new MeshHandle(1, 1), Matrix4x4.Identity));
        queue.Add(new RigidInstanceDraw(mesh, Matrix4x4.Identity));
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        Scene3D.GroupInstances(queue.Items, data, runs);
        Assert.True(queue.Items[0].CutInsideInteriors);
        Assert.All(queue.Items.Skip(1), item => Assert.False(item.CutInsideInteriors));
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, data.Select(item => item.SpecParams.W));
        Assert.Equal(2, runs.Count);
        Assert.Equal(3u, runs[0].Count);
        Assert.Equal(128u, ModelRenderer.InstanceData.SizeInBytes);
        Assert.Equal(128, Marshal.SizeOf<ModelRenderer.InstanceData>());
        Assert.Contains("vSpecParams = vec4(0.0, 32.0, FoliageParameters.w, 0.0)", ShaderSources.FoliageVert);
        Assert.DoesNotContain("interiorCut(", ShaderSources.SkinnedModelFrag);

        using var device = new FakeGpuDevice();
        using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var target = device.Factory.CreateFramebuffer(null, texture);
        using var scene = new Scene3D(device, target.Outputs);
        scene.UseGpuSkinning = false;
        scene.FrustumCulling = false;
        scene.Post.Starfield = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var skinnedMesh = SkinnedMeshBuilder.BuildTube(.25f, 1f, 4, 3, 2, Axis.Z);
        SkinnedMeshHandle skinned = scene.LoadSkinnedMesh(skinnedMesh);
        scene.Begin();
        scene.DrawSkinned(skinned, skinnedMesh.RestPose, Matrix4x4.Identity, Color.White, Material.None);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        commands.Begin();
        scene.PrepareFrame();
        scene.RenderInternal(commands, 32, 24, target);
        commands.End();
        var upload = Assert.Single(commands.Uploads, item => item.Bytes == 128);
        var packed = MemoryMarshal.Read<ModelRenderer.InstanceData>(upload.Data!);
        Assert.Equal(1f, packed.IsDynamic);
        Assert.Equal(0f, packed.SpecParams.W);
    }

    [Fact]
    public void TailAndBindingsKeepCompatibilityOffsets()
    {
        Assert.Equal(1344u, ModelRenderer.InteriorCutWindowOffset);
        Assert.Equal(1360u, ModelRenderer.InteriorCutHeightsOffset);
        Assert.Equal(1376u, IndirectUniforms.TailOffset);
        Assert.Equal(1536u, ModelRenderer.UboBytes);
        var reflected = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.ModelVert, ShaderSources.ModelFrag);
        var elements = Assert.Single(reflected.Reflection.ResourceLayouts).Elements;
        Assert.Equal(15, elements.Length);
        Assert.Equal(GpuResourceKind.TextureReadOnly, elements[14].Kind);
        Assert.Contains("binding=14) uniform texture2D InteriorCut", ShaderSources.ModelFrag);
        foreach (var (_, source) in MotionUboLayoutTests.FrameBlockSources)
        {
            string[] members = MotionUboLayoutTests.Members(source, "uniform U {");
            int indirect = Array.IndexOf(members, "vec4 IndirectParams");
            int interior = indirect < 0 ? members.Length - 2 : indirect - 2;
            Assert.Equal("vec4 InteriorCutWindow", members[interior]);
            Assert.Equal("vec4 InteriorCutHeights", members[interior + 1]);
        }
        using var device = new FakeGpuDevice();
        using var renderer = new ModelRenderer(device, Outputs, 16, 1);
        byte[] before = renderer.FrameImage.ToArray();
        var uniforms = InteriorCutUniforms.From(Field(), new Vector3(1, 2, 3));
        renderer.SetInteriorCut(renderer.InteriorResources.Texture, uniforms);
        byte[] after = renderer.FrameImage.ToArray();
        Assert.Equal(before[..1344], after[..1344]);
        Assert.Equal(uniforms.Window, MemoryMarshal.Read<Vector4>(after.AsSpan(1344)));
        Assert.Equal(uniforms.Heights, MemoryMarshal.Read<Vector4>(after.AsSpan(1360)));
        Assert.Equal(default, InteriorCutUniforms.From(null, Vector3.One));
    }

    [Fact]
    public void ColourAndMotionShareTheGatedDiscard()
    {
        const string discard = "if (vSpecParams.w > 0.5 && interiorCut(vWorldPos)) discard;";
        foreach (string source in new[] { ShaderSources.ModelFrag, ShaderSources.ModelMotionFrag })
        {
            Assert.Contains(discard, source);
            Assert.True(source.IndexOf(discard, StringComparison.Ordinal)
                > source.IndexOf("materialSample(RoughnessMap", StringComparison.Ordinal));
            Assert.Contains(ShaderSources.InteriorCutGlsl, source);
        }
        ShaderValidation.ValidatePair(ShaderSources.ModelVert, ShaderSources.ModelFrag, "InteriorColour");
        ShaderValidation.ValidatePair(ShaderSources.ModelMotionVert, ShaderSources.ModelMotionFrag, "InteriorMotion");
        ShaderValidation.ValidatePair(ShaderSources.FoliageVert, ShaderSources.ModelFrag, "InteriorFoliage");
        ShaderValidation.ValidatePair(ShaderSources.FoliageMotionVert, ShaderSources.ModelMotionFrag, "InteriorFoliageMotion");
    }

    [Fact]
    public void CpuSkinnedDissolveKeepsSharedLayoutWithoutCutting()
    {
        foreach (var (vertex, fragment, temporal) in new[]
        {
            (ShaderSources.ModelVert, ShaderSources.ModelDissolveFrag, false),
            (ShaderSources.ModelCpuSkinnedMotionVert, ShaderSources.ModelDissolveMotionFrag, true),
        })
        {
            var program = MetalShaderBuild.Pair(vertex, fragment, null, "CpuSkinnedDissolveInteriorBinding");
            GpuResourceLayoutDescription[] layouts = temporal
                ? [VulkanDescriptorLimitTests.ShippedLayouts["Model"], VulkanDescriptorLimitTests.ShippedLayouts["Motion.frame"]]
                : [VulkanDescriptorLimitTests.ShippedLayouts["Model"]];
            program.Table.RequireLayoutShape(layouts, "CpuSkinnedDissolve");
            Assert.DoesNotContain("interiorCut(", fragment);
            Assert.DoesNotContain("texelFetch(sampler2D(InteriorCut", fragment);
            Assert.Contains("textureSize(sampler2D(InteriorCut, Samp), 0)", fragment);
        }
    }

    [Fact]
    public void RebasedBoundaryMatchesCpu()
    {
        InteriorHeightField field = InteriorHeightFieldBuilder.Build([new InteriorVolume
        {
            Outline = [new(10000f), new(10002f, 10000f), new(10002f), new(10000f, 10002f)],
            FloorY = .1525f,
            CeilingY = .4025f,
        }], new InteriorWindow(new Vector2(10000f), .25f, 8), InteriorEncoding.For(0f, 1f));
        Vector3 origin = new(10000f, 0f, 10000f);
        InteriorCutUniforms uniforms = InteriorCutUniforms.From(field, origin);
        Assert.Equal(new Vector4(0f, 0f, 4f, 8f), uniforms.Window);
        Assert.Equal(0f, uniforms.Heights.X);
        Assert.Equal(new byte[] { 0, 15, 0, 40 }, field.Texels.Span[..4].ToArray());
        Assert.True(field.TryGetSpan(2, 2, out float floor, out float top));
        foreach (float height in new[] { MathF.BitDecrement(floor), floor, MathF.BitIncrement(floor),
            MathF.BitDecrement(top), top, MathF.BitIncrement(top) })
        {
            var world = new Vector3(10000.5f, height, 10000.5f);
            ReadOnlySpan<byte> bytes = field.Texels.Span.Slice((2 * 8 + 2) * 4, 4);
            float lo = bytes[0] * 256 + bytes[1], hi = bytes[2] * 256 + bytes[3];
            float q = ((world - origin).Y - uniforms.Heights.X) * uniforms.Heights.Y;
            Assert.Equal(field.Contains(world), hi > lo && q >= lo && q < hi);
        }
    }

    [Fact]
    public void FieldReplacementKeepsMaterialBindingsLive()
    {
        using var device = new UploadDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var target = factory.CreateFramebuffer(null, targetTexture);
        FakeTexture zero, reactivated;
        using (var scene = new Scene3D(device, target.Outputs))
        {
            MeshHandle plain = scene.LoadMesh(MeshPrimitives.Box(1));
            var albedo = scene.LoadTexture(new byte[] { 255, 100, 50, 255 }, 1, 1);
            MeshHandle textured = scene.LoadMesh(MeshPrimitives.Box(1), albedo);
            void Render()
            {
                scene.Begin();
                scene.Draw(new RigidInstanceDraw(plain, Matrix4x4.Identity) { CutInsideInteriors = true });
                scene.Draw(textured, Matrix4x4.Identity);
                scene.PrepareFrame();
                using var commands = factory.CreateCommandList();
                commands.Begin();
                scene.RenderInternal(commands, 16, 16, target);
                commands.End();
            }
            FakeResourceSet[] Live() => factory.ResourceSets.Where(s => !s.Disposed && s.Resources.Length == 15).ToArray();
            void AllBind(IGpuTexture texture)
            {
                Assert.True(Live().Length >= 2);
                Assert.All(Live(), set => Assert.Same(texture, set.Resources[14]));
                Assert.Equal(0, factory.LiveSetsNamingAFreedTexture);
            }
            Render();
            Assert.NotEmpty(Live());
            zero = Assert.IsType<FakeTexture>(Live()[0].Resources[14]);
            Assert.Equal(1u, zero.Width);
            Assert.Equal(new byte[4], device.LastUpload(zero));
            AllBind(zero);

            InteriorHeightField field = Field();
            scene.InteriorCut = field;
            Render();
            var first = Assert.IsType<FakeTexture>(Live()[0].Resources[14]);
            Assert.NotSame(zero, first);
            Assert.Equal(field.Texels.ToArray(), device.LastUpload(first));
            AllBind(first);
            int uploads = device.Uploads.Count, sets = factory.ResourceSets.Count;
            Render();
            Assert.Equal(uploads, device.Uploads.Count);
            Assert.Equal(sets, factory.ResourceSets.Count);
            scene.InteriorCut = Field(top: 2f);
            Render();
            AllBind(first);
            Assert.Equal(uploads + 1, device.Uploads.Count);
            Assert.Equal(sets, factory.ResourceSets.Count);
            scene.LoadMesh(MeshPrimitives.Box(1), albedo);
            AllBind(first);

            FakeResourceSet[] before = Live();
            scene.InteriorCut = Field(cells: 16);
            factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 2;
            Assert.Throws<InvalidOperationException>(Render);
            Assert.All(before, set => Assert.False(set.Disposed));
            Assert.False(first.Disposed);
            Assert.Equal(before, Live());
            Assert.Equal(0, factory.LiveSetsNamingAFreedTexture);
            Assert.True(factory.ResourceSets[^1].Disposed);
            factory.ThrowOnResourceSetCreate = 0;
            uploads = device.Uploads.Count;
            Render();
            Assert.Equal(uploads, device.Uploads.Count);
            var resized = Assert.IsType<FakeTexture>(Live()[0].Resources[14]);
            Assert.Equal(16u, resized.Width);
            Assert.True(first.Disposed);
            Assert.All(before, set => Assert.True(set.Disposed));
            AllBind(resized);

            // Replacing either shadow atlas must preserve the cut handle, and vice versa.
            Assert.True(scene.ReplaceShadowLayout(32, 2));
            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) scene.Begin();
            AllBind(resized);
            var compatibility = Live().Select(set => set.Resources[..14]).ToArray();
            scene.InteriorCut = null;
            Render();
            AllBind(zero);
            Assert.All(compatibility, expected => Assert.Contains(Live(),
                set => set.Resources.Take(14).SequenceEqual(expected)));
            Assert.True(resized.Disposed);
            Assert.Equal(Vector4.Zero, MemoryMarshal.Read<Vector4>(scene.FrameImageForTests[1344..]));
            scene.InteriorCut = field;
            Render();
            reactivated = Assert.IsType<FakeTexture>(Live()[0].Resources[14]);
            Assert.NotSame(first, reactivated);
            Assert.NotSame(zero, reactivated);
            AllBind(reactivated);
        }
        Assert.True(zero.Disposed);
        Assert.True(reactivated.Disposed);
        Assert.Equal(0, factory.LiveSetsNamingAFreedTexture);
    }

    [Fact]
    public void ResourceOwnerRetiresOnlyAfterBindingsCommit()
    {
        using var device = new UploadDevice();
        using var resources = new InteriorCutResources(device);
        IGpuTexture zero = resources.Texture;
        long version = resources.Version, generation = resources.TextureGeneration;
        resources.Apply(null);
        Assert.Equal(version, resources.Version);
        InteriorHeightField field = Field();
        resources.Apply(field);
        var first = Assert.IsType<FakeTexture>(resources.Texture);
        Assert.Equal(generation + 1, resources.TextureGeneration);
        Assert.NotEqual(version, resources.Version);
        int uploads = device.Uploads.Count;
        version = resources.Version;
        resources.Apply(field);
        Assert.Equal(version, resources.Version);
        Assert.Equal(uploads, device.Uploads.Count);
        resources.CommitBindings();
        resources.Apply(Field(cells: 16));
        Assert.False(first.Disposed);
        resources.CommitBindings();
        Assert.True(first.Disposed);
        resources.Apply(null);
        Assert.Same(zero, resources.Texture);
        Assert.NotEqual(version, resources.Version);
    }

    sealed class UploadDevice : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();
        internal List<(IGpuTexture Texture, byte[] Bytes)> Uploads { get; } = new();
        internal byte[] LastUpload(IGpuTexture texture) => Uploads.Last(u => ReferenceEquals(u.Texture, texture)).Bytes;
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => _inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }
        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void WaitForIdle() => _inner.WaitForIdle();
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => _inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
        {
            Uploads.Add((texture, (byte[])data.Clone()));
            _inner.UpdateTexture(texture, data, x, y, width, height);
        }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer)
            => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);
        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();
        public void Dispose() => _inner.Dispose();
    }

    internal static readonly GpuOutputDescription Outputs = new(null,
        new[] { GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float });

    internal static InteriorHeightField Field(float offset = 0f, int cells = 8, float top = 1f) =>
        InteriorHeightFieldBuilder.Build(new[] { new InteriorVolume
        {
            Outline = new[] { new Vector2(offset), new Vector2(offset + 2, offset),
                new Vector2(offset + 2), new Vector2(offset, offset + 2) },
            FloorY = offset - 1f,
            CeilingY = offset + top,
        } }, new InteriorWindow(new Vector2(offset), .25f, cells), InteriorEncoding.For(offset - 1f, offset + top));
}
