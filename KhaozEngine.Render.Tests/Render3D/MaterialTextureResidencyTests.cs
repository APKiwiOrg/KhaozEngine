using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Per-scene material texture residency (issue #1776): one GPU texture per image identity, reference
/// counted by the map bundles and meshes that use it, retired when the last one goes, transactional on a failed
/// upload, and holding a loader image's decoded pixels only until the upload. Device-free: FakeGpuDevice hands out
/// inert textures, and <see cref="Scene3D.LiveTextureCount"/> shows a texture leaving the scene.</summary>
public sealed class MaterialTextureResidencyTests
{
    // RGBA8 bytes of a 2x2 texture with its full chain (2x2 then 1x1), the fixture PNG size.
    const long TwoByTwoChainBytes = 2 * 2 * 4 + 1 * 1 * 4;

    static Scene3D NewScene(FakeGpuDevice device) =>
        new(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    // Two kit pieces that embed one albedo PNG, each with its own normal map, so a part carries two maps and only
    // one of them is shared.
    static (IReadOnlyList<GltfMeshPart> A, IReadOnlyList<GltfMeshPart> B) TwoKitPieces(TempDir dir)
    {
        byte[] sharedAlbedo = MaterialGlbFixture.UniquePng();
        IReadOnlyList<GltfMeshPart> Load(string name) => GltfLoader.LoadPartsWithMaterials(MaterialGlbFixture.Write(
            dir.Path, name, new MaterialBuilder("oak")
                .WithBaseColor(new SharpGLTF.Memory.MemoryImage(sharedAlbedo))
                .WithNormal(new SharpGLTF.Memory.MemoryImage(MaterialGlbFixture.UniquePng()))));
        return (Load("a.glb"), Load("b.glb"));
    }

    [Fact]
    public void One_image_shared_by_two_glbs_is_one_texture_released_with_its_last_mesh()
    {
        using var dir = new TempDir();
        var (a, b) = TwoKitPieces(dir);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        int before = scene.LiveTextureCount;

        IReadOnlyList<MeshHandle> first = scene.LoadPropMeshes(a);
        IReadOnlyList<MeshHandle> second = scene.LoadPropMeshes(b);
        Assert.Equal(3, scene.ResidentMaterialTextureCount);   // one shared albedo, two normal maps
        Assert.Equal(3 * TwoByTwoChainBytes, scene.ResidentMaterialTextureBytes);
        Assert.Equal(before + 3, scene.LiveTextureCount);

        foreach (MeshHandle handle in first) scene.UnloadMesh(handle);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);   // the shared albedo stays with the second piece
        Assert.Equal(before + 2, scene.LiveTextureCount);

        foreach (MeshHandle handle in second) scene.UnloadMesh(handle);
        Assert.Equal(0, scene.ResidentMaterialTextureCount);
        Assert.Equal(0, scene.ResidentMaterialTextureBytes);
        Assert.Equal(before, scene.LiveTextureCount);
    }

    [Fact]
    public void Every_turn_key_glTF_upload_owns_its_textures_until_it_unloads()
    {
        using var dir = new TempDir();
        byte[] albedo = MaterialGlbFixture.UniquePng();
        string rigid = MaterialGlbFixture.Write(dir.Path, "rigid.glb",
            new MaterialBuilder("m").WithBaseColor(new SharpGLTF.Memory.MemoryImage(albedo)));
        string rigged = MaterialGlbFixture.Write(dir.Path, "rigged.glb",
            new MaterialBuilder("m").WithBaseColor(new SharpGLTF.Memory.MemoryImage(albedo)), rigged: true);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);

        var (mesh, maps) = GltfLoader.LoadWithMaterial(rigid);
        MeshHandle single = scene.LoadMesh(mesh, maps);
        Scene3D.PropHandle prop = scene.LoadProp(GltfLoader.LoadPartsWithMaterials(rigid));
        var (skinnedMesh, skinnedMaps) = GltfLoader.LoadSkinnedWithMaterial(rigged);
        SkinnedMeshHandle skinned = scene.LoadSkinnedMesh(skinnedMesh, skinnedMaps);
        Assert.Equal(1, scene.ResidentMaterialTextureCount);   // all three name the same albedo image

        scene.UnloadMesh(single);
        scene.UnloadProp(prop);
        Assert.Equal(1, scene.ResidentMaterialTextureCount);
        scene.UnloadSkinnedMesh(skinned);
        Assert.Equal(0, scene.ResidentMaterialTextureCount);
    }

    [Fact]
    public void A_map_bundle_releases_one_reference_per_slot_and_a_caller_built_image_is_its_own_texture()
    {
        using var dir = new TempDir();
        var (a, _) = TwoKitPieces(dir);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);

        Scene3D.SurfaceMaps one = scene.LoadSurfaceMaps(a[0].Maps);
        Scene3D.SurfaceMaps two = scene.LoadSurfaceMaps(a[0].Maps);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);
        scene.UnloadSurfaceMaps(one);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);
        scene.UnloadSurfaceMaps(two);
        Assert.Equal(0, scene.ResidentMaterialTextureCount);

        // No identity, no sharing: each bundle uploads its own copy, as every upload did before residency.
        var own = new GltfMaterialMaps(new DecodedImage(new byte[] { 9, 8, 7, 255 }, 1, 1), null, null);
        Scene3D.SurfaceMaps ownA = scene.LoadSurfaceMaps(own);
        Scene3D.SurfaceMaps ownB = scene.LoadSurfaceMaps(own);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);
        scene.UnloadSurfaceMaps(ownA);
        scene.UnloadSurfaceMaps(ownB);
        Assert.Equal(0, scene.ResidentMaterialTextureCount);
    }

    [Fact]
    public void UnloadTexture_on_a_resident_handle_releases_one_reference()
    {
        using var dir = new TempDir();
        var (a, b) = TwoKitPieces(dir);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);

        Scene3D.SurfaceMaps first = scene.LoadSurfaceMaps(a[0].Maps);
        Scene3D.SurfaceMaps second = scene.LoadSurfaceMaps(b[0].Maps);
        Assert.Equal(first.Albedo, second.Albedo);   // one texture behind both bundles

        scene.UnloadTexture(first.Albedo);   // the second bundle still holds the shared albedo
        Assert.Equal(3, scene.ResidentMaterialTextureCount);
        scene.UnloadTexture(second.Albedo);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);
    }

    [Fact]
    public void A_failed_upload_retires_every_texture_it_created()
    {
        using var dir = new TempDir();
        var (a, _) = TwoKitPieces(dir);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        int before = scene.LiveTextureCount;
        var factory = (FakeGpuResourceFactory)device.Factory;

        factory.FailTextureCreationAfter(1);   // the albedo uploads, the normal map's texture creation throws
        Assert.Throws<InvalidOperationException>(() => scene.LoadPropMeshes(a));

        Assert.Equal(0, scene.ResidentMaterialTextureCount);
        Assert.Equal(before, scene.LiveTextureCount);

        // The rollback left nothing behind, so the same parts load cleanly once the device recovers.
        factory.ThrowOnTextureCreate = 0;
        IReadOnlyList<MeshHandle> loaded = scene.LoadPropMeshes(a);
        Assert.Equal(2, scene.ResidentMaterialTextureCount);
        foreach (MeshHandle handle in loaded) scene.UnloadMesh(handle);
    }

    [Fact]
    public void A_failed_upload_releases_a_shared_texture_without_retiring_it()
    {
        using var dir = new TempDir();
        var (a, b) = TwoKitPieces(dir);
        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        IReadOnlyList<MeshHandle> resident = scene.LoadPropMeshes(b);   // the shared albedo and b's own normal map
        int before = scene.LiveTextureCount;
        var factory = (FakeGpuResourceFactory)device.Factory;

        // a's albedo is already resident, so a's first texture creation is its own normal map, and that one throws.
        factory.FailTextureCreationAfter(0);
        Assert.Throws<InvalidOperationException>(() => scene.LoadPropMeshes(a));
        factory.ThrowOnTextureCreate = 0;

        Assert.Equal(2, scene.ResidentMaterialTextureCount);
        Assert.Equal(before, scene.LiveTextureCount);
        foreach (MeshHandle handle in resident) scene.UnloadMesh(handle);
        Assert.Equal(0, scene.ResidentMaterialTextureCount);   // the failed load kept no reference to the albedo
    }

    [Fact]
    public void A_loader_image_holds_its_pixels_until_a_scene_uploads_them()
    {
        using var dir = new TempDir();
        var (a, _) = TwoKitPieces(dir);
        DecodedImage albedo = a[0].Maps.Albedo!.Value;
        byte[] decoded = (byte[])albedo.Rgba.Clone();
        Assert.True(albedo.Source!.PixelsPinnedForTests);

        using var device = new FakeGpuDevice();
        using var scene = NewScene(device);
        IReadOnlyList<MeshHandle> loaded = scene.LoadPropMeshes(a);

        Assert.False(albedo.Source.PixelsPinnedForTests);
        Assert.Equal(decoded, albedo.Rgba);   // still readable: decoded again from the kept encoded bytes if needed
        foreach (MeshHandle handle in loaded) scene.UnloadMesh(handle);
    }
}
