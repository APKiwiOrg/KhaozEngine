using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqPaletteLayoutTests
{
    [Fact]
    public void DQ_slots_have_explicit_stride_ranges_and_alignment()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var frame = device.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer));
        using var current = new DualQuaternionPaletteBuffer(device, retired);
        using var previous = new DualQuaternionPaletteBuffer(device, retired, frame);
        Assert.Equal(4096u, DualQuaternionPaletteBuffer.SlotBytes);
        Assert.Equal(4160u, SkinnedMotionPalette.DqPayloadBytes);
        Assert.Equal(4352u, SkinnedMotionPalette.DqSlotBytes);
        current.EnsureCapacity(2);
        previous.EnsureCapacity(2);
        RigidDualQuaternion[] words = Words();
        Matrix4x4 world = Matrix4x4.CreateTranslation(7, 8, 9);
        current.Pack(1, words);
        previous.PackPrevious(1, world, words);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        long currentBytes = current.Upload(commands);
        long previousBytes = previous.Upload(commands);
        Assert.Equal(2, commands.Uploads.Count);
        var a = commands.Uploads[0];
        var b = commands.Uploads[1];
        Assert.True(a.IsWholeBuffer);
        Assert.True(b.IsWholeBuffer);
        Assert.Equal(32768L, currentBytes);
        Assert.Equal(34816L, previousBytes);
        Assert.Equal(a.Bytes, (uint)currentBytes);
        Assert.Equal(b.Bytes, (uint)previousBytes);
        Assert.True(currentBytes > 2 * 128 * 32);
        Assert.Equal(4096u, current.OffsetFor(1));
        Assert.Equal(4352u, previous.OffsetFor(1));
        Assert.Equal(0u, previous.OffsetFor(1) % 256);
        AssertRange(current.Set, a.Buffer, 4096);
        AssertRange(previous.Set, b.Buffer, 4352);
        Assert.Same(frame, Assert.IsType<FakeResourceSet>(previous.Set).Resources[0]);
        AssertWords(a.Data!, 4096, words);
        Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(b.Data!.AsSpan(4352)));
        AssertWords(b.Data!, 4416, words);
        Assert.All(b.Data!.Skip(8512).Take(192), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Mixed_methods_do_not_alias()
    {
        using var harness = new MotionTestScene();
        var renderer = Assert.IsType<ModelRenderer>(typeof(Scene3D).GetField("_model",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(harness.Scene));
        var prepared = new PreparedSkinningStore();
        SkinnedGltfMesh lbsMesh = Mesh(SkinningProfile.LinearBlend);
        SkinnedGltfMesh dqMesh = Mesh(SkinningProfile.DualQuaternionRigid);
        Matrix4x4[] first = { Matrix4x4.CreateTranslation(2, 4, 6) };
        Matrix4x4[] second = { Matrix4x4.CreateTranslation(3, 5, 7) };
        PreparedSkinningPose l0 = prepared.Prepare(lbsMesh, first, new("lbs0", null, 0));
        PreparedSkinningPose d0 = prepared.Prepare(dqMesh, first, new("dq0", null, 0));
        PreparedSkinningPose l1 = prepared.Prepare(lbsMesh, second, new("lbs1", null, 0));
        PreparedSkinningPose d1 = prepared.Prepare(dqMesh, second, new("dq1", null, 0));
        renderer.EnsureSkinnedBonePaletteCapacity(2, SkinningMethod.LinearBlend);
        renderer.EnsureSkinnedBonePaletteCapacity(2, SkinningMethod.DualQuaternionRigid);
        // Interleaved submissions include a culled actor of each method. They must not consume a palette slot.
        PreparedSkinningPose[] submissions = { l0, d0, l0, d0, l1, d1 };
        bool[] retained = { false, false, true, true, true, true };
        uint lbsSlots = 0, dqSlots = 0;
        var slots = new System.Collections.Generic.List<PreparedSkinningSlot>();
        for (int i = 0; i < submissions.Length; i++)
        {
            if (!retained[i]) continue;
            var draw = new PreparedSkinningSlot(submissions[i], ref lbsSlots, ref dqSlots);
            slots.Add(draw);
            renderer.PackPreparedSkinningPalette(draw.Slot, draw.Pose);
        }
        Assert.Equal(new uint[] { 0, 0, 1, 1 }, slots.Select(draw => draw.Slot));
        Assert.Equal(2u, lbsSlots);
        Assert.Equal(2u, dqSlots);
        Assert.Equal(l0, slots[0].Pose);
        Assert.Equal(d0, slots[1].Pose);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        Assert.Equal(65536L, renderer.UploadSkinningPalette(commands, SkinningMethod.LinearBlend));
        Assert.Equal(32768L, renderer.UploadSkinningPalette(commands, SkinningMethod.DualQuaternionRigid));
        var a = commands.Uploads[0];
        var b = commands.Uploads[1];
        Assert.NotSame(a.Buffer, b.Buffer);
        Assert.Equal(8192u, SkinnedBonePalette.SlotBytes);
        Assert.Equal(first[0], MemoryMarshal.Read<Matrix4x4>(a.Data!));
        Assert.Equal(second[0], MemoryMarshal.Read<Matrix4x4>(a.Data!.AsSpan(8192)));
        AssertWords(b.Data!, 0, prepared.Dq(d0).ToArray());
        AssertWords(b.Data!, 4096, prepared.Dq(d1).ToArray());
        first[0] = Matrix4x4.Identity;
        Assert.Equal(2f, prepared.Lbs(l0)[0].M41);
        Assert.Equal(1f, prepared.Dq(d0)[0].Dual.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Growth_preserves_all_payloads_and_retires_resources(bool motion)
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        using IGpuBuffer? frame = motion ? device.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)) : null;
        using var palette = new DualQuaternionPaletteBuffer(device, retired, frame);
        palette.EnsureCapacity(2);
        RigidDualQuaternion[] words = Words();
        RigidDualQuaternion[] secondWords = Words(1000);
        Matrix4x4 world = Matrix4x4.CreateTranslation(7, 8, 9);
        if (motion)
        {
            palette.PackPrevious(0, world, words);
            palette.PackPrevious(1, world, secondWords);
        }
        else
        {
            palette.Pack(0, words);
            palette.Pack(1, secondWords);
        }
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        palette.Upload(commands);
        var oldBuffer = Assert.IsType<FakeBuffer>(commands.Uploads[0].Buffer);
        var oldSet = Assert.IsType<FakeResourceSet>(palette.Set);
        palette.EnsureCapacity(9);
        palette.Upload(commands);
        Assert.Equal(motion ? 69632u : 65536u, commands.Uploads[1].Bytes);
        AssertWords(commands.Uploads[1].Data!, motion ? 64 : 0, words);
        AssertWords(commands.Uploads[1].Data!, motion ? 4416 : 4096, secondWords);
        if (motion) Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(commands.Uploads[1].Data!));
        Assert.False(oldBuffer.Disposed);
        Assert.False(oldSet.Disposed);
        Assert.Equal(2, retired.PendingCount);
        retired.BeginFrame();
        Assert.False(oldBuffer.Disposed);
        retired.BeginFrame();
        Assert.True(oldBuffer.Disposed);
        Assert.True(oldSet.Disposed);
        int buffers = factory.Buffers.Count;
        int sets = factory.ResourceSets.Count;
        palette.EnsureCapacity(2);
        palette.EnsureCapacity(9);
        palette.EnsureCapacity(1);
        Assert.Equal(buffers, factory.Buffers.Count);
        Assert.Equal(sets, factory.ResourceSets.Count);
        Assert.Single(factory.Buffers, buffer => !buffer.Disposed && !ReferenceEquals(buffer, frame));
        palette.Dispose();
        Assert.All(factory.Buffers.Where(buffer => !ReferenceEquals(buffer, frame)), buffer => Assert.True(buffer.Disposed));
        Assert.All(factory.ResourceSets, set => Assert.True(set.Disposed));
    }

    [Fact]
    public void Scene_counts_the_full_current_and_header_upload_after_culling()
    {
        using var harness = new MotionTestScene();
        SkinnedMeshHandle handle = harness.LoadTube(out SkinnedGltfMesh mesh);
        harness.Scene.Begin();
        harness.Scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity), mesh.RestPose);
        harness.Scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(100000, 0, 0)), mesh.RestPose);
        Matrix4x4[] finalPose = (Matrix4x4[])mesh.RestPose.Clone();
        finalPose[0] = Matrix4x4.CreateTranslation(9, 0, 0) * finalPose[0];
        harness.Scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(1, 0, 0)), finalPose);
        finalPose[0] = Matrix4x4.Identity;
        harness.Scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        harness.Scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        var palette = Assert.Single(commands.Uploads, upload => upload.Bytes == 65536);
        var headers = Assert.Single(commands.Uploads, upload => upload.Bytes == 2048);
        Assert.True(palette.IsWholeBuffer);
        Assert.True(headers.IsWholeBuffer);
        Assert.Equal(67584L, harness.Scene.LastFrameStats.SkinnedUniformUploadBytes);
        Assert.Equal(0f, MemoryMarshal.Read<Matrix4x4>(palette.Data!).M41);
        Assert.Equal(9f, MemoryMarshal.Read<Matrix4x4>(palette.Data!.AsSpan(8192)).M41);
        Assert.Equal(default(Matrix4x4), MemoryMarshal.Read<Matrix4x4>(palette.Data!.AsSpan(16384)));
        Assert.Equal(1f, MemoryMarshal.Read<Matrix4x4>(headers.Data!.AsSpan(256)).M41);
        Assert.Equal(0f, MemoryMarshal.Read<Matrix4x4>(headers.Data!.AsSpan(512)).M41);
    }

    [Fact]
    public void Method_facades_keep_previous_and_outline_ranges_separate()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var frame = device.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer));
        using var lbs = new SkinnedMotionPalette(device, frame, retired);
        using var dq = new SkinnedMotionPalette(device, frame, retired, SkinningMethod.DualQuaternionRigid);
        using var outlines = new TargetOutlineSkinningStore(device, retired);
        Matrix4x4 world = Matrix4x4.CreateTranslation(7, 8, 9);
        RigidDualQuaternion[] words = Words();
        Matrix4x4[] matrices = Enumerable.Repeat(Matrix4x4.CreateScale(2), 128).ToArray();
        lbs.EnsureCapacity(2);
        dq.EnsureCapacity(2);
        outlines.EnsurePaletteCapacity(2);
        outlines.EnsurePaletteCapacity(2, SkinningMethod.DualQuaternionRigid);
        for (uint slot = 0; slot < 2; slot++)
        {
            lbs.Pack(slot, world, matrices);
            dq.Pack(slot, world, words);
            outlines.PackPalette((int)slot, matrices);
            outlines.PackPalette((int)slot, words);
        }
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        Assert.Equal(67584L, lbs.Upload(commands));
        Assert.Equal(34816L, dq.Upload(commands));
        outlines.UploadPalette(commands);
        Assert.Equal(32768L, outlines.UploadDqPalette(commands));
        Assert.Equal(4, commands.Uploads.Select(upload => upload.Buffer).Distinct().Count());
        Assert.All(commands.Uploads, upload => Assert.True(upload.IsWholeBuffer));
        Assert.Equal(8448u, SkinnedMotionPalette.OffsetFor(1));
        Assert.Equal(4352u, SkinnedMotionPalette.DqOffsetFor(1));
        Assert.Equal(4096u, TargetOutlineSkinningStore.DqPaletteSlotBytes);
        Assert.Equal(4096u, outlines.PaletteOffsetFor(1, SkinningMethod.DualQuaternionRigid));
        Assert.NotSame(outlines.PaletteLayout, outlines.PaletteLayoutFor(SkinningMethod.DualQuaternionRigid));
        AssertRange(lbs.Set, commands.Uploads[0].Buffer, 8448);
        AssertRange(dq.Set, commands.Uploads[1].Buffer, 4352);
        AssertRange(outlines.PaletteSet, commands.Uploads[2].Buffer, 8192);
        AssertRange(outlines.PaletteSetFor(SkinningMethod.DualQuaternionRigid), commands.Uploads[3].Buffer, 4096);
        for (int slot = 0; slot < 2; slot++)
        {
            Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(commands.Uploads[1].Data!.AsSpan(slot * 4352)));
            AssertWords(commands.Uploads[1].Data!, slot * 4352 + 64, words);
            AssertWords(commands.Uploads[3].Data!, slot * 4096, words);
            Assert.Equal(MemoryMarshal.AsBytes(matrices.AsSpan()).ToArray(),
                commands.Uploads[0].Data!.AsSpan(slot * 8448 + 64, 8192).ToArray());
            Assert.Equal(MemoryMarshal.AsBytes(matrices.AsSpan()).ToArray(),
                commands.Uploads[2].Data!.AsSpan(slot * 8192, 8192).ToArray());
        }
        Assert.Throws<InvalidOperationException>(() => lbs.Pack(0, world, words));
        Assert.Throws<InvalidOperationException>(() => dq.Pack(0, world, matrices));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capacity_failure_leaves_the_old_range_and_payload_usable(bool failSet)
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        using var palette = new DualQuaternionPaletteBuffer(device, retired);
        palette.EnsureCapacity(2);
        RigidDualQuaternion[] words = Words();
        palette.Pack(1, words);
        IGpuResourceSet set = palette.Set;
        if (failSet) factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 1;
        else factory.ThrowOnBufferCreate = factory.Buffers.Count + 1;
        Assert.Throws<InvalidOperationException>(() => palette.EnsureCapacity(9));
        Assert.Same(set, palette.Set);
        Assert.Equal(0, retired.PendingCount);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        Assert.Equal(32768L, palette.Upload(commands));
        AssertWords(Assert.Single(commands.Uploads).Data!, 4096, words);
        Assert.Single(factory.Buffers, buffer => !buffer.Disposed);
        Assert.Single(factory.ResourceSets, resource => !resource.Disposed);
    }

    [Fact]
    public void Invalid_ranges_and_disposed_stores_cannot_write()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        var palette = new DualQuaternionPaletteBuffer(device, retired);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        palette.EnsureCapacity(0);
        Assert.Equal(0L, palette.Upload(commands));
        Assert.Empty(commands.Uploads);
        palette.EnsureCapacity(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.Pack(8, Words()));
        Assert.Throws<ArgumentException>(() => palette.Pack(0, new RigidDualQuaternion[129]));
        Assert.Throws<OverflowException>(() => palette.OffsetFor(uint.MaxValue));
        Assert.Throws<InvalidOperationException>(() => palette.PackPrevious(0, Matrix4x4.Identity, Words()));
        palette.Dispose();
        Assert.Throws<ObjectDisposedException>(() => palette.EnsureCapacity(1));
        Assert.Throws<ObjectDisposedException>(() => palette.Pack(0, Words()));
        Assert.Throws<ObjectDisposedException>(() => palette.Upload(commands));
    }

    static SkinnedGltfMesh Mesh(SkinningProfile profile) => new(
        new[] { new SkinnedVertex { Position = Vector3.UnitX, Normal = Vector3.UnitY, BoneWeights = Vector4.UnitX } },
        new ushort[] { 0, 0, 0 }, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity }, null, profile);

    static RigidDualQuaternion[] Words(float bias = 0)
    {
        var words = new RigidDualQuaternion[128];
        for (int i = 0; i < words.Length; i++)
            words[i] = new RigidDualQuaternion(new Vector4(bias + i + 1, -0f, i + 3, i + 4),
                new Vector4(i + 5, i + 6, -i - 7, i + 8));
        return words;
    }

    static void AssertWords(byte[] image, int offset, RigidDualQuaternion[] words) =>
        Assert.Equal(MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), image.AsSpan(offset, words.Length * 32).ToArray());

    static void AssertRange(IGpuResourceSet set, IGpuBuffer buffer, uint bytes)
    {
        var range = Assert.IsType<GpuBufferRange>(Assert.IsType<FakeResourceSet>(set).Resources.Last());
        Assert.Same(buffer, range.Buffer);
        Assert.Equal(0u, range.Offset);
        Assert.Equal(bytes, range.Size);
    }
}
