using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Admitted uploads, the path <c>MapScene3DSink</c> and <see cref="Scene3D.PrepareResourceUpload(SceneUploadData,ISceneUploadReservation)"/>
/// take, carry a material's emissive and share its textures through the scene residency the convenience uploads use
/// (issue #1776). A loader image keeps its identity through every retained copy, so an image a kit shares is one
/// texture per scene whichever path uploads it and however many uploads are in flight, and it lives until its last
/// owner retires. A caller-built image stays independent. The upload that created a shared texture keeps its
/// reservation charged until the texture is destroyed, because its claim paid for that allocation.</summary>
public sealed class AdmittedMaterialResidencyTests
{
    public enum Glow { FactorOnly, Textured }

    public enum Copy { UploadData, MapRigid, MapLod }

    public enum First { Convenience, Admitted }

    public enum Completion { Drained, Fenced }

    public enum Fault { End, Submit }

    public enum Survivor { Admitted, Convenience }

    static readonly Vector3 Factor = new(2f, 1f, 0.5f);

    [Theory]
    [InlineData(Glow.FactorOnly)]
    [InlineData(Glow.Textured)]
    public void A_published_upload_binds_its_emissive_and_retires_it_with_the_resource(Glow glow)
    {
        using var host = new NativeUploadTestScene();
        SceneUploadData data = Rigid(Glowing(glow));
        var reservation = Reserve(host.Scene, data);
        SceneResourceHandle handle = PublishNow(host.Scene, data, reservation);

        FakeResourceSet set = MaterialSet(host.Scene, handle);
        var parameters = (FakeBuffer)set.Bound("MaterialParams");
        Assert.Equal(Factor, EmissiveOf(host.Device, parameters));
        var emissive = (FakeTexture)set.Bound("EmissiveMap");
        Assert.Equal(glow == Glow.Textured ? 1 : 0, host.Scene.ResidentMaterialTextureCount);

        host.Scene.ReleaseResource(handle);
        Drain(host.Scene);
        Assert.True(set.Disposed);
        Assert.True(parameters.Disposed);
        // A real emissive map is the resource's own resident texture and goes with it. A factor-only glow samples
        // the renderer's white default, which stays.
        Assert.Equal(glow == Glow.Textured, emissive.Disposed);
        Assert.Equal(0, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal(1, reservation.ReleaseCount);
    }

    [Theory]
    [InlineData(Glow.FactorOnly)]
    [InlineData(Glow.Textured)]
    public void A_cancelled_upload_retires_its_emissive_and_releases_its_reservation_once(Glow glow)
    {
        using var host = new NativeUploadTestScene();
        var factory = (FakeGpuResourceFactory)host.Device.Inner.Factory;
        int buffers = factory.Buffers.Count, textures = factory.Textures.Count;
        SceneUploadData data = Rigid(Glowing(glow));
        var reservation = Reserve(host.Scene, data);

        SceneResourceUpload upload = host.Scene.PrepareResourceUpload(data, reservation);
        upload.Advance(64);
        upload.Dispose();
        Drain(host.Scene);

        FakeBuffer[] created = factory.Buffers.Skip(buffers).ToArray();
        Assert.Contains(created, b => b.SizeInBytes == ModelRenderer.MaterialParamsUbo.SizeInBytes);
        Assert.All(created, b => Assert.True(b.Disposed));
        Assert.All(factory.Textures.Skip(textures), t => Assert.True(t.Disposed));
        Assert.Equal(0, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal(1, reservation.ReleaseCount);
        Assert.Equal(0, reservation.ReservedBytes);
    }

    [Fact]
    public void Materials_differing_only_in_emissive_fingerprint_apart()
    {
        using var host = new NativeUploadTestScene();
        DecodedImage albedo = Loader("fingerprint-albedo", 100);
        string Digest(GltfMaterialMaps maps) => host.Scene.MeasureResourceUpload(Rigid(maps)).InputDigest;

        string[] digests =
        [
            Digest(new(albedo, null, null, 0f, null, Vector3.Zero)),
            Digest(new(albedo, null, null, 0f, null, Factor)),
            Digest(new(albedo, null, null, 0f, null, Factor * 2f)),
            Digest(new(albedo, null, null, 0f, Loader("fingerprint-glow", 90), Factor)),
            Digest(new(albedo, null, null, 0f, Loader("fingerprint-other-glow", 91), Factor)),
        ];

        Assert.Equal(digests.Length, digests.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(Copy.UploadData)]
    [InlineData(Copy.MapRigid)]
    [InlineData(Copy.MapLod)]
    public void A_retained_copy_keeps_the_emissive_and_loader_identity_and_copies_caller_pixels(Copy copy)
    {
        DecodedImage loader = Loader("copy-albedo", 100);
        byte[] pixels = Pixels(4, 60);
        var maps = new GltfMaterialMaps(loader, new DecodedImage(pixels, 4, 4), null, 0.5f, Loader("copy-glow", 90), Factor);

        GltfMaterialMaps kept = copy switch
        {
            Copy.UploadData => new SceneUploadData(NativeResourceUploadTests.Triangle(), maps, null, default).Maps!.Value,
            Copy.MapRigid => new MapRigidResource(NativeResourceUploadTests.Triangle(), maps).Maps,
            _ => new MapRenderLod(0, "lod", [new GltfMeshPart(NativeResourceUploadTests.Triangle(), maps)]).Parts[0].Maps,
        };

        Assert.Equal(Factor, kept.EmissiveFactor);
        Assert.Equal(0.5f, kept.AlphaCutoff);
        Assert.Equal(maps.Emissive!.Value.Identity, kept.Emissive!.Value.Identity);
        // An image that owns its pixel array is copied whatever it is named, and the copy keeps the name.
        Assert.Equal(loader.Identity, kept.Albedo!.Value.Identity);
        Assert.NotSame(loader.Rgba, kept.Albedo!.Value.Rgba);
        DecodedImage caller = kept.Normal!.Value;
        Assert.Null(caller.Identity);
        Assert.NotSame(pixels, caller.Rgba);
        Assert.Equal(pixels, caller.Rgba);
    }

    [Theory]
    [InlineData(First.Convenience)]
    [InlineData(First.Admitted)]
    public void A_convenience_mesh_and_an_admitted_upload_share_one_texture_until_the_last_owner_retires(First first)
    {
        using var host = new NativeUploadTestScene();
        using var dir = new TempDir();
        string path = MaterialGlbFixture.Write(dir.Path, "kit.glb",
            new MaterialBuilder("oak").WithBaseColor(new SharpGLTF.Memory.MemoryImage(MaterialGlbFixture.UniquePng(4))));
        GltfMeshPart part = GltfLoader.LoadPartsWithMaterials(path)[0];
        SceneUploadData data = Rigid(part.Maps, part.Mesh);
        var reservation = Reserve(host.Scene, data);

        MeshHandle loaded = default;
        SceneResourceHandle handle;
        if (first == First.Convenience) loaded = host.Scene.LoadMesh(part.Mesh, part.Maps);
        int copies = host.Device.TextureCopies.Count;
        handle = PublishNow(host.Scene, data, reservation);
        if (first == First.Convenience) Assert.Equal(copies, host.Device.TextureCopies.Count);   // nothing uploaded again
        else loaded = host.Scene.LoadMesh(part.Mesh, part.Maps);

        Assert.Equal(1, host.Scene.ResidentMaterialTextureCount);
        FakeResourceSet admittedSet = MaterialSet(host.Scene, handle);
        var texture = (FakeTexture)admittedSet.Bound("Albedo");
        Assert.Same(texture, ((FakeResourceSet)host.Scene.MeshMaterialSetForTests(loaded)!).Bound("Albedo"));

        // The first owner leaves: the texture stays for the second, and the admitted reservation stays charged,
        // because either its resource is still published or it created the texture that is still alive.
        if (first == First.Convenience) host.Scene.UnloadMesh(loaded);
        else host.Scene.ReleaseResource(handle);
        Drain(host.Scene);
        Assert.False(texture.Disposed);
        Assert.Equal(1, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal(0, reservation.ReleaseCount);
        if (first == First.Admitted) Assert.True(admittedSet.Disposed);

        if (first == First.Convenience) host.Scene.ReleaseResource(handle);
        else host.Scene.UnloadMesh(loaded);
        Drain(host.Scene);
        Assert.True(texture.Disposed);
        Assert.Equal(0, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal(1, reservation.ReleaseCount);
        Assert.Equal(1, reservation.DisposeCount);
        Assert.Equal(0, reservation.ReservedBytes);
    }

    /// <summary>The batch case: a world's props are all in flight before any publishes. One loader image is one
    /// texture whose level 0 is written once and whose chain is generated once, while equal pixels a caller built
    /// stay two textures.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uploads_in_flight_share_a_loader_image_and_keep_caller_images_apart(bool loaderImage)
    {
        using var host = new NativeUploadTestScene();
        var factory = (FakeGpuResourceFactory)host.Device.Inner.Factory;
        DecodedImage Image() => loaderImage ? Loader("batch-oak", 100) : new DecodedImage(Pixels(4, 100), 4, 4);
        SceneUploadData a = Rigid(new GltfMaterialMaps(Image(), null, null)), b = Rigid(new GltfMaterialMaps(Image(), null, null));
        var ra = Reserve(host.Scene, a);
        var rb = Reserve(host.Scene, b);

        using SceneResourceUpload first = host.Scene.PrepareResourceUpload(a, ra);
        using SceneResourceUpload second = host.Scene.PrepareResourceUpload(b, rb);
        int textures = loaderImage ? 1 : 2;
        Assert.Equal(textures, host.Scene.ResidentMaterialTextureCount);
        first.Advance(long.MaxValue);
        second.Advance(long.MaxValue);
        Assert.True(first.Ready);
        Assert.True(second.Ready);
        Assert.Equal(textures * 4 * 4 * 4, host.Device.TextureCopies.Sum(c => c.Bytes.Length));
        Assert.All(host.Device.TextureCopies, c => Assert.Equal(0u, c.Mip));
        SceneResourceHandle ha = first.Publish(), hb = second.Publish();

        var ta = (FakeTexture)MaterialSet(host.Scene, ha).Bound("Albedo");
        var tb = (FakeTexture)MaterialSet(host.Scene, hb).Bound("Albedo");
        Assert.Equal(loaderImage, ReferenceEquals(ta, tb));
        Assert.Equal(1, factory.CommandLists.Sum(cl => cl.MipGenerations.Count(t => ReferenceEquals(t, ta))));
        host.Scene.ReleaseResource(ha);
        host.Scene.ReleaseResource(hb);
        Drain(host.Scene);
        Assert.True(ta.Disposed);
        Assert.True(tb.Disposed);
        Assert.Equal(0, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal(1, ra.ReleaseCount);
        Assert.Equal(1, rb.ReleaseCount);
    }

    [Fact]
    public void A_cancelled_creator_leaves_its_texture_to_the_upload_sharing_it_and_stays_charged_until_it_is_destroyed()
    {
        using var host = new NativeUploadTestScene();
        var maps = new GltfMaterialMaps(Loader("cancel-oak", 100), null, null);
        SceneUploadData a = Rigid(maps), b = Rigid(maps);
        var ra = Reserve(host.Scene, a);
        var rb = Reserve(host.Scene, b);
        SceneResourceUpload creator = host.Scene.PrepareResourceUpload(a, ra);
        using SceneResourceUpload sharer = host.Scene.PrepareResourceUpload(b, rb);

        creator.Advance(long.MaxValue);   // its buffers and the whole texture, then cancelled unpublished
        creator.Dispose();
        Drain(host.Scene);
        Assert.Equal(0, ra.ReleaseCount);   // the texture it created is still alive for the sharer
        Assert.Equal(1, host.Scene.ResidentMaterialTextureCount);

        sharer.Advance(long.MaxValue);
        SceneResourceHandle handle = sharer.Publish();
        var texture = (FakeTexture)MaterialSet(host.Scene, handle).Bound("Albedo");
        Assert.Equal(4 * 4 * 4, host.Device.TextureCopies.Sum(c => c.Bytes.Length));   // written once, by the creator

        host.Scene.ReleaseResource(handle);
        Drain(host.Scene);
        Assert.True(texture.Disposed);
        Assert.Equal(1, ra.ReleaseCount);
        Assert.Equal(1, rb.ReleaseCount);
    }

    [Fact]
    public void A_creator_cancelled_mid_fill_hands_the_rest_of_the_texture_to_the_upload_sharing_it()
    {
        using var host = new NativeUploadTestScene();
        var maps = new GltfMaterialMaps(Loader("partial-oak", 100), null, null);
        SceneUploadData a = Rigid(maps), b = Rigid(maps);
        var ra = Reserve(host.Scene, a);
        var rb = Reserve(host.Scene, b);
        SceneResourceUpload creator = host.Scene.PrepareResourceUpload(a, ra);
        using SceneResourceUpload sharer = host.Scene.PrepareResourceUpload(b, rb);

        // The creator writes its buffers and the first row of the texture, one texel at a time, then is cancelled.
        while (TextureBytes(host) < 16) creator.Advance(4);
        Assert.Equal(16, TextureBytes(host));
        creator.Dispose();
        Drain(host.Scene);
        Assert.Equal(0, ra.ReleaseCount);

        sharer.Advance(long.MaxValue);
        Assert.True(sharer.Ready);
        Assert.Equal(4 * 4 * 4, TextureBytes(host));   // the sharer wrote the other three rows, nothing twice
        SceneResourceHandle handle = sharer.Publish();
        var texture = (FakeTexture)MaterialSet(host.Scene, handle).Bound("Albedo");

        host.Scene.ReleaseResource(handle);
        Drain(host.Scene);
        Assert.True(texture.Disposed);
        Assert.Equal(1, ra.ReleaseCount);
        Assert.Equal(1, rb.ReleaseCount);
    }

    /// <summary>A chain counts as generated only once the recording that generates it is submitted. The creator's
    /// recording fails at its End or at its submission, on the fenced completion and on the drained one, and the
    /// creator is cancelled. The texture lives on for the upload sharing it, and the next owner to need it, that
    /// upload or a convenience load arriving first, submits the chain before anything samples it. The texture then
    /// retires once and each reservation settles once.</summary>
    [Theory]
    [InlineData(Completion.Drained, Fault.End, Survivor.Admitted)]
    [InlineData(Completion.Drained, Fault.Submit, Survivor.Admitted)]
    [InlineData(Completion.Fenced, Fault.End, Survivor.Admitted)]
    [InlineData(Completion.Fenced, Fault.Submit, Survivor.Admitted)]
    [InlineData(Completion.Drained, Fault.End, Survivor.Convenience)]
    [InlineData(Completion.Drained, Fault.Submit, Survivor.Convenience)]
    [InlineData(Completion.Fenced, Fault.End, Survivor.Convenience)]
    [InlineData(Completion.Fenced, Fault.Submit, Survivor.Convenience)]
    public void A_chain_whose_recording_failed_is_submitted_by_the_owner_that_survives(Completion completion, Fault fault,
        Survivor survivor)
    {
        ReadbackRingFakeDevice? ring = completion == Completion.Fenced ? new ReadbackRingFakeDevice() : null;
        using var host = new NativeUploadTestScene(ring);
        host.Device.RecordSubmissions();
        FakeGpuResourceFactory factory = ring?.InnerFactory ?? (FakeGpuResourceFactory)host.Device.Inner.Factory;
        var maps = new GltfMaterialMaps(Loader($"fault-oak-{completion}-{fault}-{survivor}", 100), null, null);
        SceneUploadData a = Rigid(maps), b = Rigid(maps);
        var ra = Reserve(host.Scene, a);
        var rb = Reserve(host.Scene, b);
        SceneResourceUpload creator = host.Scene.PrepareResourceUpload(a, ra);
        FakeTexture texture = factory.Textures[^1];
        using SceneResourceUpload sharer = host.Scene.PrepareResourceUpload(b, rb);

        if (fault == Fault.End) factory.FailNextEnd();
        else host.Device.FailNextSubmit();
        Assert.Throws<InvalidOperationException>(() => creator.Advance(long.MaxValue));
        Assert.Equal(4 * 4 * 4, TextureBytes(host));   // level 0 was written before the recording failed
        Assert.Equal(0, SubmittedGenerations(host, texture));
        Settle(host, ring);
        Assert.False(texture.Disposed);
        Assert.Equal(0, ra.ReleaseCount);   // the texture its claim paid for is still alive for the sharer

        Scene3D.SurfaceMaps convenience = default;
        if (survivor == Survivor.Convenience) convenience = host.Scene.LoadSurfaceMaps(maps);
        sharer.Advance(long.MaxValue);
        if (ring is not null) foreach (IGpuFence fence in ring.SubmittedFences) fence.AsManual().Signal();
        Assert.True(sharer.Ready);
        Assert.Equal(1, SubmittedGenerations(host, texture));
        Assert.Equal(4 * 4 * 4, TextureBytes(host));   // nothing written twice
        SceneResourceHandle handle = sharer.Publish();
        Assert.Same(texture, MaterialSet(host.Scene, handle).Bound("Albedo"));

        if (survivor == Survivor.Convenience) host.Scene.UnloadSurfaceMaps(convenience);
        host.Scene.ReleaseResource(handle);
        Settle(host, ring);
        Assert.Equal(1, texture.DisposeCalls);
        Assert.Equal(0, host.Scene.ResidentMaterialTextureCount);
        Assert.Equal((1, 1), (ra.ReleaseCount, ra.DisposeCount));
        Assert.Equal((1, 1), (rb.ReleaseCount, rb.DisposeCount));
        Assert.Equal((0L, 0L), (ra.ReservedBytes, rb.ReservedBytes));
    }

    // Generations of the chain in lists a submission accepted. A list whose End or submission failed never got there.
    static int SubmittedGenerations(NativeUploadTestScene host, IGpuTexture texture) =>
        host.Device.SubmittedLists!.Sum(list =>
            (list is CommandTallyGpuCommandList tally ? tally.Inner : list) is NullGpuCommandList recorded
                ? recorded.MipGenerations.Count(t => ReferenceEquals(t, texture)) : 0);

    // A fenced device retires only behind signalled fences, so each frame of the drain signals every one submitted.
    static void Settle(NativeUploadTestScene host, ReadbackRingFakeDevice? ring)
    {
        for (int frame = 0; frame <= GpuRetireQueue.DefaultFrameDelay + 1; frame++)
        {
            if (ring is not null) foreach (IGpuFence fence in ring.SubmittedFences) fence.AsManual().Signal();
            host.Scene.Begin();
        }
    }

    static int TextureBytes(NativeUploadTestScene host) => host.Device.TextureCopies.Sum(c => c.Bytes.Length);

    static GltfMaterialMaps Glowing(Glow glow) =>
        new(null, null, null, 0f, glow == Glow.Textured ? Loader("glow-" + Guid.NewGuid().ToString("N"), 90) : null, Factor);

    static DecodedImage Loader(string name, byte red) => new(Pixels(4, red), 4, 4, "sha256:" + name);

    static byte[] Pixels(int size, byte red) =>
        Enumerable.Range(0, size * size).SelectMany(_ => new byte[] { red, 80, 40, 255 }).ToArray();

    static SceneUploadData Rigid(GltfMaterialMaps maps, GltfMesh? mesh = null) =>
        new(mesh ?? NativeResourceUploadTests.Triangle(), maps, null, default);

    static NativeUploadReservation Reserve(Scene3D scene, SceneUploadData data) => new(scene.MeasureResourceUpload(data));

    static SceneResourceHandle PublishNow(Scene3D scene, SceneUploadData data, NativeUploadReservation reservation)
    {
        using SceneResourceUpload upload = scene.PrepareResourceUpload(data, reservation);
        upload.Advance(long.MaxValue);
        return upload.Publish();
    }

    static FakeResourceSet MaterialSet(Scene3D scene, SceneResourceHandle handle) =>
        (FakeResourceSet)scene.MeshMaterialSetForTests(scene.ResourceMesh(handle))!;

    static Vector3 EmissiveOf(NativeUploadTestDevice device, IGpuBuffer parameters)
    {
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(device.BufferCopies[parameters]);
        return new Vector3(values[0], values[1], values[2]);
    }

    static void Drain(Scene3D scene)
    {
        for (int frame = 0; frame <= GpuRetireQueue.DefaultFrameDelay + 1; frame++) scene.Begin();
    }
}
