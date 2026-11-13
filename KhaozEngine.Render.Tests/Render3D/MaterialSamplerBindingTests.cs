using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Per-slot material sampling (issue #1776). A real map samples through the material sampler, wrap-addressed
/// and anisotropic at <see cref="RenderQuality.MaterialAnisotropy"/>, while a slot on its 1x1 default keeps the
/// device's linear sampler, so the defaults sample exactly as they always did and an untextured mesh is unchanged.
/// A change to the setting rebuilds every live textured set when the next frame begins. Device-free: the fake
/// factory records every set's resources and every created sampler's description.</summary>
public sealed class MaterialSamplerBindingTests
{
    static readonly string[] SamplerSlots = ["Sampler", "NormalSamp", "RoughnessSamp", "EmissiveSamp"];

    static Scene3D NewScene(FakeGpuDevice device) =>
        new(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    static DecodedImage Image() => new(new byte[] { 200, 100, 50, 255, 10, 20, 30, 255, 1, 2, 3, 255, 4, 5, 6, 255 }, 2, 2);

    static GltfMesh Quad() => MeshAssembler.Build(new List<MeshCorner>
    {
        new(new(0, 0, 0), Vector3.UnitY, Vector4.One, new(0, 0)), new(new(1, 0, 0), Vector3.UnitY, Vector4.One, new(1, 0)),
        new(new(1, 0, 1), Vector3.UnitY, Vector4.One, new(1, 1)),
    });

    // Albedo and roughness maps are real, normal and emissive fall back to their defaults, and the material glows.
    static GltfMaterialMaps HalfTextured() => new(Image(), null, Image(), 0f, null, new Vector3(1f, 0.5f, 0f));

    static IGpuSampler Slot(IGpuResourceSet set, string name)
    {
        var fake = (FakeResourceSet)set;
        GpuResourceLayoutElement[] elements = ((FakeResourceLayout)fake.Layout!).Description.Elements;
        int index = Array.FindIndex(elements, element => element.Name == name);
        Assert.True(index >= 0, $"layout has no {name} binding");
        return (IGpuSampler)fake.Resources[index];
    }

    [Theory]
    [InlineData(16, 16u)]
    [InlineData(4, 4u)]
    [InlineData(64, 16u)]   // clamped to the maximum
    public void Real_maps_bind_the_anisotropic_material_sampler_and_defaults_keep_the_linear_one(int requested, uint anisotropy)
    {
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        scene.Post.Quality.MaterialAnisotropy = requested;
        scene.Begin();
        MeshHandle mesh = scene.LoadMesh(Quad(), HalfTextured());
        IGpuResourceSet set = scene.MeshMaterialSetForTests(mesh)!;

        var material = (FakeSampler)Slot(set, "Sampler");
        Assert.NotSame(device.LinearSampler, material);
        Assert.Equal(GpuSamplerFilter.Anisotropic, material.Description.Filter);
        Assert.Equal(anisotropy, material.Description.MaximumAnisotropy);
        Assert.Equal(GpuSamplerAddress.Wrap, material.Description.AddressModeU);
        Assert.Equal(GpuSamplerAddress.Wrap, material.Description.AddressModeV);
        Assert.Equal(GpuSamplerAddress.Wrap, material.Description.AddressModeW);
        Assert.Equal(0, material.Description.MipLodBias);
        Assert.Same(material, Slot(set, "RoughnessSamp"));
        Assert.Same(device.LinearSampler, Slot(set, "NormalSamp"));
        Assert.Same(device.LinearSampler, Slot(set, "EmissiveSamp"));

        // Every untextured mesh draws with the default set, whose slots never leave the linear sampler.
        foreach (string slot in SamplerSlots)
            Assert.Same(device.LinearSampler, Slot(scene.ModelDefaultResourcesForTests.Set, slot));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]   // clamped up to trilinear
    public void Anisotropy_one_samples_every_slot_through_the_devices_linear_sampler(int requested)
    {
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        scene.Post.Quality.MaterialAnisotropy = requested;
        scene.Begin();
        IGpuResourceSet set = scene.MeshMaterialSetForTests(scene.LoadMesh(Quad(), HalfTextured()))!;

        foreach (string slot in SamplerSlots) Assert.Same(device.LinearSampler, Slot(set, slot));
    }

    [Fact]
    public void An_anisotropy_change_rebuilds_rigid_and_skinned_sets_when_the_next_frame_begins()
    {
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        MeshHandle rigid = scene.LoadMesh(Quad(), HalfTextured());
        SkinnedMeshHandle skinned = scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(0.5f, 2f, 6, 6, 2, Axis.Z),
            HalfTextured());
        IGpuResourceSet before = scene.MeshMaterialSetForTests(rigid)!;
        var oldSampler = (FakeSampler)Slot(before, "Sampler");

        scene.Post.Quality.MaterialAnisotropy = 2;
        Assert.Same(before, scene.MeshMaterialSetForTests(rigid));   // nothing moves until the frame boundary
        scene.Begin();

        IGpuResourceSet after = scene.MeshMaterialSetForTests(rigid)!;
        Assert.NotSame(before, after);
        var newSampler = (FakeSampler)Slot(after, "Sampler");
        Assert.Equal(2u, newSampler.Description.MaximumAnisotropy);
        var (model, skinnedSet) = scene.SkinnedMaterialSetsForTests(skinned);
        Assert.Same(newSampler, Slot(model!, "Sampler"));
        Assert.Same(newSampler, Slot(skinnedSet!, "Sampler"));
        Assert.Same(device.LinearSampler, Slot(skinnedSet!, "NormalSamp"));

        // The replaced sampler goes through the retire queue, which frees it once its frames have drained.
        Assert.False(oldSampler.Disposed);
        for (int frame = 0; frame <= GpuRetireQueue.DefaultFrameDelay; frame++) scene.Begin();
        Assert.True(oldSampler.Disposed);
    }
}
