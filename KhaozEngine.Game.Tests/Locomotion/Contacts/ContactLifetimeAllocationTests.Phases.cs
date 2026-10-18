using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class ContactLifetimeAllocationTests
{
    [Theory]
    [InlineData("stationary")]
    [InlineData("explicit rounding handoff")]
    public void AcquiredTickPhaseAllocationMatchesNamedTokens(string name)
    {
        var tokens = Inventory();
        long total = tokens.Movement + tokens.Physics + tokens.Pin;
        var row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name)
            with
        { Explicit = true };
        SupportQueryDiagnostics.Enable(true);
        var recorder = new AllocationPhaseRecorder();
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        try
        {
            SupportCostSample sample = new SupportCostSampler(false) { BeforeWarm = recorder.Reset, IsolateAllocations = true }.Measure(row, "ExplicitTick")[0];
            long bytes = sample.WarmBytes;
            output.WriteLine($"{name}. TieredCompilation {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}, total {bytes} B.");
            foreach (ContactAllocationPhase phase in Enum.GetValues<ContactAllocationPhase>())
                output.WriteLine($"{phase}. Exclusive {recorder.Bytes[(int)phase]} B, calls {recorder.Calls[(int)phase]}, GC {recorder.Collections[(int)phase, 0]}/{recorder.Collections[(int)phase, 1]}/{recorder.Collections[(int)phase, 2]}.");
            Assert.Equal(0, recorder.Depth);
            Assert.Equal(bytes, recorder.Bytes.Sum());
            Assert.False(sample.Refusal);
            Assert.Equal(sample.ColdEndState, sample.EndState);
            Assert.Equal(total, bytes);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }

    [Fact]
    public void ChangingRoundedQueryDimensionsAllocateNothing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var producer = new EnvironmentAcquisitionFixture(view) { ContactSupportEnabled = true };
        using var lease = (MovementQueryLease)producer.Acquire().Lease!;
        var body = new MovementBodyQuery(new(1, 1, 0), 0.4f, 0.9f, new("world", "room"), null);
        Vector3 firstDelta = new(0.1f, 0, 0), nextDelta = new(0.2f, 0, 0);
        Assert.True(MovementCapsuleRounding.TryEnclose(body, firstDelta, out var first, out _, out _));
        Assert.True(MovementCapsuleRounding.TryEnclose(body, nextDelta, out var next, out _, out _));
        Assert.NotEqual(first.Radius, next.Radius);
        Span<CapsuleContact> contacts = stackalloc CapsuleContact[4];
        using var region = SupportAllocationRegion.Start();
        Assert.Equal(MovementAvailability.Known,
            lease.QuerySolidSweepFiltered(first, firstDelta, out _, QueryFilter.StaticsOnly));
        Assert.Equal(MovementAvailability.Known,
            lease.QuerySolidContactsFiltered(first, 0, contacts, out _, QueryFilter.StaticsOnly));
        long before = GC.GetAllocatedBytesForCurrentThread();
        MovementAvailability swept = lease.QuerySolidSweepFiltered(next, nextDelta, out var sweep, QueryFilter.StaticsOnly);
        MovementAvailability contacted = lease.QuerySolidContactsFiltered(next, 0, contacts, out var contact, QueryFilter.StaticsOnly);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        output.WriteLine($"Changed rounded dimensions. Radius {first.Radius:R} -> {next.Radius:R}, allocation {bytes} B.");
        Assert.Equal(MovementAvailability.Known, swept);
        Assert.Equal(MovementAvailability.Known, contacted);
        Assert.Equal(CapsuleSweepStatus.Clear, sweep.Status);
        Assert.True(contact.Complete);
        Assert.Equal(0, contact.Written);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void AllocationRegionStillCountsFreshShapes()
    {
        CapsuleShape? shape = null;
        long bytes = AllocationMeasurement.Measure(nameof(AllocationRegionStillCountsFreshShapes), () =>
            shape = new CapsuleShape(0.4f, 1));
        GC.KeepAlive(shape);
        output.WriteLine($"Positive allocation control. Fresh capsule {bytes} B.");
        Assert.True(bytes > 0);
    }

    sealed class AllocationPhaseRecorder
    {
        readonly ContactAllocationPhase[] stack = new ContactAllocationPhase[64];
        long previous;
        readonly int[] previousCollections = new int[3];
        internal int[,] Collections { get; } = new int[Enum.GetValues<ContactAllocationPhase>().Length, 3];
        internal long[] Bytes { get; } = new long[Enum.GetValues<ContactAllocationPhase>().Length];
        internal int[] Calls { get; } = new int[Enum.GetValues<ContactAllocationPhase>().Length];
        internal int Depth { get; private set; }

        internal void Reset()
        {
            Array.Clear(Bytes);
            Array.Clear(Calls);
            Array.Clear(Collections);
            for (int generation = 0; generation < 3; generation++) previousCollections[generation] = GC.CollectionCount(generation);
            previous = GC.GetAllocatedBytesForCurrentThread();
        }

        internal void Observe(ContactAllocationPhase phase, bool entering)
        {
            long now = GC.GetAllocatedBytesForCurrentThread();
            for (int generation = 0; generation < 3; generation++)
            {
                int collections = GC.CollectionCount(generation);
                if (Depth > 0) Collections[(int)stack[Depth - 1], generation] += collections - previousCollections[generation];
                previousCollections[generation] = collections;
            }
            if (Depth > 0) Bytes[(int)stack[Depth - 1]] += now - previous;
            previous = now;
            if (entering)
            {
                stack[Depth++] = phase;
                Calls[(int)phase]++;
            }
            else
            {
                if (Depth == 0 || stack[Depth - 1] != phase) throw new InvalidOperationException("Unbalanced allocation phase.");
                Depth--;
            }
        }
    }
}
