using System;
using System.Numerics;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuTree = BepuPhysics.Trees.Tree;

namespace KhaozEngine.Tests.Physics;

// Owned finite trees, not native geometry or a selected-world coverage certificate.
public unsafe class CapsuleTreePreflightTests
{

    [Fact]
    public void EmptyTreeNeedsNoNodeOrLeafVisit()
    {
        using var fixture = new Fixture(empty: true);
        Assert.Equal((true, 0, 0, 0), Run(fixture));
    }

    [Fact]
    public void ClosedSingleLeafMatchesThePinnedEnumerator()
    {
        using var fixture = new Fixture(single: true);
        Assert.Equal(1, fixture.CountActual(Vector3.One, Vector3.One));
        Assert.Equal((true, 1, 1, 0), Run(fixture, min: Vector3.One, max: Vector3.One));
        Vector3 outside = new(MathF.BitIncrement(1f));
        Assert.Equal(0, fixture.CountActual(outside, outside));
        Assert.Equal((true, 1, 0, 0), Run(fixture, min: outside, max: outside));
    }

    [Fact]
    public void PinnedAFirstOrderHasADifferentPeakThanTheReverseTree()
    {
        using var a = new Fixture();
        using var b = new Fixture(reverse: true);
        Assert.Equal(3, a.CountActual(new(-1), new(1)));
        Assert.Equal(3, b.CountActual(new(-1), new(1)));
        Assert.Equal((true, 2, 3, 2), Run(a));
        Assert.Equal((true, 2, 3, 1), Run(b));
    }

    [Fact]
    public void PendingCapacityUsesTheActualPinnedOrder()
    {
        using var a = new Fixture();
        using var b = new Fixture(reverse: true);
        Refused(Run(a, pending: 1));
        Assert.Equal((true, 2, 3, 1), Run(b, pending: 1));
    }

    [Fact]
    public void NodeBudgetExhaustionPublishesNoPartialCounts()
    {
        using var fixture = new Fixture();
        Refused(Run(fixture, nodes: 1));
    }

    [Fact]
    public void LeafBudgetExhaustionPublishesNoPartialCounts()
    {
        using var fixture = new Fixture();
        Refused(Run(fixture, leaves: 2));
    }

    [Fact]
    public void NodeIndexInsideCapacityButOutsideLiveCountRefuses()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Tree.NodeCount < fixture.Tree.Nodes.Length);
        fixture.Tree.Nodes[0].A.Index = fixture.Tree.NodeCount;
        Refused(Run(fixture));
    }

    [Fact]
    public void LeafIndexInsideCapacityButOutsideLiveCountRefuses()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Tree.LeafCount < fixture.Tree.Leaves.Length);
        fixture.Tree.Nodes[0].B.Index = BepuTree.Encode(fixture.Tree.LeafCount);
        Refused(Run(fixture));
    }

    [Fact]
    public void NonfiniteOrInvertedVisitedBoundsCannotMeanOutside()
    {
        using var fixture = new Fixture();
        fixture.Tree.Nodes[0].A.Min.X = float.NaN;
        Refused(Run(fixture));
        fixture.Tree.Nodes[0].A.Min = new(2);
        Refused(Run(fixture));
    }

    [Fact]
    public void ANodeCycleRefusesWithinTheFixedBudgets()
    {
        using var fixture = new Fixture();
        fixture.Tree.Nodes[0].A.Index = 0;
        Refused(Run(fixture));
    }

    [Fact]
    public void InvalidAperturesAndPolicyLimitsRefuse()
    {
        using var fixture = new Fixture();
        Refused(Run(fixture, min: new(float.NaN)));
        Refused(Run(fixture, min: new(2), max: new(1)));
        Refused(Run(fixture, nodes: 0));
        Refused(Run(fixture, nodes: 8193));
        Refused(Run(fixture, leaves: 4097));
        Refused(Run(fixture, pending: 129));
    }

    [Fact]
    public void APrunedSubtreeDoesNotConsumeItsNodesOrLeaves()
    {
        using var fixture = new Fixture();
        fixture.Tree.Nodes[0].A.Min = fixture.Tree.Nodes[1].A.Min = fixture.Tree.Nodes[1].B.Min = new(10);
        fixture.Tree.Nodes[0].A.Max = fixture.Tree.Nodes[1].A.Max = fixture.Tree.Nodes[1].B.Max = new(11);
        Assert.Equal(1, fixture.CountActual(new(-1), new(1)));
        Assert.Equal((true, 1, 1, 0), Run(fixture, nodes: 1, leaves: 1));
    }

    static void Refused((bool Complete, int Nodes, int Leaves, int Pending) result) =>
        Assert.Equal((false, 0, 0, 0), result);

    static (bool Complete, int Nodes, int Leaves, int Pending) Run(Fixture fixture,
        Vector3? min = null, Vector3? max = null, int nodes = 8192, int leaves = 4096, int pending = 128)
    {
        bool complete = CapsuleTreePreflight.TryValidate(in fixture.Tree, min ?? new(-1), max ?? new(1),
            nodes, leaves, pending, out int visitedNodes, out int visitedLeaves, out int peak);
        return (complete, visitedNodes, visitedLeaves, peak);
    }

    sealed class Fixture : IDisposable
    {
        readonly BufferPool pool = new();
        internal BepuTree Tree;

        internal Fixture(bool empty = false, bool single = false, bool reverse = false)
        {
            Tree = new BepuTree(pool, 16);
            if (empty) return;
            Tree.NodeCount = single ? 1 : 2;
            Tree.LeafCount = single ? 1 : 3;
            Tree.Nodes[0] = new Node { A = Child(BepuTree.Encode(0), 1) };
            Tree.Leaves[0] = new Leaf(0, 0);
            if (single) return;
            Tree.Nodes[0] = reverse
                ? new Node { A = Child(BepuTree.Encode(2), 1), B = Child(1, 2) }
                : new Node { A = Child(1, 2), B = Child(BepuTree.Encode(2), 1) };
            Tree.Nodes[1] = new Node { A = Child(BepuTree.Encode(0), 1), B = Child(BepuTree.Encode(1), 1) };
            Tree.Leaves[0] = new Leaf(1, 0);
            Tree.Leaves[1] = new Leaf(1, 1);
            Tree.Leaves[2] = new Leaf(0, reverse ? 0 : 1);
            Tree.Metanodes[0] = new Metanode { Parent = -1, IndexInParent = -1 };
            Tree.Metanodes[1] = new Metanode { Parent = 0, IndexInParent = reverse ? 1 : 0 };
        }

        static NodeChild Child(int index, int leaves) => new()
        {
            Min = new(-1), Max = new(1), Index = index, LeafCount = leaves
        };

        internal int CountActual(Vector3 min, Vector3 max)
        {
            var counter = new Counter();
            Tree.GetOverlaps(min, max, ref counter);
            return counter.Count;
        }

        public void Dispose()
        {
            Tree.Dispose(pool);
            pool.Clear();
        }
    }

    struct Counter : IBreakableForEach<int>
    {
        public int Count;
        public bool LoopBody(int index) { Count++; return true; }
    }
}
