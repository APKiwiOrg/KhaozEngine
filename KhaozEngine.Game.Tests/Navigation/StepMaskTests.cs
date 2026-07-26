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

    // A 5x5 terrace: the center cell at 2.0, the ring around it at 1.0, the outer ring at 0.0. Every ring
    // cell erodes against the outer ring, which leaves the center a lone standable top with a fully
    // blocked 8-neighborhood, the shape a hop link exists to cross.
    static float[] TerracedIsland()
    {
        var height = new float[25];
        for (int cz = 0; cz < 5; cz++)
            for (int cx = 0; cx < 5; cx++)
            {
                int ring = Math.Max(Math.Abs(cx - 2), Math.Abs(cz - 2));
                height[cz * 5 + cx] = ring switch { 0 => 2f, 1 => 1f, _ => 0f };
            }

        return height;
    }

    [Fact]
    public void IsolatedTop_WithFullyBlockedRing_SurvivesInsteadOfEroding()
    {
        bool[] blocked = StepMask.Compute(
            AllStandable(25), TerracedIsland(), UniformHeadroom(25), 5, 5, stepHeight: 0.4f, agentHeight: 0f);

        // The lone top survives, so NavHopLinks can still see it and link it. Erasing it here is what made
        // a one-cell island unlinkable by any hop.
        Assert.False(blocked[2 * 5 + 2]);

        // Its whole ring still erodes (each ring cell has same-height ring neighbors within the step, so
        // it is the edge of a connected field, not a lone top), and the outer ring stays open ground.
        for (int cz = 1; cz <= 3; cz++)
            for (int cx = 1; cx <= 3; cx++)
                if (cx != 2 || cz != 2)
                    Assert.True(blocked[cz * 5 + cx], $"ring cell ({cx}, {cz}) should still erode");

        Assert.False(blocked[0]);
        Assert.False(blocked[2]);
        Assert.False(blocked[24]);
    }

    [Fact]
    public void RaisedCell_StillTouchingWalkableGround_StillErodes()
    {
        // The guard on the carve-out above. This raised cell is a lone top too, but its neighbors are open
        // walkable ground, so keeping it would hand the grid planner a walk edge straight up a 2 m drop,
        // which is the exact traversal the erosion rule exists to block. A hop cannot reach it either:
        // NavHopLinks only crosses a blocked rim, never open ground.
        var height = new float[9];
        height[4] = 2f;

        bool[] blocked = StepMask.Compute(
            AllStandable(9), height, UniformHeadroom(9), 3, 3, stepHeight: 0.4f, agentHeight: 0f);

        Assert.True(blocked[4]);
        for (int i = 0; i < 9; i++)
            if (i != 4)
                Assert.False(blocked[i]);
    }

    [Fact]
    public void CliffEdge_WithOneNeighborWithinStepAndOneFarBelow_StillErodes()
    {
        // The plateau edge: cell 1 has a same-height neighbor (within the step) and a far-below neighbor,
        // so it is a connected field's edge rather than an island and keeps eroding exactly as before.
        var standable = AllStandable(3);
        var height = new[] { 1f, 1f, 0f };
        var headroom = UniformHeadroom(3);

        bool[] blocked = StepMask.Compute(standable, height, headroom, 3, 1, stepHeight: 0.4f, agentHeight: 0f);

        Assert.False(blocked[0]);
        Assert.True(blocked[1]);
        Assert.False(blocked[2]);
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
