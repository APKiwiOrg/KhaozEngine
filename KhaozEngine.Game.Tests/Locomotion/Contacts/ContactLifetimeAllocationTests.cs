using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed partial class ContactLifetimeAllocationTests(ITestOutputHelper output) : IDisposable
{
    [Fact]
    public void ExplicitTickAllocatesOnlyNamedLifetimeTokens()
    {
        SupportQueryDiagnostics.Enable(true);
        var tokens = Inventory();
        long total = tokens.Movement + tokens.Physics + tokens.Pin;
        output.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}. " +
            $"TieredCompilation {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}. " +
            $"MovementQueryLease and release binding {tokens.Movement} B, ReadLease {tokens.Physics} B, pin {tokens.Pin} B, total {total} B.");
        var names = new[] { "stationary", "flat box", "pushed seat", "stairs box", "explicit rounding handoff",
            "explicit changed-axis rounding", "explicit medium descent retry", "explicit dry swim handoff" };
        var samples = new List<SupportCostSample>();
        foreach (string name in names)
        {
            SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name)
                with
            { Explicit = true };
            var phases = new AllocationPhaseRecorder();
            SupportCostSample sample;
            ContactAllocationDiagnostics.SetObserver(phases.Observe);
            try
            {
                sample = new SupportCostSampler(false) { BeforeWarm = phases.Reset, IsolateAllocations = true }.Measure(row, "ExplicitTick")[0];
            }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
            foreach (ContactAllocationPhase phase in Enum.GetValues<ContactAllocationPhase>())
                if (phases.Bytes[(int)phase] != 0)
                    output.WriteLine($"{name} phase {phase}. Exclusive {phases.Bytes[(int)phase]} B, calls {phases.Calls[(int)phase]}, GC {phases.Collections[(int)phase, 0]}/{phases.Collections[(int)phase, 1]}/{phases.Collections[(int)phase, 2]}.");
            Assert.Equal(sample.WarmBytes, phases.Bytes.Sum());
            output.WriteLine($"{name}. Cold {sample.ColdBytes} B, warm {sample.WarmBytes} B, Find {sample.Counts.Find}, outcome {sample.Outcome}, routes {sample.Routes}.");
            samples.Add(sample);
            Assert.False(sample.Refusal);
            Assert.Equal(sample.ColdEndState, sample.EndState);
            using var borrowed = new SupportCostExecution(row, "ExplicitTickBorrowed");
            ContactAllocationDiagnostics.SetObserver(phases.Observe);
            SupportCostOutput cold, warm;
            long supportBytes;
            try
            {
                using var region = SupportAllocationRegion.Start();
                using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route)) cold = borrowed.Invoke();
                phases.Reset();
                long before = GC.GetAllocatedBytesForCurrentThread();
                using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Route)) warm = borrowed.Invoke();
                supportBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            finally
            {
                ContactAllocationDiagnostics.SetObserver(null);
            }
            output.WriteLine($"Borrowed outcomes {name}. Cold {cold.Outcome}, warm {warm.Outcome}, refused {warm.Refused}.");
            output.WriteLine($"Borrowed {name}. TieredCompilation {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}, {supportBytes} B.");
            foreach (ContactAllocationPhase phase in Enum.GetValues<ContactAllocationPhase>())
                if (phases.Bytes[(int)phase] != 0)
                    output.WriteLine($"Borrowed {name} phase {phase}. Exclusive {phases.Bytes[(int)phase]} B, calls {phases.Calls[(int)phase]}, GC {phases.Collections[(int)phase, 0]}/{phases.Collections[(int)phase, 1]}/{phases.Collections[(int)phase, 2]}.");
            Assert.Equal(supportBytes, phases.Bytes.Sum());
            Assert.Equal(0, supportBytes);
            Assert.Equal(cold, warm);
            var slots = (CapsuleShape?[])typeof(ContactCapsuleShapes).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(borrowed.LastMovementLease!.Workspace.Shapes)!;
            output.WriteLine($"Shapes {name}. " + string.Join(", ", Enum.GetValues<ContactCapsuleRole>().Select(role =>
                $"{role} {slots.Skip((int)role * ContactCapsuleShapes.SlotsPerRole).Take(ContactCapsuleShapes.SlotsPerRole).Count(s => s is not null)}")));
        }
        Assert.True(samples.Max(s => s.Counts.Find) > samples.Min(s => s.Counts.Find));
        Assert.True(samples.Single(s => s.Scene == "pushed seat").Routes.PushedPlacements > 0);
        Assert.Equal(3, samples.Single(s => s.Scene == "stairs box").SupportReads.Count(q => q.ReachUp > 1));
        Assert.True(samples.Single(s => s.Scene == "explicit rounding handoff").Counts.ModeSegments > 1);
        Assert.All(samples, sample => Assert.True(total == sample.WarmBytes, $"{sample.Scene}. Expected {total} B, actual {sample.WarmBytes} B."));
    }

    [Fact]
    public void BorrowedSampleAndBindingAllocateNothing()
    {
        var row = new SupportCostRow("explicit entry", Explicit: true) { Analytic = true, TerrainHeight = 0 };
        using var execution = new SupportCostExecution(row, "ExplicitTick");
        using var lease = (MovementQueryLease)execution.Producer.Acquire().Lease!;
        var request = new MovementSupportRequest(new(row.Input.Position, 0.4f, 0.9f, new("world", "room"), null),
            0.4f, 0.4f, MathF.PI / 4, new(new("world", "room"), row.Feet));
        using var region = SupportAllocationRegion.Start();
        Assert.Equal(MovementAvailability.Known, lease.FindContactSupport(request, row.Query, out var support, out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        MovementAvailability sampled = lease.SampleAnalyticGround(request, out var ground);
        MovementAvailability bound = lease.BindContactSupport(request, support, out var binding);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(MovementAvailability.Known, sampled);
        Assert.Equal(MovementAvailability.Known, bound);
        Assert.Equal(ground!.Value.Owner, binding.Owner);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void WarmDryTicksStayWithinLeaseCeilingWithoutFindGrowth()
    {
        SupportQueryDiagnostics.Enable(true);
        var samples = new List<SupportCostSample>();
        foreach (string name in new[] { "stationary", "flat box", "pushed seat", "stairs box" })
        {
            var row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);
            SupportCostSample sample = new SupportCostSampler(false) { IsolateAllocations = true }.Measure(row, "DryTick")[0];
            output.WriteLine($"Dry {name}. {sample.WarmBytes} B, Find {sample.Counts.Find}.");
            Assert.InRange(sample.WarmBytes, 0, 208);
            samples.Add(sample);
        }
        Assert.All(samples, sample => Assert.Equal(samples[0].WarmBytes, sample.WarmBytes));
        Assert.True(samples.Max(s => s.Counts.Find) > samples.Min(s => s.Counts.Find));
    }

    static (long Movement, long Physics, long Pin) Inventory()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        using var region = SupportAllocationRegion.Start();
        var environment = new EnvironmentAcquisitionFixture(view) { ContactSupportEnabled = true };
        environment.Prepare(environment.Scope);
        using (var cold = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease()) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var physics = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        long physicsBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        environment.TryPinPrepared(environment.Scope, physics, out var coldPin);
        coldPin!.Dispose();
        before = GC.GetAllocatedBytesForCurrentThread();
        environment.TryPinPrepared(environment.Scope, physics, out var pin);
        long pinBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var witness = pin!.Witness;
        var workspace = new ContactTickWorkspace();
        before = GC.GetAllocatedBytesForCurrentThread();
        var movement = new MovementQueryLease(view, physics, pin, witness, pin.Frame, pin.EnvironmentGeneration,
            new Action(Released), workspace);
        long movementBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        movement.Dispose();
        return (movementBytes, physicsBytes, pinBytes);
    }

    public void Dispose()
    {
        SupportQueryDiagnostics.Enable(false);
        SupportQueryDiagnostics.Reset();
    }

    static void Released() { }
}
