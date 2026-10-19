using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("CoverageCapabilities")]
public sealed class CoverageVerificationTests
{
    [Fact]
    public void Verification_is_off_by_default_and_configuration_requires_an_explicit_actor()
    {
        using var h = new CoverageSceneFixture();
        Draw(h);
        Assert.Null(h.Scene.CoverageVerificationForTests);
        h.Scene.ConfigureCoverageVerification(MotionKey.From(10), 3);
        Draw(h);
        Assert.Null(h.Scene.CoverageVerificationForTests); // Recording fixture has no completion fences.
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Scene.ConfigureCoverageVerification(MotionKey.None, -1));
        h.Scene.ConfigureCoverageVerification(MotionKey.None, 3);
        Draw(h);
        Assert.Null(h.Scene.CoverageVerificationForTests);
        h.Scene.ConfigureCoverageVerification(MotionKey.From(10), 0);
        Draw(h);
        Assert.Null(h.Scene.CoverageVerificationForTests);
    }

    [Fact]
    public void Held_completion_and_full_ring_preserve_pose_and_log_only_first_differing_vertex()
    {
        var sink = new InMemorySink();
        using var manager = new LogManager(new LoggerOptions { Synchronous = true, Sinks = { sink } });
        using var logging = Log.Use(manager);
        using var h = new CoverageSceneFixture();
        var p = Draw(h).Preparation!;
        var gpu = new SubmissionTestDevice(3);
        using var verification = new CoverageVerificationReadbacks(gpu, p.Owner.Topology, p.Palette.Length);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = Surface(p, out var expected);
        uint[] upload = (uint[])p.Owner.Gpu.Frame.Clone();
        uint[]? retainedPose = null;
        for (int i = 0; i < 4; i++)
        {
            p.Generation = (ulong)(i + 1) * 3;
            SetHeader(source, p);
            submission.Begin();
            Assert.True(submission.TryRegister(verification));
            bool staged = verification.Stage(p, 91, upload, source);
            Assert.Equal(i < 3, staged);
            if (staged)
            {
                Assert.True(verification.Record(gpu, submission));
                retainedPose ??= verification.Snapshots[0].PoseWords.ToArray();
            }
            submission.Submit(gpu);
        }
        Assert.Equal(3, verification.PendingCount);
        Assert.False(verification.TryRead(out _));
        Assert.Equal(0, verification.ReferenceCalls);
        p.Palette[0] = Matrix4x4.CreateScale(99);
        p.Model = Matrix4x4.CreateTranslation(99, 99, 99);
        p.Commands[0] = default;
        Array.Fill(upload, 0xdeadbeefu);
        foreach (var fence in gpu.Fences) fence.Signaled = true;
        Assert.True(verification.TryRead(out var equal));
        Assert.Equal(-1, equal.Vertex);
        Assert.Empty(sink.Entries);
        Assert.Equal(new ulong[] { 3, 6, 9 }, verification.Snapshots.Select(s => s.FrameId));
        Assert.Equal(retainedPose, equal.Pose.PoseWords);
        Assert.NotEqual(upload, equal.Pose.UploadWords);
        Assert.True(verification.TryRead(out var second));
        Assert.Equal(-1, second.Vertex);
        // Injected comparison words are a control, separate from completed device copies.
        expected[7 * 16] = 0x3f800000u;
        var different = (uint[])expected.Clone();
        different[7 * 16] = 0x3f800001u;
        different[8 * 16 + 3] ^= 1;
        var mismatch = CoverageVerificationComparison.Compare(second.Pose, false, true, default, expected, different);
        Assert.Equal(7, mismatch.Vertex);
        Assert.Equal("Position.X", mismatch.Field);
        Assert.Equal(0x3f800000u, mismatch.Expected);
        Assert.Equal(0x3f800001u, mismatch.Actual);
        Assert.Equal(retainedPose, mismatch.Pose.PoseWords);
        var entry = Assert.Single(sink.Entries);
        Assert.Contains("vertex=7 field=Position.X expected=0x3f800000 actual=0x3f800001", entry.Message);
        Assert.Contains(string.Join(",", retainedPose!.Select(w => $"{w:x8}")), entry.Message);
        Assert.True(verification.TryRead(out var last));
        Assert.Equal(-1, last.Vertex);
        Assert.Equal(4, gpu.SubmitCalls);
        Assert.Equal(0, gpu.WaitCalls);
        Assert.Equal(0, gpu.FlushCalls);
    }

    [Theory]
    [InlineData("abandon")]
    [InlineData("disable")]
    [InlineData("submit-failure")]
    [InlineData("callback-failure")]
    public void Cancellation_and_failures_keep_exact_fence_ownership_without_reference_work(string operation)
    {
        using var h = new CoverageSceneFixture();
        var p = Draw(h).Preparation!;
        var gpu = new SubmissionTestDevice(3);
        using var verification = new CoverageVerificationReadbacks(gpu, p.Owner.Topology, p.Palette.Length);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = Surface(p, out _);
        submission.Begin();
        var throwing = new GpuFrameSubmissionTests.Participant { ThrowSubmitted = operation == "callback-failure" };
        Assert.True(submission.TryRegister(throwing));
        Assert.True(submission.TryRegister(verification));
        Assert.True(verification.Stage(p, 1, p.Owner.Gpu.Frame, source));
        Assert.True(verification.Record(gpu, submission));
        var fence = gpu.Fences[0];
        if (operation == "abandon")
        {
            submission.Abandon();
            Assert.Equal(0, verification.PendingCount);
        }
        else
        {
            gpu.FailSubmit = operation == "submit-failure";
            if (operation.EndsWith("failure", StringComparison.Ordinal))
                Assert.Throws<InvalidOperationException>(() => submission.Submit(gpu));
            else submission.Submit(gpu);
            verification.Dispose();
            Assert.True(verification.HasPending(fence));
            Assert.All(gpu.Buffers, b => Assert.Equal(0, b.DisposeCalls));
            Assert.False(verification.TryRead(out _));
            fence.Signaled = true;
            Assert.False(verification.HasPending(fence));
            Assert.All(gpu.Buffers, b => Assert.Equal(1, b.DisposeCalls));
        }
        Assert.Equal(0, verification.ReferenceCalls);
        Assert.Equal(0, gpu.WaitCalls);
        Assert.Equal(0, gpu.FlushCalls);
        Assert.Equal(0, fence.ResetCalls);
        Assert.Equal(0, fence.DisposeCalls);
    }

    [Fact]
    public void Unrecorded_sample_is_discarded_before_the_next_frame_and_signed_zero_is_exact()
    {
        using var h = new CoverageSceneFixture();
        var p = Draw(h).Preparation!;
        var gpu = new SubmissionTestDevice(3);
        using var verification = new CoverageVerificationReadbacks(gpu, p.Owner.Topology, p.Palette.Length);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = Surface(p, out _);
        Assert.True(verification.Stage(p, 1, p.Owner.Gpu.Frame, source));
        verification.BeginFrame();
        submission.Begin();
        Assert.False(verification.Record(gpu, submission));
        Assert.Equal(0, gpu.CopyCalls);
        Assert.True(verification.Stage(p, 2, p.Owner.Gpu.Frame, source));
        Assert.True(verification.Record(gpu, submission));
        submission.Abandon();
        Assert.Equal(0, verification.PendingCount);
        var result = CoverageVerificationComparison.Compare(verification.Snapshots[0], false, true, default,
            new uint[] { 0 }, new uint[] { 0x80000000 });
        Assert.Equal(0, result.Vertex);
        Assert.Equal("Position.X", result.Field);
        Assert.Equal(0x80000000u, result.Actual);
    }

    internal static SkinnedCoverageFrame Draw(CoverageSceneFixture h)
    {
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        return frame;
    }

    internal static SubmissionTestBuffer Surface(CoveragePreparation p, out uint[] expected)
    {
        var topology = p.Owner.Topology;
        var composed = new Matrix4x4[p.Palette.Length];
        CoverageReferenceSkinning.Compose(p.Palette, p.Owner.InverseBind, composed);
        var posed = new ModelVertex[topology.SourceVertexCount];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(topology.Source, composed.Length), topology.Source, composed, posed, out _));
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(topology, posed, p.Model, p.Commands.AsSpan(0, p.CommandCount), new(topology), final, out _));
        expected = new uint[final.Length * 16];
        for (int i = 0; i < final.Length; i++) CoverageGpuUpload.WriteModel(final[i], expected.AsSpan(i * 16));
        var source = new SubmissionTestBuffer((uint)(16 + posed.Length * 16 + expected.Length) * 4);
        var words = MemoryMarshal.Cast<byte, uint>(source.Bytes.AsSpan());
        SetHeader(source, p);
        expected.AsSpan().CopyTo(words[(16 + posed.Length * 16)..]);
        p.GpuReservation = p.GpuReservation with { CurrentBase = 0 };
        return source;
    }

    internal static void SetHeader(SubmissionTestBuffer source, CoveragePreparation p)
    {
        var words = MemoryMarshal.Cast<byte, uint>(source.Bytes.AsSpan());
        words[0] = (uint)p.Generation; words[1] = (uint)(p.Generation >> 32);
        words[2] = (uint)p.View; words[3] = (uint)(p.View >> 32);
        words[4] = (uint)p.Actor.Value; words[5] = (uint)(p.Actor.Value >> 32);
        words[6] = (uint)p.Id; words[7] = (uint)(p.Id >> 32);
    }
}
