using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Simulation;
using Xunit;
using AllocAssert = KhaozEngine.Tests.AllocationMeasurement;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WornMotionBatchTests
{
    const float FrameTime = 1f / 144;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layered_batch_matches_every_sequential_draw_bit_for_bit(bool parallel)
    {
        const int wearers = 4, frames = 288;
        using var workers = parallel ? new WorkerJobScheduler(3) : null;
        var batch = new WornMotionBatch((IJobScheduler?)workers ?? new SingleThreadedJobScheduler());
        using var rig = new OutfitRig();
        using WearerPair first = new(rig, 0, wearers, useSheetFixture: true);
        using WearerPair second = new(rig, 1, wearers, useSheetFixture: true);
        using WearerPair third = new(rig, 2, wearers, useSheetFixture: true);
        using WearerPair fourth = new(rig, 3, wearers, useSheetFixture: true);
        WearerPair[] pairs = [first, second, third, fourth];

        for (int frame = 0; frame < frames; frame++)
        {
            float seconds = frame * FrameTime;
            foreach (WearerPair pair in pairs)
            {
                pair.Inline.Pose(seconds);
                pair.Batched.Pose(seconds);
                batch.Add(pair.Batched.Motion, rig.Skeleton, pair.Batched.Palette, pair.Batched.Model, FrameTime);
            }
            foreach (WearerPair pair in pairs) pair.Inline.Step(FrameTime);
            batch.Step();
            batch.Clear();
            foreach (WearerPair pair in pairs) pair.AssertSameDraws();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Invalid_entry_refuses_the_whole_batch_before_advancing_motion(int invalid)
    {
        using var rig = new OutfitRig();
        using WearerPair first = new(rig, 0, 2);
        using WearerPair second = new(rig, 1, 2);
        WearerPair[] pairs = [first, second];
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        foreach (WearerPair pair in pairs) pair.Prime();

        for (int i = 0; i < pairs.Length; i++)
        {
            SwingCostWearer wearer = pairs[i].Batched;
            batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, i == invalid ? -FrameTime : FrameTime);
        }
        var error = Assert.Throws<ArgumentOutOfRangeException>(batch.Step);
        Assert.Equal("deltaSeconds", error.ParamName);
        foreach (WearerPair pair in pairs) pair.AssertSameDraws();

        batch.Clear();
        foreach (WearerPair pair in pairs)
        {
            SwingCostWearer wearer = pair.Batched;
            batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
            pair.Inline.Step(FrameTime);
        }
        batch.Step();
        foreach (WearerPair pair in pairs) pair.AssertSameDraws();
    }

    [Fact]
    public void Repeated_motion_names_the_duplicate_entry_and_advances_nothing()
    {
        using var rig = new OutfitRig();
        using WearerPair pair = new(rig, 0, 1);
        pair.Prime();
        var batch = new WornMotionBatch(new SingleThreadedJobScheduler());
        SwingCostWearer wearer = pair.Batched;
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);

        var error = Assert.Throws<AttachmentSubmissionArgumentException>(batch.Step);
        Assert.Equal(AttachmentSubmissionRefusal.DuplicateMotion, error.Reason);
        Assert.Equal(1, error.EntryIndex);
        pair.AssertSameDraws();

        Matrix4x4[] beforeStep = wearer.Motion.JointsFromAnchor.ToArray();
        batch.Clear();
        batch.Add(wearer.Motion, rig.Skeleton, wearer.Palette, wearer.Model, FrameTime);
        pair.Inline.Step(FrameTime);
        batch.Step();
        pair.AssertSameDraws();
        Assert.False(beforeStep.AsSpan().SequenceEqual(wearer.Motion.JointsFromAnchor));
    }

    [Fact]
    public void Warm_batch_fill_and_step_allocate_nothing_on_the_caller()
    {
        using var rig = new OutfitRig();
        using WearerPair pair = new(rig, 0, 2);
        pair.Prime();
        using var scheduler = new WorkerJobScheduler(1);
        var batch = new WornMotionBatch(scheduler);

        void FillAndStep()
        {
            batch.Clear();
            batch.Add(pair.Batched.Motion, rig.Skeleton, pair.Batched.Palette, pair.Batched.Model, FrameTime);
            batch.Add(pair.Inline.Motion, rig.Skeleton, pair.Inline.Palette, pair.Inline.Model, FrameTime);
            batch.Step();
        }

        for (int frame = 0; frame < 16; frame++) FillAndStep();
        AllocAssert.NoPerCallAllocation("worn motion batch fill and step", () =>
        {
            for (int frame = 0; frame < 16; frame++) FillAndStep();
        });
    }

    sealed class WearerPair : IDisposable
    {
        internal SwingCostWearer Inline { get; }
        internal SwingCostWearer Batched { get; }

        internal WearerPair(OutfitRig rig, int index, int wearers, bool useSheetFixture = false)
        {
            var body = rig.BodyRegions(new RegionRun(0, 4));
            var colliders = SwingCostFixture.LoadColliders(rig);
            var pieces = useSheetFixture
                ? LayeredSwingCostOutfit.LoadSheetCase(rig, sevenGuides: false)
                : LayeredSwingCostOutfit.Load(rig);
            var options = new ChainSimulationOptions();
            Inline = new SwingCostWearer(rig, body, pieces, colliders, options, index, wearers);
            try
            {
                Batched = new SwingCostWearer(rig, body, pieces, colliders, options, index, wearers);
            }
            catch
            {
                Inline.Dispose();
                throw;
            }
            Inline.Reset(MotionLevel.Simulated);
            Batched.Reset(MotionLevel.Simulated);
        }

        internal void Prime()
        {
            for (int frame = 0; frame < 8; frame++)
            {
                Inline.Pose(frame * FrameTime);
                Batched.Pose(frame * FrameTime);
                Inline.Step(FrameTime);
                Batched.Step(FrameTime);
            }
            Inline.Pose(8 * FrameTime);
            Batched.Pose(8 * FrameTime);
        }

        internal void AssertSameDraws()
        {
            ReadOnlySpan<Matrix4x4> expectedFrames = Inline.Motion.JointsFromAnchor;
            ReadOnlySpan<Matrix4x4> actualFrames = Batched.Motion.JointsFromAnchor;
            Assert.True(expectedFrames.Length > 0);
            Assert.Equal(expectedFrames.Length, actualFrames.Length);
            for (int joint = 0; joint < expectedFrames.Length; joint++)
                AssertBits(expectedFrames[joint], actualFrames[joint]);

            Inline.WriteDraws();
            Batched.WriteDraws();
            Assert.Equal(3, Inline.Draws.Length);
            Assert.Equal(Inline.Draws.Length, Batched.Draws.Length);
            for (int worn = 0; worn < Inline.Draws.Length; worn++)
            {
                AttachmentDraw expected = Assert.Single(Inline.Draws[worn]);
                AttachmentDraw actual = Assert.Single(Batched.Draws[worn]);
                Assert.Equal(expected.IsSkinned, actual.IsSkinned);
                if (expected.IsSkinned)
                {
                    AssertBits(expected.Skinned.Model, actual.Skinned.Model);
                    ReadOnlySpan<Matrix4x4> expectedBones = expected.BoneMatrices.Span;
                    ReadOnlySpan<Matrix4x4> actualBones = actual.BoneMatrices.Span;
                    Assert.True(expectedBones.Length > 2);
                    Assert.Equal(expectedBones.Length, actualBones.Length);
                    for (int joint = 0; joint < expectedBones.Length; joint++)
                        AssertBits(expectedBones[joint], actualBones[joint]);
                }
                else
                {
                    AssertBits(expected.Rigid.World, actual.Rigid.World);
                }
            }
        }

        static void AssertBits(in Matrix4x4 expected, in Matrix4x4 actual)
        {
            if (MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(in expected, 1)).SequenceEqual(
                MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(in actual, 1)))) return;

            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Assert.Equal(BitConverter.SingleToInt32Bits(expected[row, column]),
                        BitConverter.SingleToInt32Bits(actual[row, column]));
        }

        public void Dispose()
        {
            Batched.Dispose();
            Inline.Dispose();
        }
    }
}
