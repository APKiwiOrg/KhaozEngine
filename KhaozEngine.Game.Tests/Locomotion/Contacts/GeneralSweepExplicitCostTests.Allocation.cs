using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class GeneralSweepExplicitCostTests
{
    [Fact]
    public void AcquiredGeneralGeometryTicksAllocateOnlyNamedLifetimeTokens()
    {
        var tokens = ContactLifetimeAllocationTests.Inventory();
        long ceiling = tokens.Movement + tokens.Physics + tokens.Pin;
        output.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}. " +
            $"Movement/release {tokens.Movement} B, physics {tokens.Physics} B, producer pin {tokens.Pin} B.");
        var observations = new List<(SupportQueryCounts Support, QueryCounts Queries, int Samples, long Bytes)>();
        SupportCostRow[] rows = [Row("stairs box"), Row("stairs hull"), Row("stairs mesh"), Row("hidden crate"),
            Row("stairs hull") with { Direction = new Vector2(0.25f, 0) }];
        foreach (var row in rows)
        {
            string name = $"{row.Name}, direction {row.Direction}";
            using var run = new SupportCostExecution(row, "ExplicitTick");
            SupportCostOutput cold = run.Invoke(), prime = run.Invoke(), measured = default;
            Assert.Equal(MovementStepOutcome.Advanced, prime.StepOutcome);
            Assert.False(prime.Refused);
            run.View.Reset();
            SupportQueryDiagnostics.Reset();
            int samples = run.Producer.SampleCalls;
            long bytes = AllocationMeasurement.Measure(name, () => measured = run.Invoke());
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            QueryCounts queries = run.View.Counts;
            samples = run.Producer.SampleCalls - samples;
            var capture = new TickCapture(run);
            SupportCostOutput checkedResult = capture.Invoke();
            AssertAccepted(run, row, capture, checkedResult);
            Assert.Equal(cold, prime);
            Assert.Equal(prime, measured);
            Assert.Equal(measured, checkedResult);
            Assert.Equal(MovementAvailability.Known, run.LastAcquisitionStatus);
            Assert.True(run.Producer.Disposed);
            Assert.Equal(ceiling, bytes);
            observations.Add((counts, queries, samples, bytes));
            output.WriteLine($"{name}. Acquired {bytes} B, Find {counts.Find}, sweeps {queries.Sweeps}, " +
                $"contacts {queries.Penetrations}, body samples {samples}, ground substeps {counts.GroundSubsteps}.");

            using var borrowed = new SupportCostExecution(row, "ExplicitTickBorrowed");
            SupportCostOutput borrowedCold = borrowed.Invoke(), borrowedPrime = borrowed.Invoke(), borrowedMeasured = default;
            long borrowedBytes = AllocationMeasurement.Measure(name + " borrowed", () => borrowedMeasured = borrowed.Invoke());
            var borrowedCapture = new TickCapture(borrowed);
            SupportCostOutput borrowedChecked = borrowedCapture.Invoke();
            AssertAccepted(borrowed, row, borrowedCapture, borrowedChecked);
            Assert.Equal(borrowedCold, borrowedPrime);
            Assert.Equal(borrowedPrime, borrowedMeasured);
            Assert.Equal(borrowedMeasured, borrowedChecked);
            Assert.Equal(measured, borrowedMeasured);
            Assert.Equal(0, borrowedBytes);
            output.WriteLine($"{name}. Borrowed {borrowedBytes} B.");
        }
        Assert.True(observations.Max(x => x.Support.Find) > observations.Min(x => x.Support.Find));
        Assert.True(observations.Max(x => x.Queries.Sweeps) > observations.Min(x => x.Queries.Sweeps));
        Assert.True(observations.Max(x => x.Samples) > observations.Min(x => x.Samples));
        Assert.All(observations, x => Assert.Equal(ceiling, x.Bytes));
    }
}
