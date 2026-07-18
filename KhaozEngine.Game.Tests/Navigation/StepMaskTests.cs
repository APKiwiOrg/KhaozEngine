using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class StepMaskTests
{
    static bool[] AllStandable(int count)
    {
        var standable = new bool[count];
        for (int i = 0; i < count; i++) standable[i] = true;
        return standable;
    }

    static float[] UniformHeadroom(int count, float value = float.PositiveInfinity)
    {
        var headroom = new float[count];
        for (int i = 0; i < count; i++) headroom[i] = value;
        return headroom;
    }

    [Fact]
    public void LowStep_WithinBudget_NoErosion()
    {
        var standable = AllStandable(3);
        var height = new[] { 0f, 0.4f, 0f };
        var headroom = UniformHeadroom(3);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 3, 1, stepHeight: 0.5f, agentHeight: 0f);

        Assert.All(blocked, b => Assert.False(b));
    }

    [Fact]
    public void TallStep_HigherSideEroded()
    {
        var standable = AllStandable(3);
        var height = new[] { 0f, 10f, 0f };
        var headroom = UniformHeadroom(3);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 3, 1, stepHeight: 0.5f, agentHeight: 0f);

        Assert.False(blocked[0]);
        Assert.True(blocked[1]);
        Assert.False(blocked[2]);
    }

    [Fact]
    public void TallStep_BlockConsistency_5x1()
    {
        var standable = AllStandable(5);
        var height = new[] { 0f, 0f, 5f, 0f, 0f };
        var headroom = UniformHeadroom(5);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 5, 1, stepHeight: 0.5f, agentHeight: 0f);

        Assert.False(blocked[0]);
        Assert.False(blocked[1]);
        Assert.True(blocked[2]);
        Assert.False(blocked[3]);
        Assert.False(blocked[4]);
    }

    [Fact]
    public void Headroom_BelowAgentHeight_Blocks()
    {
        var standable = new[] { true };
        var height = new[] { 0f };

        bool[] belowBoundary = StepMask.Compute(standable, height, new[] { 1.5f }, 1, 1, stepHeight: 0.5f, agentHeight: 2.0f);
        Assert.True(belowBoundary[0]);

        bool[] atBoundary = StepMask.Compute(standable, height, new[] { 2.0f }, 1, 1, stepHeight: 0.5f, agentHeight: 2.0f);
        Assert.False(atBoundary[0]);
    }

    [Fact]
    public void NonStandableNeighbor_DoesNotErode()
    {
        var standable = new[] { true, false, true };
        var height = new[] { 0f, 0f, 0f };
        var headroom = UniformHeadroom(3);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 3, 1, stepHeight: 0.5f, agentHeight: 0f);

        Assert.False(blocked[0]);
        Assert.True(blocked[1]);
        Assert.False(blocked[2]);
    }

    [Fact]
    public void DiagonalDrop_ErodesHigherCorner()
    {
        // 2x2: (0,0)=0 (1,0)=0
        //      (0,1)=0 (1,1)=10  <- high corner, index 3
        var standable = AllStandable(4);
        var height = new[] { 0f, 0f, 0f, 10f };
        var headroom = UniformHeadroom(4);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 2, 2, stepHeight: 0.5f, agentHeight: 0f);

        Assert.False(blocked[0]);
        Assert.False(blocked[1]);
        Assert.False(blocked[2]);
        Assert.True(blocked[3]);
    }

    [Fact]
    public void Compute_BadDimensions_Throws()
    {
        var standable = AllStandable(3);
        var height = new float[3];
        var headroom = UniformHeadroom(3);

        Assert.Throws<ArgumentException>(() => StepMask.Compute(standable, height, headroom, 2, 1, 0.5f, 0f));
        Assert.Throws<ArgumentException>(() => StepMask.Compute(standable, new float[2], headroom, 3, 1, 0.5f, 0f));
        Assert.Throws<ArgumentException>(() => StepMask.Compute(standable, height, new float[2], 3, 1, 0.5f, 0f));
        Assert.Throws<ArgumentException>(() => StepMask.Compute(standable, height, headroom, 0, 1, 0.5f, 0f));
        Assert.Throws<ArgumentException>(() => StepMask.Compute(standable, height, headroom, 3, 0, 0.5f, 0f));
    }

    [Fact]
    public void Compute_Deterministic()
    {
        var standable = AllStandable(9);
        var height = new[] { 0f, 0.2f, 5f, 0.1f, 0f, 3f, 0f, 0.6f, 0f };
        var headroom = UniformHeadroom(9);

        bool[] a = StepMask.Compute(standable, height, headroom, 3, 3, stepHeight: 0.5f, agentHeight: 0f);
        bool[] b = StepMask.Compute(standable, height, headroom, 3, 3, stepHeight: 0.5f, agentHeight: 0f);

        Assert.Equal(a, b);
    }
}
