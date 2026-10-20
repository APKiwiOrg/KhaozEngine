using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace KhaozEngine.Tests.Movement;

// Every fault here is a pure function of the query it sees, so the set of failing work items is fixed and the
// serial build's first failure is the expected exception on every interleaving. Coordination only reorders wall-clock
// time: the earlier fault waits, bounded, until the later worker has recorded its failure and terminated.
public class ParallelContactBakeFailureTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    static readonly int[] Degrees = [2, 3, 8];

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void TwoAdmissionFailuresOnDifferentRawSurfacesThrowTheSerialFailure(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var faults = new Faults
        {
            // The right column's slab top in the first row, after its floor surface admits.
            Earlier = Near(Centre(1, 0), 0.45f, 0.8f),
            // The left deck in the last row.
            Later = Near(Centre(0, 5), 0.2f, 0.6f),
        };
        AssertSerialFailureWins(faults);
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void TwoEdgeFailuresInDifferentLayersThrowTheSerialFailure(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var faults = new Faults();
        using (var reference = new SmallStairScene())
        {
            GroundNavigation nav = reference.Build(null);
            Assert.Equal(2, nav.Space.Layers.Count);
            // Faults straddle the layer boundary in adjacent bands at every tested degree. Holding the first
            // row for a fault near the final row made the wait depend on nearly the whole edge phase.
            // The left deck permits the full lift that MovingFrom targets. The right floor sits under the slab.
            (int x, int z) earlier = DirectedEdges(nav, 0, 3).Last(edge => edge.x == 0);
            (int x, int z) later = DirectedEdges(nav, 1, 2).First();
            Assert.Equal(nav.Space.Layers[0].Height - 1, earlier.z);
            Assert.Equal(0, later.z);
            AssertAdjacentBands(nav.Space.Layers.Sum(layer => layer.Height), nav.Space.Layers[0].Height, earlier.z,
                nav.Space.Layers[0].Height + later.z);
            faults.Earlier = MovingFrom(nav.Space.Layers[0], earlier.x, earlier.z, -Vector2.UnitY);
            faults.Later = MovingFrom(nav.Space.Layers[1], later.x, later.z, Vector2.UnitY);
        }
        AssertSerialFailureWins(faults);
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void TwoStairLinkFailuresInCandidateOrderThrowTheSerialFailure(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var faults = new Faults();
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

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void ProviderExceptionIsTheSerialException(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var faults = new Faults { Earlier = Near(Centre(1, 2), 0.45f, 0.8f) };
        using var scene = new SmallStairScene(fault: faults.Check);
        var serial = Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true }));
        foreach (int degree in Degrees)
        {
            var parallel = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(degree)));
            Assert.Equal(serial.Message, parallel.Message);
            Assert.Contains(nameof(Faults.Check), parallel.StackTrace);
            scene.AssertUsable();
        }
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void FailureWhileASiblingHoldsItsLeaseReleasesEveryGate(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
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
        string expected = Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true })).Message;
        failed.Reset();
        coordinate = true;
        var thrown = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(2)));
        Assert.Equal(expected, thrown.Message);
        Assert.True(siblingHeld);
        Assert.True(siblingReleased);
        scene.AssertUsable();
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void SetupFailureAfterPartialWorkerConstructionReleasesTheBuiltWorkers(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var scene = new SmallStairScene();
        var released = new List<int>();
        var setup = new InvalidOperationException("setup fault");
        using (ContactNavigationBuildHooks.Install(new()
        {
            DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
            DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
            WorkerPinned = worker => { if (worker == 1) throw setup; },
            Releasing = released.Add,
        }))
            Assert.Same(setup, Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(3))));
        Assert.Equal([0, 1, 2], released);
        Assert.False(setup.Data.Contains(ContactNavigationWorkers.CleanupFailuresKey));
        scene.AssertUsable();
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void CleanupFailureRidesOnThePrimaryFailure(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var faults = new Faults { Earlier = Near(Centre(1, 2), 0.45f, 0.8f) };
        using var scene = new SmallStairScene(fault: faults.Check);
        string expected = Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true })).Message;
        var released = new List<int>();
        var cleanup = new InvalidOperationException("release fault");
        using (ContactNavigationBuildHooks.Install(new()
        {
            DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
            DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
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

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void CleanupFailureAfterASuccessfulBuildPublishesNothing(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        using var scene = new SmallStairScene();
        var cleanup = new InvalidOperationException("release fault");
        using (ContactNavigationBuildHooks.Install(new()
        {
            DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
            DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
            Releasing = worker => { if (worker == 0) throw cleanup; }
        }))
            Assert.Same(cleanup, Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(2))));
        scene.AssertUsable();
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void InterruptedJoinStillJoinsEveryWorkerBeforeRelease(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        var holding = new ManualResetEventSlim();
        var resume = new ManualResetEventSlim();
        var released = new ManualResetEventSlim();
        float row0 = Centre(0, 0).Y;
        int held = 0;
        bool resumed = false;
        Thread? heldWorker = null;
        // The first query on row 0 holds its admission worker inside a leased query until the test resumes it.
        using var scene = new SmallStairScene(fault: (position, _) =>
        {
            if (MathF.Abs(position.Z - row0) >= 0.01f || Interlocked.Exchange(ref held, 1) != 0) return;
            heldWorker = Thread.CurrentThread;
            holding.Set();
            resumed = resume.Wait(Timeout);
        });
        var aliveAtRelease = new List<bool>();
        Exception? thrown = null;
        var builder = new Thread(() =>
        {
            using var hooks = ContactNavigationBuildHooks.Install(new()
            {
                DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
                DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
                Releasing = _ =>
                {
                    aliveAtRelease.Add(heldWorker!.IsAlive);
                    released.Set();
                },
            });
            try { scene.Build(new NavBakeExecution(2)); }
            catch (Exception failure) { thrown = failure; }
        });
        builder.Start();
        try
        {
            Assert.True(holding.Wait(Timeout));
            // Every worker has started, so the builder blocks only in a phase join.
            Assert.True(SpinWait.SpinUntil(() => (builder.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout));
            builder.Interrupt();
            // A join that gave up on the interrupt would release the workers while the held one is still inside
            // its query. The fixed join keeps waiting, so nothing is released before the worker resumes.
            Assert.False(released.Wait(TimeSpan.FromMilliseconds(500)), "Workers were released under a live worker.");
        }
        finally
        {
            resume.Set();
            Assert.True(builder.Join(Timeout));
        }
        Assert.True(resumed);
        Assert.IsType<ThreadInterruptedException>(thrown);
        Assert.NotEmpty(aliveAtRelease);
        Assert.DoesNotContain(true, aliveAtRelease);
        scene.AssertUsable();
    }

    static void AssertSerialFailureWins(Faults faults)
    {
        using var scene = new SmallStairScene(fault: faults.Check);
        Func<Vector3, Vector3, bool>? earlier = faults.Earlier, later = faults.Later;
        faults.Earlier = null;
        Assert.Equal("later", Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true })).Tag);
        faults.Earlier = earlier;
        faults.Later = null;
        Assert.Equal("earlier", Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true })).Tag);
        faults.Later = later;
        var serial = Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true }));
        Assert.Equal("earlier", serial.Tag);

        foreach (int degree in Degrees)
        {
            faults.Coordinate();
            var parallel = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(degree)));
            Assert.Equal(serial.Message, parallel.Message);
            Assert.Contains(nameof(Faults.Check), parallel.StackTrace);
            Assert.True(faults.EarlierWaitedForLater,
                $"The later failure did not finish first at degree {degree}. {faults.CoordinationState}");
            scene.AssertUsable();
        }
    }

    internal static Vector2 Centre(int x, int z) =>
        new(-0.3f + (x + 0.5f) * SmallStairScene.Cell, -0.9f + (z + 0.5f) * SmallStairScene.Cell);

    // Any capsule query on the cell's column within the height band. Holds and anchors query a shell about 0.26 m above
    // the feet for this tuning.
    internal static Func<Vector3, Vector3, bool> Near(Vector2 cell, float lowCentre, float highCentre) => (position, _) =>
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

    // Accepted edges of one layer in row order, with the production neighbor index.
    static IEnumerable<(int x, int z)> DirectedEdges(GroundNavigation nav, int layer, int direction)
    {
        NavTraversalLayer exits = nav.Graph.Layers[layer];
        for (int z = 0; z < exits.Height; z++)
            for (int x = 0; x < exits.Width; x++)
                if ((exits.ExitMask(x, z) & (1 << direction)) != 0) yield return (x, z);
    }

    static void AssertAdjacentBands(int rows, int workerRows, int earlierRow, int laterRow)
    {
        foreach (int degree in Degrees)
        {
            int count = ContactNavigationWorkers.BandCount(Math.Min(degree, workerRows), rows);
            var schedule = new ContactNavigationSchedule(rows, count, row => row, new ContactNavigationCutoff());
            int earlierBand = -1, laterBand = -1;
            while (schedule.TryClaim(out var band))
            {
                if (earlierRow >= band.Start && earlierRow < band.End) earlierBand = band.Index;
                if (laterRow >= band.Start && laterRow < band.End) laterBand = band.Index;
            }
            Assert.True(earlierBand >= 0);
            Assert.Equal(earlierBand + 1, laterBand);
        }
    }

    sealed class Faults : IDisposable
    {
        readonly ManualResetEventSlim _laterThrown = new();
        Thread? _laterWorker;
        bool _coordinate;
        int _waited, _signalled, _earlierMatches, _laterMatches;

        internal Func<Vector3, Vector3, bool>? Earlier { get; set; }
        internal Func<Vector3, Vector3, bool>? Later { get; set; }
        internal bool EarlierWaitedForLater => Volatile.Read(ref _waited) == 1;
        internal string CoordinationState => $"Earlier matches: {_earlierMatches}, later matches: {_laterMatches}, " +
            $"signal observed: {_signalled}, later worker: {_laterWorker?.ThreadState}.";

        internal void Coordinate()
        {
            _laterThrown.Reset();
            _laterWorker = null;
            _coordinate = true;
            _waited = _signalled = _earlierMatches = _laterMatches = 0;
        }

        internal void Check(Vector3 position, Vector3 displacement)
        {
            if (Later?.Invoke(position, displacement) == true)
            {
                Interlocked.Increment(ref _laterMatches);
                _laterWorker = Thread.CurrentThread;
                _laterThrown.Set();
                throw new ProviderFaultException("later", position);
            }
            if (Earlier?.Invoke(position, displacement) != true) return;
            Interlocked.Increment(ref _earlierMatches);
            if (_coordinate)
            {
                long start = Stopwatch.GetTimestamp();
                if (_laterThrown.Wait(Timeout))
                {
                    Interlocked.Exchange(ref _signalled, 1);
                    // The event precedes the throw. Joining proves the later failure reached the worker's cutoff
                    // before this earlier failure can throw, within the original wait budget.
                    TimeSpan remaining = Timeout - Stopwatch.GetElapsedTime(start);
                    if (_laterWorker!.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                        Interlocked.Exchange(ref _waited, 1);
                }
            }
            throw new ProviderFaultException("earlier", position);
        }

        public void Dispose() => _laterThrown.Dispose();
    }
}
