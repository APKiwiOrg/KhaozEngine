using System;
using System.Collections;
using System.Collections.Generic;
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

public sealed class DqSubmissionTests
{
    [Theory]
    [InlineData("joint", true)]
    [InlineData("count", true)]
    [InlineData("overflow", true)]
    [InlineData("consumer", true)]
    [InlineData("joint", false)]
    [InlineData("count", false)]
    [InlineData("overflow", false)]
    [InlineData("consumer", false)]
    public void Rejected_DQ_pose_changes_no_queue_palette_or_history(string failure, bool gpu)
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        scene.UseGpuSkinning = gpu;
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh dq = Mesh(SkinningProfile.DualQuaternionRigid);
        if (failure == "overflow") dq.InverseBind[1] = Matrix4x4.CreateTranslation(float.MaxValue, 0, 0);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(dq);
        Seed(scene);
        Matrix4x4[] joints = (Matrix4x4[])dq.RestPose.Clone();
        if (failure == "joint") joints[1] = Matrix4x4.CreateScale(2);
        if (failure == "count") joints = joints[..1];
        if (failure == "overflow") joints[1] = Matrix4x4.CreateTranslation(float.MaxValue, 0, 0);
        var draw = new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = MotionKey.From(9) };

        AssertAtomic(scene, () =>
        {
            Exception error = failure == "consumer"
                ? Assert.Throws<NotSupportedException>(() => scene.DrawSkinned(draw, joints))
                : Assert.Throws<ArgumentException>(() => scene.DrawSkinned(draw, joints));
            Assert.Contains("DualQuaternionRigid", error.Message);
            Assert.Contains($"{handle.Index}:{handle.Generation}", error.Message);
            if (failure is "joint" or "overflow")
            {
                Assert.Contains("joint 1", error.Message);
                Assert.Contains("<unnamed>", error.Message);
            }
            if (failure == "count") Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
            if (failure == "overflow") Assert.Contains("finite", error.Message);
            if (failure == "consumer") Assert.Contains("consumer", error.Message);
        });

        scene.UnloadSkinnedMesh(handle);
        SkinnedMeshHandle replacement = scene.LoadSkinnedMesh(dq);
        Assert.Equal(handle.Index, replacement.Index);
        Assert.NotEqual(handle.Generation, replacement.Generation);
        AssertAtomic(scene, () => scene.DrawSkinned(draw, joints));
    }

    [Theory]
    [InlineData(float.NaN, MeshOutlineOcclusion.SceneDepth, "widthPixels")]
    [InlineData(float.PositiveInfinity, MeshOutlineOcclusion.SceneDepth, "widthPixels")]
    [InlineData(float.NegativeInfinity, MeshOutlineOcclusion.SceneDepth, "widthPixels")]
    [InlineData(0.49f, MeshOutlineOcclusion.SceneDepth, "widthPixels")]
    [InlineData(8.01f, MeshOutlineOcclusion.SceneDepth, "widthPixels")]
    [InlineData(1f, (MeshOutlineOcclusion)2, "occlusion")]
    [InlineData(0f, (MeshOutlineOcclusion)2, "widthPixels")]
    public void Invalid_outline_style_leaves_no_prepared_state(
        float widthPixels, MeshOutlineOcclusion occlusion, string parameter)
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.LinearBlend);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();

        AssertAtomic(scene, () =>
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                scene.BeginMeshOutline(Color.White, widthPixels, occlusion));
            Assert.Equal(parameter, error.ParamName);
        });
        AssertAtomic(scene, () =>
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                scene.DrawSkinnedOutline(handle, mesh.RestPose, Matrix4x4.Identity, Color.White,
                    widthPixels, occlusion));
            Assert.Equal(parameter, error.ParamName);
        });
        Assert.Equal(0, PreparedCount(scene));
    }

    [Fact]
    public void Upload_retains_profile_and_source_generation()
    {
        using var device = new UploadDevice();
        using var scene = Scene(device);
        SkinnedGltfMesh original = Mesh(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle first = scene.LoadSkinnedMesh(original);
        object entry = Entry(scene, first);
        SkinnedGltfMesh snapshot = Assert.IsType<SkinnedGltfMesh>(Field(entry, "Source"));
        Assert.Equal(SkinningProfile.DualQuaternionRigid, Field(entry, "Profile"));
        Assert.Equal(first, Field(entry, "SourceIdentity"));
        Assert.NotSame(original.Vertices, snapshot.Vertices);
        Assert.NotSame(original.InverseBind, snapshot.InverseBind);
        Assert.Same(snapshot.Vertices, ((IList)Field(scene, "_skinnedCpuVerts"))[first.Index]);

        original.Vertices[0].Position = new Vector3(99);
        original.Vertices[0].BoneIndices.W = float.NaN;
        original.InverseBind[1] = Matrix4x4.CreateScale(99);
        Assert.Equal(Vector3.UnitX, snapshot.Vertices[0].Position);
        Assert.Equal(0f, snapshot.Vertices[0].BoneIndices.W);
        Assert.Equal(Matrix4x4.Identity, snapshot.InverseBind[1]);
        IGpuBuffer vertexBuffer = Assert.IsAssignableFrom<IGpuBuffer>(Field(entry, "Vb"));
        Assert.Equal(device.Uploads[vertexBuffer], MemoryMarshal.AsBytes(snapshot.Vertices.AsSpan()).ToArray());
        Assert.IsType<DqBoundsSource>(Field(entry, "DqBounds"));
        var store = new PreparedSkinningStore();
        PreparedSkinningPose prepared = store.Prepare(snapshot,
            [Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 2, 3)], new SkinningValidationContext("snapshot", null, 0));
        Assert.Equal(new Vector4(.5f, 1, 1.5f, -0f), store.Dq(prepared)[1].Dual);

        scene.UnloadSkinnedMesh(first);
        SkinnedMeshHandle second = scene.LoadSkinnedMesh(Mesh(SkinningProfile.LinearBlend));
        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(second, Field(Entry(scene, second), "SourceIdentity"));
        Assert.Equal(SkinningProfile.LinearBlend, Field(Entry(scene, second), "Profile"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Atlas_replacement_preserves_source_profile_and_generation(bool textured)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 2048, ShadowCascadeCount = 3 });
        SkinnedGltfMesh source = Mesh(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = textured
            ? scene.LoadSkinnedMesh(source, scene.LoadTexture([255, 255, 255, 255], 1, 1))
            : scene.LoadSkinnedMesh(source);
        object before = Entry(scene, handle);

        Assert.True(scene.ReplaceShadowLayout(3072, 4));

        object after = Entry(scene, handle);
        Assert.NotSame(before, after);
        Assert.Same(Field(before, "Source"), Field(after, "Source"));
        Assert.Same(Field(before, "InverseBind"), Field(after, "InverseBind"));
        Assert.Equal(SkinningProfile.DualQuaternionRigid, Field(after, "Profile"));
        Assert.Equal(handle, Field(after, "SourceIdentity"));
        Assert.Equal(Field(before, "DqBounds"), Field(after, "DqBounds"));
        scene.Begin();
        AssertAtomic(scene, () => Assert.Throws<NotSupportedException>(() =>
            scene.DrawSkinned(handle, source.RestPose, Matrix4x4.Identity, Color.White)));
    }

    [Theory]
    [InlineData("joint")]
    [InlineData("count")]
    [InlineData("overflow")]
    [InlineData("consumer")]
    public void Invalid_outline_pose_is_atomic_without_colour_draw(string failure)
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.DualQuaternionRigid);
        if (failure == "overflow") mesh.InverseBind[1] = Matrix4x4.CreateTranslation(float.MaxValue, 0, 0);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1);
        Matrix4x4[] joints = (Matrix4x4[])mesh.RestPose.Clone();
        if (failure == "joint") joints[1] = Matrix4x4.CreateScale(2);
        if (failure == "count") joints = joints[..1];
        if (failure == "overflow") joints[1] = Matrix4x4.CreateTranslation(float.MaxValue, 0, 0);

        void Reject(Action submit) => AssertAtomic(scene, () =>
        {
            Exception error = failure == "consumer"
                ? Assert.Throws<NotSupportedException>(submit)
                : Assert.Throws<ArgumentException>(submit);
            Assert.Contains("DualQuaternionRigid", error.Message);
            if (failure is "joint" or "overflow") Assert.Contains("joint 1", error.Message);
        });

        Reject(() => scene.DrawSkinnedOutline(group, handle, joints, Matrix4x4.Identity));
        Reject(() => scene.DrawSkinnedOutlineDissolved(group, handle, joints, Matrix4x4.Identity, .5f, true));
        Reject(() => scene.DrawSkinnedOutline(handle, joints, Matrix4x4.Identity, Color.White, 1));
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Upload_revalidates_zero_weight_indices_without_restricting_LBS()
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        var factory = (FakeGpuResourceFactory)device.Factory;
        SkinnedGltfMesh dq = Mesh(SkinningProfile.DualQuaternionRigid);
        dq.Vertices[0].BoneIndices.W = 2;
        int before = factory.Buffers.Count;

        ArgumentException error = Assert.Throws<ArgumentException>(() => scene.LoadSkinnedMesh(dq));

        Assert.Contains("BoneIndices[3]", error.Message);
        Assert.Contains("DualQuaternionRigid", error.Message);
        Assert.Equal(before, factory.Buffers.Count);
        Assert.Equal(0, scene.LiveSkinnedMeshCount);
        SkinnedGltfMesh lbs = Mesh(SkinningProfile.LinearBlend);
        lbs.Vertices[0].BoneIndices.W = 2;
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(lbs);
        scene.Begin();
        scene.DrawSkinned(handle, [Matrix4x4.CreateScale(2), Matrix4x4.CreateScale(3)], Matrix4x4.Identity, Color.White);
        Assert.Single(scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Failed_upload_disposes_partial_resources_and_publishes_no_handle()
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        var factory = (FakeGpuResourceFactory)device.Factory;
        int before = factory.Buffers.Count;
        factory.FailBufferCreationAfter(1);

        Assert.Throws<InvalidOperationException>(() => scene.LoadSkinnedMesh(Mesh(SkinningProfile.DualQuaternionRigid)));

        Assert.True(factory.Buffers[before].Disposed);
        Assert.Equal(0, scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Failed_index_upload_disposes_both_partial_buffers()
    {
        using var device = new UploadDevice();
        using var scene = Scene(device);
        var factory = (FakeGpuResourceFactory)device.Factory;
        int before = factory.Buffers.Count;
        device.RejectIndexUpload = true;

        Assert.Throws<InvalidOperationException>(() => scene.LoadSkinnedMesh(Mesh(SkinningProfile.DualQuaternionRigid)));

        Assert.Equal(before + 2, factory.Buffers.Count);
        Assert.True(factory.Buffers[before].Disposed);
        Assert.True(factory.Buffers[before + 1].Disposed);
        Assert.Equal(0, scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Submitted_words_are_owned_and_Begin_discards_prepared_poses()
    {
        using var device = new FakeGpuDevice();
        using var scene = Scene(device);
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.LinearBlend);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] joints = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 2, 3)];
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = MotionKey.From(1) }, joints);
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1);
        scene.DrawSkinnedOutline(group, handle, joints, Matrix4x4.Identity);
        joints[1] = Matrix4x4.CreateScale(99);

        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), scene.BonePaletteSlotForTests(0)[1]);
        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), scene.OutlinePoseMatrixAt(1));
        Assert.True(PreparedCount(scene) > 0);
        scene.Begin();
        Assert.Equal(0, PreparedCount(scene));
        Assert.Empty((IList)Field(scene, "_boneMatrices"));
        Assert.Equal(0, scene.OutlinePoseMatrixCount);
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
        Assert.True(scene.ActiveMotionHistory!.TryGetPreviousSkinned(MotionKey.From(1), out _, out var previous));
        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), previous[1]);
        scene.DrawSkinned(handle, mesh.RestPose, Matrix4x4.Identity, Color.White);
        Assert.Equal(Matrix4x4.Identity, scene.BonePaletteSlotForTests(0)[1]);
    }

    [Fact]
    public void Prepared_DQ_words_survive_caller_edits_and_interleaved_appends_until_reset()
    {
        var store = new PreparedSkinningStore();
        var other = new PreparedSkinningStore();
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.DualQuaternionRigid);
        var context = new SkinningValidationContext("public synthetic source", null, 0);
        Matrix4x4[] joints = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 2, 3)];
        PreparedSkinningPose first = store.Prepare(mesh, joints, context);
        ReadOnlySpan<RigidDualQuaternion> words = store.Dq(first);
        RigidDualQuaternion[] before = words.ToArray();
        joints[1] = Matrix4x4.CreateScale(99);
        PreparedSkinningPose lbs = store.Prepare(Mesh(SkinningProfile.LinearBlend), joints, context);
        PreparedSkinningPose second = store.Prepare(mesh, mesh.RestPose, context);

        Assert.Equal(SkinningProfile.DualQuaternionRigid, first.Profile);
        Assert.Equal(2, first.BoneCount);
        Assert.Equal(0, first.Offset);
        Assert.Equal(2, second.Offset);
        Assert.Equal(0, lbs.Offset);
        Assert.Equal(3, store.PoseCount);
        Assert.Equal(2, store.LbsCount);
        Assert.Equal(4, store.DqCount);
        Assert.Equal(new Vector4(0, 0, 0, 1), words[1].Real);
        Assert.Equal(new Vector4(.5f, 1, 1.5f, -0f), words[1].Dual);
        Assert.Equal(before, store.Dq(first).ToArray());
        Assert.Equal(before, words.ToArray());
        Assert.Equal(Matrix4x4.CreateScale(99), store.Lbs(lbs)[1]);
        Assert.Throws<InvalidOperationException>(() => { _ = store.Lbs(first); });
        Assert.Throws<InvalidOperationException>(() => { _ = other.Dq(first); });
        store.Reset();
        PreparedSkinningPose nextFrame = store.Prepare(mesh, mesh.RestPose, context);
        Assert.Equal(0, nextFrame.Offset);
        Assert.Throws<InvalidOperationException>(() => { _ = store.Dq(first); });
        Assert.Equal(Vector4.Zero, store.Dq(nextFrame)[1].Dual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepared_store_rejects_a_late_joint_or_unrepresentable_envelope_without_an_append(bool envelope)
    {
        var store = new PreparedSkinningStore();
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.DualQuaternionRigid);
        var context = new SkinningValidationContext("public synthetic source", "bend", 7);
        PreparedSkinningPose accepted = store.Prepare(mesh, mesh.RestPose, context);
        RigidDualQuaternion[] before = store.Dq(accepted).ToArray();
        Matrix4x4[] joints = [Matrix4x4.Identity, envelope
            ? Matrix4x4.CreateTranslation(float.MaxValue / 16, 0, 0) : Matrix4x4.CreateScale(2)];

        ArgumentException error = Assert.Throws<ArgumentException>(() => store.Prepare(mesh, joints, context));

        Assert.Contains("public synthetic source", error.Message);
        Assert.Contains("bend", error.Message);
        Assert.Contains("sample 7", error.Message);
        Assert.Contains(envelope ? "envelope" : "joint 1", error.Message);
        Assert.Equal(1, store.PoseCount);
        Assert.Equal(2, store.DqCount);
        Assert.Equal(0, store.LbsCount);
        Assert.Equal(before, store.Dq(accepted).ToArray());
    }

    static void Seed(Scene3D scene)
    {
        SkinnedGltfMesh mesh = Mesh(SkinningProfile.LinearBlend);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity) { Motion = MotionKey.From(1) }, mesh.RestPose);
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1);
        scene.DrawSkinnedOutline(group, handle, mesh.RestPose, Matrix4x4.Identity);
    }

    static void AssertAtomic(Scene3D scene, Action submit)
    {
        int queueBefore = scene.QueuedSkinnedInstancesForTests.Count;
        var palette = (IList)Field(scene, "_boneMatrices");
        int paletteCountBefore = palette.Count;
        int historyCountBefore = scene.MotionKeyCounts.KeyedSkinned;
        var historyBefore = scene.MotionKeyCounts;
        var store = (PreparedSkinningStore)Field(scene, "_preparedSkinning");
        var preparedBefore = (store.PoseCount, store.LbsCount, store.DqCount);
        object[] paletteBefore = new object[palette.Count];
        palette.CopyTo(paletteBefore, 0);
        int groupsBefore = scene.MeshOutlineGroupCount, partsBefore = scene.MeshOutlinePartCount;
        int outlineBefore = scene.OutlinePoseMatrixCount;
        submit();
        Assert.Equal(queueBefore, scene.QueuedSkinnedInstancesForTests.Count);
        Assert.Equal(paletteCountBefore, palette.Count);
        Assert.Equal(historyCountBefore, scene.MotionKeyCounts.KeyedSkinned);
        Assert.Equal(historyBefore, scene.MotionKeyCounts);
        Assert.Equal(preparedBefore, (store.PoseCount, store.LbsCount, store.DqCount));
        Assert.Equal(paletteBefore, System.Linq.Enumerable.Cast<object>(palette));
        Assert.Equal(groupsBefore, scene.MeshOutlineGroupCount);
        Assert.Equal(partsBefore, scene.MeshOutlinePartCount);
        Assert.Equal(outlineBefore, scene.OutlinePoseMatrixCount);
    }

    static int PreparedCount(Scene3D scene)
        => ((PreparedSkinningStore)Field(scene, "_preparedSkinning")).PoseCount;

    static object Entry(Scene3D scene, SkinnedMeshHandle handle) =>
        ((IList)Field(scene, "_skinnedMeshes"))[handle.Index]!;

    static object Field(object owner, string name)
    {
        FieldInfo? field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(field);
        return field.GetValue(owner)!;
    }

    static Scene3D Scene(IGpuDevice device) => new(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    static SkinnedGltfMesh Mesh(SkinningProfile profile) => new(
        [DqFixtureMeshes.Vertex(Vector3.UnitX, new Vector4(.5f, .5f, 0, 0))],
        new ushort[] { 0, 0, 0 }, [Matrix4x4.Identity, Matrix4x4.Identity],
        [Matrix4x4.Identity, Matrix4x4.Identity], null, profile);

    sealed class UploadDevice : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();
        internal Dictionary<IGpuBuffer, byte[]> Uploads { get; } = new();
        internal bool RejectIndexUpload { get; set; }
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => _inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }
        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> completions) => _inner.Submit(cl, completions);
        public void WaitForIdle() => _inner.WaitForIdle();
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            if (RejectIndexUpload && (typeof(T) == typeof(ushort) || typeof(T) == typeof(uint)))
                throw new InvalidOperationException("Injected index upload failure.");
            if (typeof(T) == typeof(SkinnedVertex)) Uploads[buffer] = MemoryMarshal.AsBytes(data).ToArray();
            _inner.UpdateBuffer(buffer, offsetBytes, data);
        }
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, T[] data) where T : unmanaged =>
            UpdateBuffer<T>(buffer, offsetBytes, data.AsSpan());
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, in T data) where T : unmanaged =>
            UpdateBuffer(buffer, offsetBytes, new[] { data });
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) =>
            _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer) => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);
        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
        public void ResizeSwapchain(uint width, uint height) => _inner.ResizeSwapchain(width, height);
        public void Present() => _inner.Present();
        public void Dispose() => _inner.Dispose();
    }
}
