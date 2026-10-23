using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqCoverageIdentityTests
{
    // Losing coverage metadata at the ordinary snapshot boundary, or retaining any caller array,
    // disconnects conformation from the source actually uploaded and used by ordinary history.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Coverage_and_ordinary_consumers_share_the_retained_source_after_caller_mutation(bool dq)
    {
        using var device = new CoverageCaptureDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm))
        { FrustumCulling = false, ForceTemporalForTests = true };
        scene.Post.Starfield = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Frame(Vector3.Zero, new Vector3(4, 3, 4));
        using var colour = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, colour);
        CoverageFixture fixture = CoverageFixture.CurvedPanel(seams: false);
        var skeleton = new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1]);
        SkinningProfile profile = dq ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend;
        SkinnedGltfMesh initial = Mesh(profile);
        SkinnedGltfMesh caller = dq ? initial : new SkinnedGltfMesh(initial.Vertices, initial.Indices32,
            initial.InverseBind, initial.RestPose, skeleton, profile)
        { Coverage = HostCoveragePart.Create(fixture.Map, fixture.Source, 0, initial) };
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(caller);
        object entry = Entry(handle);
        var retained = Assert.IsType<SkinnedGltfMesh>(Field(entry, "Source"));
        byte[] sourceWords = Words(retained.Vertices);
        byte[] indexWords = Words(retained.Indices32);
        byte[] bindWords = Words(retained.InverseBind);
        byte[] restWords = Words(retained.RestPose);
        var upload = Assert.IsAssignableFrom<IGpuBuffer>(Field(entry, "Vb"));
        Assert.Equal(sourceWords, device.Uploads[upload]);
        var bounds = Assert.IsType<MeshBounds>(Field(entry, "Bounds"));
        var identity = new SkinnedMotionIdentity(handle.Index, handle.Generation,
            ((ulong)(uint)handle.Generation << 32) | (uint)handle.Index, profile);
        MotionKey key = MotionKey.From(701);
        Render(gpu: false);
        var cpu = (List<ModelVertex>)Field(scene, "_cpuSkinnedVerts");
        byte[] firstSurface = Words(cpu.ToArray());
        Assert.True(scene.ActiveMotionHistory!.TryGetCurrentBounds(key, out MeshBounds firstBounds));

        caller.Vertices[0].Position = new Vector3(99, 88, 77);
        caller.Vertices[0].BoneWeights = Vector4.UnitY;
        caller.Indices32[0] = caller.Indices32[1];
        caller.InverseBind[1] = Matrix4x4.CreateTranslation(9, 8, 7);
        caller.RestPose[1] = Matrix4x4.CreateTranslation(6, 5, 4);
        skeleton.ParentIndices[1] = -1;
        SkinnedGltfMesh replacement = Mesh(dq ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle replacementHandle = scene.LoadSkinnedMesh(replacement);
        Assert.NotEqual(handle, replacementHandle);
        Assert.NotSame(retained, Field(Entry(replacementHandle), "Source"));
        Assert.Equal(profile, Field(entry, "Profile"));
        Assert.Equal(handle, Field(entry, "SourceIdentity"));
        Assert.Equal(sourceWords, Words(retained.Vertices));
        Assert.Equal(indexWords, Words(retained.Indices32));
        Assert.Equal(bindWords, Words(retained.InverseBind));
        Assert.Equal(restWords, Words(retained.RestPose));
        Assert.Equal(0, retained.Skeleton!.ParentIndices[1]);
        Assert.Equal(bounds, Field(entry, "Bounds"));
        Assert.Equal(sourceWords, device.Uploads[upload]);

        Render(gpu: true);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(key, identity, out _, out var lbs, out var dual));
        if (dq) Assert.Equal(Vector4.Zero, dual[1].Dual);
        else Assert.Equal(Matrix4x4.Identity, lbs[1]);
        Assert.True(scene.ActiveMotionHistory.TryGetCurrentBounds(key, out MeshBounds currentBounds));
        Assert.Equal(firstBounds, currentBounds);
        Render(gpu: false);
        Assert.Equal(firstSurface, Words(cpu.ToArray()));

        if (!dq)
        {
            var hosts = (Dictionary<(int, int), SkinnedGltfMesh>)Field(scene, "_coverageHostUploads");
            Assert.Same(retained, hosts[(handle.Index, handle.Generation)]);
            Assert.Same(caller.Coverage, retained.Coverage);
            Assert.True(retained.Coverage!.Matches(retained));
            Assert.False(retained.Coverage.Matches(caller));
            Assert.Equal(sourceWords, Words(retained.Coverage.Source.SkinVertices.ToArray()));
            // The caller's rig changed too. Conformation must use the retained upload, not revalidate the caller.
            using var conformer = scene.CreateHostConformer(retained.Skeleton, [handle]);
            Assert.Equal(fixture.Source.SkinVertices.Length, conformer.Topology.Source.SkinVertices.Length);
        }

        scene.UnloadSkinnedMesh(handle);
        SkinnedMeshHandle recycled = scene.LoadSkinnedMesh(replacement);
        Assert.Equal(handle.Index, recycled.Index);
        Assert.NotEqual(handle.Generation, recycled.Generation);
        Assert.NotEqual(Field(entry, "SourceIdentity"), Field(Entry(recycled), "SourceIdentity"));

        SkinnedGltfMesh Mesh(SkinningProfile selected) => new(fixture.Source.SkinVertices.ToArray(),
            fixture.Source.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray(),
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity], skeleton, selected);
        object Entry(SkinnedMeshHandle mesh) => ((IList)Field(scene, "_skinnedMeshes"))[mesh.Index]!;
        void Render(bool gpu)
        {
            scene.UseGpuSkinning = gpu;
            scene.Begin();
            scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = key },
                [Matrix4x4.Identity, Matrix4x4.Identity]);
            scene.PrepareFrame();
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            scene.RenderInternal(commands, 32, 24, target);
        }
    }

    [Theory]
    [InlineData("vertex")]
    [InlineData("index")]
    [InlineData("bind")]
    [InlineData("profile")]
    public void Stale_coverage_metadata_refuses_before_allocating_an_upload(string change)
    {
        CoverageFixture fixture = CoverageFixture.CurvedPanel(seams: false);
        var mesh = new SkinnedGltfMesh(fixture.Source.SkinVertices.ToArray(),
            fixture.Source.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray(),
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity]);
        HostCoveragePart part = HostCoveragePart.Create(fixture.Map, fixture.Source, 0, mesh);
        if (change == "vertex") mesh.Vertices[0].Position += Vector3.UnitX;
        if (change == "index") mesh.Indices32[0] = mesh.Indices32[1];
        if (change == "bind") mesh.InverseBind[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        var supplied = new SkinnedGltfMesh(mesh.Vertices, mesh.Indices32, mesh.InverseBind, mesh.RestPose, null,
            change == "profile" ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend)
        { Coverage = part };
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        int buffers = ((FakeGpuResourceFactory)device.Factory).Buffers.Count;
        if (change == "profile")
        {
            Assert.Throws<NotSupportedException>(() => HostCoveragePart.Create(fixture.Map, fixture.Source, 0, supplied));
            Assert.Throws<NotSupportedException>(() => scene.LoadSkinnedMesh(supplied));
        }
        else Assert.Throws<InvalidDataException>(() => scene.LoadSkinnedMesh(supplied));
        Assert.Equal(buffers, ((FakeGpuResourceFactory)device.Factory).Buffers.Count);
        Assert.Equal(0, scene.LiveSkinnedMeshCount);
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
    }

    static byte[] Words<T>(T[] values) where T : unmanaged => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    static object Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
}
