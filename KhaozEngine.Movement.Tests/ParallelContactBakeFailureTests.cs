using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

// Every fault here is a pure function of the query it sees, so the set of failing work items is fixed and the
// serial build's first failure is the expected exception on every interleaving. Coordination only reorders wall-clock
// time: the earlier fault waits, bounded, until the later one has thrown.
public class ParallelContactBakeFailureTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    static readonly int[] Degrees = [2, 3, 8];

    [Fact]
    public void TwoAdmissionFailuresOnDifferentRawSurfacesThrowTheSerialFailure()
    {
        var faults = new Faults
        {
            // The right column's slab top in the first row, after its floor surface admits.
            Earlier = Near(Centre(1, 0), 0.45f, 0.8f),
            // The left deck in the last row.
            Later = Near(Centre(0, 5), 0.2f, 0.6f),
        };
        AssertSerialFailureWins(faults);
    }

    [Fact]
    public void TwoEdgeFailuresInDifferentLayersThrowTheSerialFailure()
    {
        var faults = new Faults();
        using (var reference = new SmallStairScene())
        {
            GroundNavigation nav = reference.Build(null);
            Assert.True(nav.Space.Layers.Count >= 2);
            (int x, int z) first = ForwardEdges(nav, 0).First();
            (int x, int z) last = ForwardEdges(nav, nav.Space.Layers.Count - 1).Last();
            faults.Earlier = MovingFrom(nav.Space.Layers[0], first.x, first.z, Vector2.UnitY);
            faults.Later = MovingFrom(nav.Space.Layers[^1], last.x, last.z, Vector2.UnitY);
        }
        AssertSerialFailureWins(faults);
    }

    [Fact]
    public void TwoStairLinkFailuresInCandidateOrderThrowTheSerialFailure()
    {
        var faults = new Faults();
        using (var reference = new SmallStairScene())
        {
            GroundNavigation nav = reference.Build(null);
            // Links whose start and heading no same-layer edge shares, so only the link proof reaches the fault.
            List<int> unique = [.. Enumerable.Range(0, nav.Space.Links.Count).Where(index =>
            {
                NavLink link = nav.Space.Links[index];
                return nav.Graph.Links.Contains(link) &&
                    !nav.Space.Layers[link.FromLayer].IsPassable(link.ToX, link.ToZ, 0f);
            })];
            Assert.True(unique.Count >= 2);
            Assert.True(unique[0] < nav.Space.Links.Count / 2 && unique[^1] >= nav.Space.Links.Count / 2);
            faults.Earlier = LinkFrom(nav, nav.Space.Links[unique[0]]);
            faults.Later = LinkFrom(nav, nav.Space.Links[unique[^1]]);
        }
        AssertSerialFailureWins(faults);
    }

    [Fact]
    public void ProviderExceptionIsTheSerialException()
    {
        var faults = new Faults { Earlier = Near(Centre(1, 2), 0.45f, 0.8f) };
        using var scene = new SmallStairScene(fault: faults.Check);
        var serial = Assert.Throws<ProviderFaultException>(() => scene.Build(null));
        foreach (int degree in Degrees)
        {
            var parallel = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(degree)));
            Assert.Equal(serial.Message, parallel.Message);
            Assert.Contains(nameof(Faults.Check), parallel.StackTrace);
            scene.AssertUsable();
        }
    }

    [Fact]
    public void FailureWhileASiblingHoldsItsLeaseReleasesEveryGate()
    {
        var holding = new ManualResetEventSlim();
        var failed = new ManualResetEventSlim();
        Func<Vector3, Vector3, bool> earlier = Near(Centre(1, 0), 0.45f, 0.8f), sibling = Near(Centre(0, 5), 0.2f, 0.6f);
        bool coordinate = false, siblingHeld = false, siblingReleased = false;
        using var scene = new SmallStairScene(fault: (position, displacement) =>
        {
            if (coordinate && sibling(position, displacement) && !holding.IsSet)
            {
                holding.Set();
                siblingHeld = true;
                // The sibling stays inside its leased query until the earlier failure is thrown.
                siblingReleased = failed.Wait(Timeout);
            }
            if (!earlier(position, displacement)) return;
            if (coordinate && !failed.IsSet) Assert.True(holding.Wait(Timeout));
            failed.Set();
            throw new ProviderFaultException("earlier", position);
        });
        string expected = Assert.Throws<ProviderFaultException>(() => scene.Build(null)).Message;
        failed.Reset();
        coordinate = true;
        var thrown = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(2)));
        Assert.Equal(expected, thrown.Message);
        Assert.True(siblingHeld);
        Assert.True(siblingReleased);
        scene.AssertUsable();
    }

    [Fact]
    public void SetupFailureAfterPartialWorkerConstructionReleasesTheBuiltWorkers()
    {
        using var scene = new SmallStairScene();
        var released = new List<int>();
        var setup = new InvalidOperationException("setup fault");
        using (ContactNavigationBuildHooks.Install(new()
        {
            WorkerPinned = worker => { if (worker == 1) throw setup; },
            Releasing = released.Add,
        }))
            Assert.Same(setup, Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(3))));
        Assert.Equal([0, 1, 2], released);
        Assert.False(setup.Data.Contains(ContactNavigationWorkers.CleanupFailuresKey));
        scene.AssertUsable();
    }

    [Fact]
    public void CleanupFailureRidesOnThePrimaryFailure()
    {
        var faults = new Faults { Earlier = Near(Centre(1, 2), 0.45f, 0.8f) };
        using var scene = new SmallStairScene(fault: faults.Check);
        string expected = Assert.Throws<ProviderFaultException>(() => scene.Build(null)).Message;
        var released = new List<int>();
        var cleanup = new InvalidOperationException("release fault");
        using (ContactNavigationBuildHooks.Install(new()
        {
            Releasing = worker =>
            {
                released.Add(worker);
                if (worker == 1) throw cleanup;
            },
        }))
        {
            var primary = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(3)));
            Assert.Equal(expected, primary.Message);
            var attached = Assert.IsType<AggregateException>(primary.Data[ContactNavigationWorkers.CleanupFailuresKey]);
            Assert.Same(cleanup, Assert.Single(attached.InnerExceptions));
        }
        Assert.Equal([0, 1, 2], released);
        scene.AssertUsable();
    }

    [Fact]
    public void CleanupFailureAfterASuccessfulBuildPublishesNothing()
    {
        using var scene = new SmallStairScene();
        var cleanup = new InvalidOperationException("release fault");
        using (ContactNavigationBuildHooks.Install(new() { Releasing = worker => { if (worker == 0) throw cleanup; } }))
            Assert.Same(cleanup, Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(2))));
        scene.AssertUsable();
    }

    static void AssertSerialFailureWins(Faults faults)
    {
        using var scene = new SmallStairScene(fault: faults.Check);
        Func<Vector3, Vector3, bool>? earlier = faults.Earlier, later = faults.Later;
        faults.Earlier = null;
        Assert.Equal("later", Assert.Throws<ProviderFaultException>(() => scene.Build(null)).Tag);
        faults.Earlier = earlier;
        faults.Later = null;
        Assert.Equal("earlier", Assert.Throws<ProviderFaultException>(() => scene.Build(null)).Tag);
        faults.Later = later;
        var serial = Assert.Throws<ProviderFaultException>(() => scene.Build(null));
        Assert.Equal("earlier", serial.Tag);

        foreach (int degree in Degrees)
        {
            faults.Coordinate();
            var parallel = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(degree)));
            Assert.Equal(serial.Message, parallel.Message);
            Assert.True(faults.EarlierWaitedForLater, $"The later failure did not throw first at degree {degree}.");
            scene.AssertUsable();
        }
    }

    static Vector2 Centre(int x, int z) =>
        new(-0.3f + (x + 0.5f) * SmallStairScene.Cell, -0.9f + (z + 0.5f) * SmallStairScene.Cell);

    // Any capsule query on the cell's column within the height band. Holds and anchors query a shell about 0.26 m above
    // the feet for this tuning.
    static Func<Vector3, Vector3, bool> Near(Vector2 cell, float lowCentre, float highCentre) => (position, _) =>
        MathF.Abs(position.X - cell.X) < 0.01f && MathF.Abs(position.Z - cell.Y) < 0.01f &&
        position.Y >= lowCentre && position.Y <= highCentre;

    // A moving sweep that starts on the cell's column and heads along the direction. Moving sweeps carry the step-up
    // shell, about 0.47 m above the resting feet for this tuning, so the band keeps the layer below and above apart.
    static Func<Vector3, Vector3, bool> MovingFrom(NavGrid grid, int x, int z, Vector2 direction)
    {
        Vector2 cell = grid.CellCenter(x, z);
        float resting = grid.RestingHeightAt(x, z)!.Value;
        return (position, displacement) =>
        {
            var heading = new Vector2(displacement.X, displacement.Z);
            return MathF.Abs(position.X - cell.X) < 0.01f && MathF.Abs(position.Z - cell.Y) < 0.01f &&
                position.Y - resting is > 0.35f and < 0.6f && heading.Length() > 0.005f &&
                Vector2.Dot(Vector2.Normalize(heading), direction) > 0.95f;
        };
    }

    static Func<Vector3, Vector3, bool> LinkFrom(GroundNavigation nav, NavLink link)
    {
        NavGrid from = nav.Space.Layers[link.FromLayer], to = nav.Space.Layers[link.ToLayer];
        return MovingFrom(from, link.FromX, link.FromZ,
            Vector2.Normalize(to.CellCenter(link.ToX, link.ToZ) - from.CellCenter(link.FromX, link.FromZ)));
    }

    // Accepted +Z edges of one layer in row order.
    static IEnumerable<(int x, int z)> ForwardEdges(GroundNavigation nav, int layer)
    {
        NavTraversalLayer exits = nav.Graph.Layers[layer];
        for (int z = 0; z < exits.Height; z++)
            for (int x = 0; x < exits.Width; x++)
                if ((exits.ExitMask(x, z) & (1 << 2)) != 0) yield return (x, z);
    }

    sealed class Faults
    {
        ManualResetEventSlim _laterThrown = new();
        bool _coordinate;
        int _waited;

        internal Func<Vector3, Vector3, bool>? Earlier { get; set; }
        internal Func<Vector3, Vector3, bool>? Later { get; set; }
        internal bool EarlierWaitedForLater => Volatile.Read(ref _waited) == 1;

        internal void Coordinate()
        {
            _laterThrown = new ManualResetEventSlim();
            _coordinate = true;
            _waited = 0;
        }

        internal void Check(Vector3 position, Vector3 displacement)
        {
            if (Later?.Invoke(position, displacement) == true)
            {
                _laterThrown.Set();
                throw new ProviderFaultException("later", position);
            }
            if (Earlier?.Invoke(position, displacement) != true) return;
            if (_coordinate && _laterThrown.Wait(Timeout)) Interlocked.Exchange(ref _waited, 1);
            throw new ProviderFaultException("earlier", position);
        }
    }
}
