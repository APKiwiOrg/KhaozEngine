using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]   // one case is a zero-allocation reading (#264)
public sealed class RigidMotionSlotsTests
{
    static readonly MotionKey Mover = MotionKey.From(11), Newcomer = MotionKey.From(12);
    static readonly Vector3 Origin = new(128f, 0f, 0f);

    static MotionHistory HistoryWith(MotionKey key, Matrix4x4 world)
    {
        var history = new MotionHistory();
        history.BeginFrame();
        history.RecordRigid(key, world);
        history.BeginFrame();   // that record is now the previous generation
        return history;
    }

    [Fact]
    public void AKeyWithALastFrameGetsASlotAndEverythingElseReadsItsOwnTransform()
    {
        MotionHistory history = HistoryWith(Mover, Matrix4x4.CreateTranslation(130f, 2f, -4f));
        var slots = new float[3];
        var previous = new List<Matrix4x4>();

        int count = RigidMotionSlots.Build(new[] { MotionKey.None, Mover, Newcomer }, history, Origin, slots, previous);

        Assert.Equal(1, count);
        Assert.Equal(new[] { -1f, 0f, -1f }, slots);
        Assert.Equal(Matrix4x4.CreateTranslation(2f, 2f, -4f), previous[0]);   // reduced by this frame's origin
    }

    [Fact]
    public void EachDuplicateOfAKeyGetsItsOwnSlotHoldingTheKeysOnePreviousTransform()
    {
        // Two submissions sharing a key this frame collide (the last recorded one is what the history keeps), and each
        // still reads that one previous transform rather than sharing a slot.
        MotionHistory history = HistoryWith(Mover, Matrix4x4.CreateTranslation(130f, 0f, 0f));
        var slots = new float[2];
        var previous = new List<Matrix4x4>();

        Assert.Equal(2, RigidMotionSlots.Build(new[] { Mover, Mover }, history, Origin, slots, previous));
        Assert.Equal(new[] { 0f, 1f }, slots);
        Assert.Equal(new[] { Matrix4x4.CreateTranslation(2f, 0f, 0f), Matrix4x4.CreateTranslation(2f, 0f, 0f) }, previous);
    }

    [Fact]
    public void NoHistoryMeansNoSlotAtAll()
    {
        var slots = new float[2];
        var previous = new List<Matrix4x4> { Matrix4x4.Identity };

        Assert.Equal(0, RigidMotionSlots.Build(new[] { Mover, Newcomer }, null, Origin, slots, previous));
        Assert.Equal(new[] { -1f, -1f }, slots);
        Assert.Empty(previous);
    }

    [Fact]
    public void ASteadyBuildAllocatesNothing()
    {
        MotionHistory history = HistoryWith(Mover, Matrix4x4.Identity);
        var keys = new MotionKey[512];
        for (int i = 0; i < keys.Length; i++) keys[i] = i % 2 == 0 ? Mover : MotionKey.None;
        var slots = new float[keys.Length];
        var previous = new List<Matrix4x4>();
        for (int i = 0; i < 4; i++) RigidMotionSlots.Build(keys, history, Origin, slots, previous);

        AllocAssert.NoPerCallAllocation("20 rigid motion slot builds over 512 instances", () =>
        {
            for (int i = 0; i < 20; i++) RigidMotionSlots.Build(keys, history, Origin, slots, previous);
        });
    }
}
