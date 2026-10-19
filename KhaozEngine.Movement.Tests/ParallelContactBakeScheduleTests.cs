using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.SmallStairScene;

namespace KhaozEngine.Tests.Movement;

// More bands than workers: bands are claimed in ascending order, merged by band index and never by completion, so the
// bytes and the selected failure match the serial build however unevenly the rows cost.
public class ParallelContactBakeScheduleTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    const int Rows = 6;

    [Fact]
    public void BandCountIsFourPerWorkerCappedByTheRows()
    {
        Assert.Equal(8, ContactNavigationWorkers.BandCount(2, 100));
        Assert.Equal(Rows, ContactNavigationWorkers.BandCount(2, Rows));
        Assert.Equal(1, ContactNavigationWorkers.BandCount(8, 1));
        Assert.Equal(0, ContactNavigationWorkers.BandCount(8, 0));
    }

    [Fact]
    public void AnEarlierFailureCanCutOffTheUnclaimedDistantFault()
    {
        // The old edge fixture held layer 0 row 0 while the other worker traversed bands 1 through 6.
        // Its later fault was in band 7. A missed wait let ordinal 2 fail before that band was claimed.
        var cutoff = new ContactNavigationCutoff();
        var schedule = new ContactNavigationSchedule(12, ContactNavigationWorkers.BandCount(2, 12),
            row => row * 16L, cutoff);
        Assert.True(schedule.TryClaim(out var earlier));
        Assert.True(earlier.Admits(2));
        for (int index = 1; index < 7; index++)
        {
            Assert.True(schedule.TryClaim(out var intervening));
            Assert.Equal(index, intervening.Index);
            Assert.True(intervening.End <= 10);
        }
        var failure = new ProviderFaultException("earlier", default);
        earlier.Fail(failure);
        Assert.False(schedule.TryClaim(out _));
        Assert.Same(failure, Assert.Throws<ProviderFaultException>(cutoff.ThrowSelected));
    }

    [Fact]
    public void DuplicateAnchorBelowAWorkerFailureThrowsTheDuplicate()
    {
        var cutoff = new ContactNavigationCutoff();
        cutoff.Fail(7, new InvalidOperationException("worker failure"));
        var anchors = new Dictionary<Vector3, float>();
        ContactAdmissionBand.MergeAnchors(SharedKeyBands(duplicateOrdinal: 5), cutoff, anchors);
        Assert.Throws<ArgumentException>(cutoff.ThrowSelected);
        Assert.Equal(2, anchors.Count);
    }

    [Fact]
    public void WorkerFailureBelowADuplicateAnchorThrowsTheWorkerFailure()
    {
        var cutoff = new ContactNavigationCutoff();
        var failure = new InvalidOperationException("worker failure");
        cutoff.Fail(3, failure);
        var anchors = new Dictionary<Vector3, float>();
        ContactAdmissionBand.MergeAnchors(SharedKeyBands(duplicateOrdinal: 5), cutoff, anchors);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(cutoff.ThrowSelected));
        // Merging stopped at the first anchor at or above the cutoff.
        Assert.Single(anchors);
    }

    // Two bands in row order sharing one key: the first inserts it at ordinal 1, the second at the duplicate ordinal,
    // after a distinct anchor at ordinal 4.
    static ContactAdmissionBand?[] SharedKeyBands(long duplicateOrdinal)
    {
        var key = new Vector3(0.15f, 0.1875f, -0.75f);
        var first = new ContactAdmissionBand(0, 2);
        var second = new ContactAdmissionBand(2, 4);
        first.Anchors.Add((key, 0.2f, 1));
        second.Anchors.Add((new Vector3(-0.15f, 0, -0.45f), 0f, 4));
        second.Anchors.Add((key, 0.2f, duplicateOrdinal));
        return [first, second];
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void MoreBandsThanWorkersMatchTheSerialBytesWhenOneRowIsSlow(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        int bands = ContactNavigationWorkers.BandCount(2, Rows);
        Assert.True(bands > 2);
        var slow = new SlowFirstRow(bands);
        using var scene = new SmallStairScene(fault: slow.Hold);
        using PhysicsNavBake capture = scene.Capture();
        NavBakeProfile[] profiles = [new("player", StairTuning, default)];
        byte[] serial = ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles,
            new NavBakeExecution(1) { UseStableContactHoldReuse = mode == ContactNavigationReuseMode.Combined }));

        slow.Arm();
        byte[] parallel;
        using (ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = slow.Completed,
            DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
            DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined
        }))
            parallel = ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles,
                new NavBakeExecution(2)));
        Assert.Equal(serial, parallel);
        Assert.True(slow.Waited, "The slow row did not outlast the other admission bands.");
        // One worker held row 0 while the other claimed and finished every other admission band.
        Assert.Single(slow.OtherThreads);
        Assert.DoesNotContain(slow.HeldThread, slow.OtherThreads);
        scene.AssertUsable();
    }

    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void ReuseFailureSelectionMatchesSerialUnderForcedBands(ContactNavigationReuseMode mode)
    {
        using var reuse = ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(mode));
        Func<Vector3, Vector3, bool> earlier = ParallelContactBakeFailureTests.Near(
            ParallelContactBakeFailureTests.Centre(1, 0), 0.45f, 0.8f);
        Func<Vector3, Vector3, bool> later = ParallelContactBakeFailureTests.Near(
            ParallelContactBakeFailureTests.Centre(0, Rows - 1), 0.2f, 0.6f);
        bool coordinate = false;
        var laterThrown = new ManualResetEventSlim();
        int waited = 0;
        using var scene = new SmallStairScene(fault: (position, displacement) =>
        {
            if (later(position, displacement))
            {
                laterThrown.Set();
                throw new ProviderFaultException("later", position);
            }
            if (!earlier(position, displacement)) return;
            if (Volatile.Read(ref coordinate) && laterThrown.Wait(Timeout)) Interlocked.Exchange(ref waited, 1);
            throw new ProviderFaultException("earlier", position);
        });
        var serial = Assert.Throws<ProviderFaultException>(() => scene.Build(new(1) { UseStableContactHoldReuse = true }));
        Assert.Equal("earlier", serial.Tag);

        var completions = new ConcurrentDictionary<int, int>();
        Volatile.Write(ref coordinate, true);
        using (ContactNavigationBuildHooks.Install(new()
        {
            DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
            DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
            BandCompleted = (phase, _) =>
            {
                if (phase == ContactNavigationPhase.Admission)
                    completions.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, count) => count + 1);
            },
        }))
        {
            var parallel = Assert.Throws<ProviderFaultException>(() => scene.Build(new NavBakeExecution(2)));
            Assert.Equal(serial.Message, parallel.Message);
        }
        Assert.Equal(1, Volatile.Read(ref waited));
        // The worker that reached the last row first finished several bands on the way.
        Assert.True(completions.Values.Max() >= 2);
        scene.AssertUsable();
    }

    // Holds the first query on row 0 in the parallel build until every other admission band has completed.
    sealed class SlowFirstRow(int bands)
    {
        readonly float _row0 = ParallelContactBakeFailureTests.Centre(0, 0).Y;
        readonly ManualResetEventSlim _othersDone = new();
        readonly ConcurrentDictionary<int, byte> _others = new();
        int _armed, _held, _completed, _waited;

        internal bool Waited => Volatile.Read(ref _waited) == 1;
        internal int HeldThread { get; private set; }
        internal int[] OtherThreads => [.. _others.Keys];

        internal void Arm() => Volatile.Write(ref _armed, 1);

        internal void Hold(Vector3 position, Vector3 displacement)
        {
            if (Volatile.Read(ref _armed) == 0 || MathF.Abs(position.Z - _row0) >= 0.01f ||
                Interlocked.Exchange(ref _held, 1) != 0) return;
            HeldThread = Environment.CurrentManagedThreadId;
            if (_othersDone.Wait(Timeout)) Volatile.Write(ref _waited, 1);
        }

        internal void Completed(ContactNavigationPhase phase, int band)
        {
            if (phase != ContactNavigationPhase.Admission || band == 0) return;
            _others.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (Interlocked.Increment(ref _completed) == bands - 1) _othersDone.Set();
        }
    }
}
