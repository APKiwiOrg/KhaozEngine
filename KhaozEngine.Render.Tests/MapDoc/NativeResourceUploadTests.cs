using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeResourceUploadTests
{
    [Fact]
    public void ResourceUpload_ChargesAllBuffersAndRefusesEarlyPublish()
    {
        using var host = new NativeUploadTestScene();
        var vertices = new ModelVertex[82782];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new(new(i % 3, 0, i % 2), Vector3.UnitY, Vector4.One);
        var data = new SceneUploadData(new(vertices, new uint[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2 }), null, null, default);
        var demand = host.Scene.MeasureResourceUpload(data);
        Assert.Equal(6_291_456L, demand.TransferBytes);
        Assert.True(demand.PeakCpuBytes >= demand.TransferBytes);
        Assert.True(demand.PeakStagingGpuBytes >= demand.TransferBytes);
        using var reservation = new NativeUploadReservation(demand);
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        Assert.Throws<InvalidOperationException>(() => upload.Publish());
        Assert.Equal(4_194_304L, upload.Advance(4_194_304));
        Assert.False(upload.Ready);
        Assert.Throws<InvalidOperationException>(() => upload.Publish());
        Assert.Equal(2_097_152L, upload.Advance(4_194_304));
        Assert.True(upload.Ready);
        var handle = upload.Publish();
        Assert.True(new SceneResourceAccess(host.Scene).MeshOf(handle).Generation > 0);
        Assert.Equal(demand.TransferBytes, host.Device.UploadBytes);
        Assert.Equal(0, upload.Advance(4_194_304));
        Assert.Throws<InvalidOperationException>(() => upload.Publish());
        host.Scene.ReleaseResource(handle);
    }

    [Fact]
    public void PublishedHandle_DrawsWithoutUploadAndPreservesEveryRigidField()
    {
        using var host = new NativeUploadTestScene();
        var handle = Publish(host.Scene, new(Triangle(), null, null, default));
        var access = new SceneResourceAccess(host.Scene);
        var draw = new RigidInstanceDraw(default, Matrix4x4.CreateTranslation(.125f, .25f, -.5f))
        {
            Tint = new(.2f, .4f, .6f, .8f),
            Material = new(new Color(.1f, .2f, .3f), .4f, 12),
            Dissolve = .5f,
            DissolveEdgeWidth = .2f,
            DissolveEdgeColor = Color.White,
            CastsShadows = true,
            ShadowOnly = true,
            InvertShadowDissolve = true,
            DissolveComplement = .75f,
            Motion = MotionKey.From(91),
            CutInsideInteriors = true
        };
        host.Scene.Begin();
        long before = host.Device.UploadBytes;
        var anchor = new RenderAnchor(32000, -500, -32000);
        var anchored = new AnchoredRigidTransform(draw.World, anchor);
        access.DrawResourceAnchored(handle, draw, anchor);
        var actual = Assert.Single(host.Scene.QueuedInstancesForTests);
        var expected = draw with { Mesh = access.MeshOf(handle), World = anchored.Absolute };
        Assert.Equal(new SceneInstances.Instance(in expected, false) { Anchored = anchored }, actual);
        Assert.Equal(0L, host.Device.UploadBytes - before);
        using var foreign = new NativeUploadTestScene();
        Assert.Throws<ArgumentException>(() => new SceneResourceAccess(foreign.Scene).MeshOf(handle));
        host.Scene.ReleaseResource(handle);
        Assert.Throws<ArgumentException>(() => access.MeshOf(handle));
    }

    [Fact]
    public void PreparedFoliage_RejectsLazyUpload()
    {
        using var host = new NativeUploadTestScene();
        LoadParts(host.Scene);
        var data = new SceneUploadData(null, null, NativeRenderAssetFixtures.FoliageSource(), default);
        using var reservation = new NativeUploadReservation(host.Scene.MeasureResourceUpload(data));
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        Assert.Throws<ArgumentException>(() => GroundCoverRenderer.DrawPrepared(host.Scene, default, Vector3.Zero, new(), default));
        upload.Advance(long.MaxValue);
        var handle = upload.Publish();
        Assert.Throws<ArgumentException>(() => new SceneResourceAccess(host.Scene).MeshOf(handle));
        Assert.Throws<ArgumentException>(() => GroundCoverRenderer.DrawPrepared(host.Scene, handle, Vector3.Zero, new(), new(1, 0, 0)));
        host.Scene.Begin();
        long before = host.Device.UploadBytes;
        Assert.Equal(6, GroundCoverRenderer.DrawPrepared(host.Scene, handle, new(1, 0, 1), new(), default));
        Assert.Equal(0L, host.Device.UploadBytes - before);
        host.Render();
        Assert.Equal(0, host.Scene.LastFoliageStats.InstanceUploadBytes);
        host.Scene.ReleaseResource(handle);
    }

    [Fact]
    public void CancelledUpload_ReleasesReservedAndRetiredBytes()
    {
        using var inner = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(inner);
        var data = new SceneUploadData(Triangle(), null, null, default);
        var reservation = new NativeUploadReservation(host.Scene.MeasureResourceUpload(data));
        var upload = host.Scene.PrepareResourceUpload(data, reservation);
        upload.Advance(long.MaxValue);
        Assert.False(upload.Ready);
        Assert.Throws<InvalidOperationException>(() => upload.Publish());
        upload.Dispose(); upload.Dispose();
        Assert.True(reservation.ReservedBytes > 0);
        host.Scene.Begin();
        Assert.True(reservation.ReservedBytes > 0);
        foreach (var fence in inner.SubmittedFences) ((ManualFence)fence).Signal();
        host.Scene.Begin();
        Assert.Equal(0L, reservation.ReservedBytes);
        Assert.Equal(1, reservation.ReleaseCount);
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public void PreparedFoliage_ProductionPackingMatchesTwoModelLiterals()
    {
        using var host = new NativeUploadTestScene();
        LoadParts(host.Scene);
        var source = NativeRenderAssetFixtures.FoliageSource();
        var data = new SceneUploadData(null, null, source, default);
        using var reservation = new NativeUploadReservation(host.Scene.MeasureResourceUpload(data));
        using var upload = host.Scene.PrepareResourceUpload(data, reservation);
        var product = Assert.IsType<ScenePreparedFoliage>(upload.PreparedFoliage);
        Assert.Equal(832, product.Demand.OwnedGpuBytes);
        Assert.Equal(1024, product.Demand.PeakStagingGpuBytes);
        Assert.Equal(832, product.Demand.TransferBytes);
        Assert.Equal(NativeRenderAssetFixtures.ExpandedRows, product.ExpandedSourceRows.ToArray());
        Assert.Equal(NativeRenderAssetFixtures.ExpectedPatches, product.Patches);
        var packed = MemoryMarshal.Cast<byte, float>(product.PackedInstances.Span);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(NativeRenderAssetFixtures.ExpectedParameters[i], new Vector4(packed[i * 24 + 16], packed[i * 24 + 17], packed[i * 24 + 18], packed[i * 24 + 19]));
            Assert.Equal(NativeRenderAssetFixtures.ExpectedMotion[i], new Vector4(packed[i * 24 + 20], packed[i * 24 + 21], packed[i * 24 + 22], packed[i * 24 + 23]));
        }
        var swapped = new SceneUploadData(null, null, NativeRenderAssetFixtures.FoliageSource(true), default);
        using var other = host.Scene.PrepareResourceUpload(swapped, new NativeUploadReservation(host.Scene.MeasureResourceUpload(swapped)));
        Assert.Equal(NativeRenderAssetFixtures.SwappedExpandedRows, other.PreparedFoliage!.ExpandedSourceRows.ToArray());
        Assert.NotEqual(product.PackedInstances.ToArray(), other.PreparedFoliage.PackedInstances.ToArray());
    }

    [Fact]
    public void MeasureUpload_ReportsMipsClipAndStagingWithoutFactoriesAndDeniedClaimCreatesNothing()
    {
        using var host = new NativeUploadTestScene();
        var factory = (FakeGpuResourceFactory)host.Device.Inner.Factory;
        var maps = new GltfMaterialMaps(new(new byte[64], 4, 4), null, null, .5f);
        var clip = WaterSurfaceDomain.Encode(new Vector2[] { new(0, 0), new(1, 0), new(0, 1) });
        var data = new SceneUploadData(Triangle(), maps, null, clip);
        int buffers = factory.Buffers.Count, textures = factory.Textures.Count;
        var demand = host.Scene.MeasureResourceUpload(data);
        Assert.Equal(382, demand.TransferBytes); // 192 vertices, 36 normals, 6 indices, 84 mip bytes, 64 clip bytes.
        Assert.Equal(384, demand.OwnedGpuBytes);
        Assert.True(demand.PeakStagingGpuBytes >= 256 * (1 + 1 + 1 + 4 + 2 + 1 + 1));
        Assert.Equal(buffers, factory.Buffers.Count); Assert.Equal(textures, factory.Textures.Count);
        var denied = new NativeUploadReservation(demand) { Deny = true };
        Assert.Throws<InvalidOperationException>(() => host.Scene.PrepareResourceUpload(data, denied));
        Assert.Equal(buffers, factory.Buffers.Count); Assert.Equal(textures, factory.Textures.Count);
        Assert.Throws<ArgumentException>(() => host.Scene.PrepareResourceUpload(data,
            new NativeUploadReservation(demand with { InputDigest = "stale" })));
    }

    [Fact]
    public void PreparedFoliage_RefusesReleasedPartBeforePublication()
    {
        using var host = new NativeUploadTestScene();
        LoadParts(host.Scene);
        var data = new SceneUploadData(null, null, NativeRenderAssetFixtures.FoliageSource(), default);
        using var upload = host.Scene.PrepareResourceUpload(data, new NativeUploadReservation(host.Scene.MeasureResourceUpload(data)));
        upload.Advance(long.MaxValue);
        host.Scene.UnloadMesh(new(0));
        Assert.Throws<ArgumentException>(() => upload.Publish());
    }

    [Fact]
    public void PublishedHandle_DirectUnloadRefusesAndRetainsResourceOwnership()
    {
        using var host = new NativeUploadTestScene();
        var handle = Publish(host.Scene, new(Triangle(), null, null, default));
        var access = new SceneResourceAccess(host.Scene);
        var mesh = access.MeshOf(handle);
        Assert.Throws<InvalidOperationException>(() => host.Scene.UnloadMesh(mesh));
        Assert.Equal(mesh, access.MeshOf(handle));
        host.Scene.ReleaseResource(handle);
    }

    [Fact]
    public void FoliageSpaces_SubtractWorldAnchorOnceAndPreserveExplicitLocalBits()
    {
        using var host = new NativeUploadTestScene();
        LoadParts(host.Scene);
        var anchor = new RenderAnchor(31999, -500, -32000);
        var models = NativeRenderAssetFixtures.FoliageSource().Models;
        var world = new SceneFoliageSource(anchor, new[] { Matrix4x4.CreateTranslation(31999.125f, -499.5f, -31999.75f) },
            new float[] { .2f }, new[] { 1 }, models);
        var local = new SceneFoliageSource(anchor, new[] { Matrix4x4.CreateTranslation(.123456f, .5f, .25f) },
            new float[] { .2f }, new[] { 1 }, models, SceneFoliageTransformSpace.AnchorLocal);
        var worldData = new SceneUploadData(null, null, world, default);
        var localData = new SceneUploadData(null, null, local, default);
        using var w = host.Scene.PrepareResourceUpload(worldData, new NativeUploadReservation(host.Scene.MeasureResourceUpload(worldData)));
        using var l = host.Scene.PrepareResourceUpload(localData, new NativeUploadReservation(host.Scene.MeasureResourceUpload(localData)));
        Assert.Equal(new Vector3(.125f, .5f, .25f), MemoryMarshal.Read<Matrix4x4>(w.PreparedFoliage!.PackedInstances.Span).Translation);
        Assert.Equal(new Vector3(.123456f, .5f, .25f), MemoryMarshal.Read<Matrix4x4>(l.PreparedFoliage!.PackedInstances.Span).Translation);
        Assert.Equal((3999d, -4000d), (l.PreparedFoliage.Patches[0].CellX, l.PreparedFoliage.Patches[0].CellZ));
    }

    [Fact]
    public void GroundAndLight_UseDistinctPublishedKindsAndTheReleasedGroundPipeline()
    {
        using var host = new NativeUploadTestScene();
        var mesh = Triangle();
        for (int i = 0; i < mesh.Vertices.Length; i++) mesh.Vertices[i].Tangent.Z = 1;
        var material = new SceneGroundMaterialData(1, 1, new[] { new TileGroundLayerImage { AlbedoRgba = new byte[] { 120, 80, 40, 255 } } });
        var data = new SceneUploadData(mesh, null, null, default, material);
        var demand = host.Scene.MeasureResourceUpload(data);
        Assert.Equal(234 + 4 + TileGroundMaterialConfig.ParamsBytes, demand.TransferBytes);
        var handle = Publish(host.Scene, data);
        Assert.Equal(1, host.Scene.LiveTileGroundMaterialCount);
        host.Scene.Begin();
        long bytes = host.Device.UploadBytes;
        new SceneResourceAccess(host.Scene).DrawResourceAnchored(handle, new(default, Matrix4x4.Identity), default);
        host.Render();
        Assert.Equal(bytes, host.Device.UploadBytes);
        var lightData = new SceneUploadData(null, null, null, default);
        Assert.Equal(0, host.Scene.MeasureResourceUpload(lightData).OwnedGpuBytes);
        var light = Publish(host.Scene, lightData);
        Assert.Throws<ArgumentException>(() => new SceneResourceAccess(host.Scene).MeshOf(light));
        host.Scene.ReleaseResource(light); host.Scene.ReleaseResource(handle);
        Assert.Equal(0, host.Scene.LiveTileGroundMaterialCount);
    }

    [Fact]
    public void FactoryFailure_RetiresEveryCreatedObjectAndReleasesTheLeaseOnce()
    {
        using var host = new NativeUploadTestScene();
        var factory = (FakeGpuResourceFactory)host.Device.Inner.Factory;
        var data = new SceneUploadData(Triangle(), null, null, default);
        var reservation = new NativeUploadReservation(host.Scene.MeasureResourceUpload(data));
        int start = factory.Buffers.Count;
        factory.FailBufferCreationAfter(1);
        Assert.Throws<InvalidOperationException>(() => host.Scene.PrepareResourceUpload(data, reservation));
        factory.ThrowOnBufferCreate = 0;
        Assert.False(factory.Buffers[start].Disposed);
        host.Scene.Begin(); host.Scene.Begin(); host.Scene.Begin(); host.Scene.Begin();
        Assert.True(factory.Buffers[start].Disposed);
        Assert.Equal(0, reservation.ReservedBytes);
        Assert.Equal(1, reservation.ReleaseCount);
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Theory]
    [InlineData("colour")]
    [InlineData("motion")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    [InlineData("point")]
    public void PreparedFoliage_ProductionShadersValidateWithoutADevice(string pass)
    {
        var pair = pass switch
        {
            "colour" => (ShaderSources.PreparedFoliage(ShaderSources.FoliageVert, 2), ShaderSources.ModelFrag),
            "motion" => (ShaderSources.PreparedFoliage(ShaderSources.FoliageMotionVert, 3), ShaderSources.ModelMotionFrag),
            "cascade" => (ShaderSources.PreparedFoliage(ShaderSources.FoliageDepthVert, 3), ShaderSources.ShadowDepthFrag),
            "cutout" => (ShaderSources.PreparedFoliage(ShaderSources.FoliageDepthVert, 3), ShaderSources.FoliageDepthCutoutFrag),
            _ => (ShaderSources.PreparedPointFoliageVert, ShaderSources.PointShadowRigidFrag)
        };
        ShaderValidation.ValidatePair(pair.Item1, pair.Item2, "prepared-foliage-" + pass);
    }

    [Fact]
    public void TextureMips_ProgressByBoundedRowsWithinTheAllowance()
    {
        using var host = new NativeUploadTestScene();
        byte[] pixels = Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 200, 100, 50, 255 }).ToArray();
        var data = new SceneUploadData(Triangle(), new(new(pixels, 4, 4), null, null, 0), null, default);
        using var upload = host.Scene.PrepareResourceUpload(data, new NativeUploadReservation(host.Scene.MeasureResourceUpload(data)));
        Assert.Equal(234, upload.Advance(234));
        Assert.Empty(host.Device.TextureCopies);
        Assert.Equal(0, upload.Advance(3));
        Assert.Equal(20, upload.Advance(20));
        Assert.False(upload.Ready);
        Assert.Equal(64, upload.Advance(long.MaxValue));
        Assert.True(upload.Ready);
        Assert.Equal(84, host.Device.TextureCopies.Sum(t => t.Bytes.Length));
        Assert.Equal(new uint[] { 0, 1, 2 }, host.Device.TextureCopies.Select(t => t.Mip).Distinct());
        foreach (var copy in host.Device.TextureCopies)
        {
            Assert.Equal(copy.Width * 4, (uint)copy.Bytes.Length);
            Assert.Equal(Enumerable.Range(0, (int)copy.Width).SelectMany(_ => new byte[] { 200, 100, 50, 255 }), copy.Bytes);
        }
        var handle = upload.Publish(); host.Scene.ReleaseResource(handle);
    }

    [Fact]
    public void PublishedFoliage_SubUlpLocalReplacementInvalidatesStaticPointShadows()
    {
        using var host = new NativeUploadTestScene();
        var scene = host.Scene;
        LoadParts(scene);
        var anchor = new RenderAnchor(32000, 0, 32000);
        scene.RenderOrigin = new(32000, 0, 32000);
        scene.Camera.Frame(new(32000, 0, 32000), new Vector3(4));
        SceneUploadData Input(float x) => new(null, null, new SceneFoliageSource(anchor,
            new[] { Matrix4x4.CreateTranslation(x, 0, 0) }, new float[] { .1f }, new[] { 0 },
            NativeRenderAssetFixtures.FoliageSource().Models, SceneFoliageTransformSpace.AnchorLocal), default);
        var first = Publish(scene, Input(.0001f));
        void Frame(SceneResourceHandle resource)
        {
            scene.Begin();
            scene.DrawPreparedFoliage(resource, Vector3.Zero, new(), anchor);
            scene.AddLightAnchored(new(0, 2, 0), anchor, Color.White, 6, 1, LightShadow.Static(111));
            host.Render();
        }
        Frame(first); // Admit the atlas at its documented next frame boundary.
        Frame(first);
        Assert.Equal(1, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        scene.ReleaseResource(first);
        var replacement = Publish(scene, Input(.0002f));
        Frame(replacement);
        Assert.Equal(1, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        scene.ReleaseResource(replacement);
    }

    [Fact]
    public void PublishedFoliage_MixedCastingSharedMeshKeepsExpandedGpuSlots()
    {
        using var host = new NativeUploadTestScene();
        var scene = host.Scene;
        var mesh = scene.LoadMesh(Triangle());
        var source = new SceneFoliageSource(default,
            new[] { Matrix4x4.Identity, Matrix4x4.Identity }, new float[] { .1f, .2f }, new[] { 0, 1 },
            new SceneFoliageModelBinding[] { new("still", new[] { mesh }, default, false), new("caster", new[] { mesh }, default, true) });
        var handle = Publish(scene, new(null, null, source, default));
        void Frame()
        {
            scene.Begin();
            scene.DrawPreparedFoliage(handle, Vector3.Zero, new(), default);
            scene.AddLight(new(0, 2, 0), Color.White, 6, 1, LightShadow.Static(112));
            host.Render();
        }
        Frame(); // Admit the atlas at its documented next frame boundary.
        Frame();
        Assert.True(scene.FoliagePointShadowDrawCalls > 0);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        scene.ReleaseResource(handle);
    }

    [Fact]
    public void ReleasedFoliage_DropsPointAndFrameCacheReferencesBeforeLeaseRetirement()
    {
        using var host = new NativeUploadTestScene();
        var scene = host.Scene;
        LoadParts(scene);
        var handle = Publish(scene, new(null, null, NativeRenderAssetFixtures.FoliageSource(), default));
        void Frame()
        {
            scene.Begin(); scene.DrawPreparedFoliage(handle, Vector3.Zero, new(), default);
            scene.AddLight(new(1, 2, 1), Color.White, 6, 1, LightShadow.Static(113));
            host.Render();
        }
        Frame(); Frame();
        IEnumerable Cache(string name) => (IEnumerable)typeof(Scene3D).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        Assert.NotEmpty(Cache("_foliagePointSources").Cast<object>());
        scene.ReleaseResource(handle);
        foreach (string name in new[] { "_foliagePointSources", "_foliagePointPending", "_foliagePointSourceOrder", "_foliagePointFaceRuns", "_foliageDraws", "_foliageShadowDraws" })
            Assert.Empty(Cache(name).Cast<object>());
    }

    internal static GltfMesh Triangle() => new(new ModelVertex[]
    {
        new(new(0, 0, 0), Vector3.UnitY, Vector4.One), new(new(1, 0, 0), Vector3.UnitY, Vector4.One),
        new(new(0, 0, 1), Vector3.UnitY, Vector4.One)
    }, new uint[] { 0, 2, 1 });
    internal static void LoadParts(Scene3D scene)
    {
        foreach (var part in NativeRenderAssetFixtures.FoliageParts()) scene.LoadMesh(part.Mesh, part.Maps);
    }
    internal static SceneResourceHandle Publish(Scene3D scene, SceneUploadData data)
    {
        using var upload = scene.PrepareResourceUpload(data, new NativeUploadReservation(scene.MeasureResourceUpload(data)));
        upload.Advance(long.MaxValue);
        return upload.Publish();
    }
}

internal sealed class NativeUploadReservation(SceneUploadDemand demand) : ISceneUploadReservation
{
    public SceneUploadDemand Demand { get; } = demand;
    internal bool Deny;
    internal long ReservedBytes;
    internal int ReleaseCount, DisposeCount;
    public void Claim(SceneUploadDemand value)
    {
        Assert.Equal(Demand, value);
        if (Deny) throw new InvalidOperationException("Admission denied.");
        ReservedBytes = value.OwnedGpuBytes + value.PeakStagingGpuBytes + value.PeakCpuBytes;
    }
    public void Published(SceneResourceHandle handle) => Assert.True(handle.Value > 0);
    public void TransferCompleted() => ReservedBytes -= Demand.PeakStagingGpuBytes;
    public void InputsReleased(long retainedCpuBytes) => ReservedBytes -= Demand.PeakCpuBytes - retainedCpuBytes;
    public void Retiring() { }
    public void Released() { ReleaseCount++; ReservedBytes = 0; }
    public void Dispose() => DisposeCount++;
}
