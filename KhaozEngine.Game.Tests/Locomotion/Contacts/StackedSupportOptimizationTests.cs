using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Every support cost optimization at once against the reference. A whole optimized tick and a whole tick
/// under <see cref="SupportReferenceEvaluation"/> with <see cref="GroundSignals.AlwaysSample"/> are bit-identical on
/// every ordinary cost row through both doors. Their support reads differ only by the look-ahead reads the SC-13 skip
/// removes. The SC-15 memo still observes every read it serves.</summary>
public sealed class StackedSupportOptimizationTests(ITestOutputHelper output)
{
    static readonly MovementSpaceKey Room = new("world", "room");

    readonly record struct TickResult(object Result, SupportQueryCounts Counts, SupportFindObservation[] Reads);

    [Fact]
    public void WholeOptimizedTicksMatchTheReferenceOnEveryOrdinaryRow()
    {
        int compared = 0, skipped = 0;
        long served = 0;
        foreach (SupportCostRow scene in SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Where(r => !r.Dense))
            foreach (bool explicitDoor in new[] { false, true })
            {
                SupportCostRow row = scene with { Explicit = explicitDoor };
                string name = $"{row.Name} {(explicitDoor ? "explicit" : "dry")}";
                using var execution = new SupportCostExecution(row, explicitDoor ? "ExplicitTick" : "DryTick");
                TickResult reference = Tick(execution, row, reference: true, sample: true);
                TickResult optimized = Tick(execution, row, reference: false, sample: false);
                TickResult sampled = Tick(execution, row, reference: false, sample: true);
                AssertSameBits(reference.Result, optimized.Result, name);
                AssertSameBits(reference.Result, sampled.Result, name + " sampled");
                Assert.Equal(0, reference.Counts.FindMemoHits);

                // With the skip disabled, the stacked optimizations issue the reference's reads bit for bit.
                AssertSameReads(reference.Reads, sampled.Reads, name + " sampled");

                // The skip removes exactly the three look-ahead reads of an ascent already decided, and nothing else.
                SupportFindObservation[] lookAhead = [.. reference.Reads.Where(read => IsLookAhead(read.ReachUp))];
                if (optimized.Reads.Length < reference.Reads.Length)
                {
                    Assert.Equal(GroundSignals.Samples, lookAhead.Length);
                    Assert.True(GroundSignals.Ascending(row.Input.ClimbRate), name);
                    AssertSameReads([.. reference.Reads.Where(read => !IsLookAhead(read.ReachUp))], optimized.Reads, name);
                    skipped++;
                }
                else AssertSameReads(reference.Reads, optimized.Reads, name);

                // The memo serves exactly the exact repeats among the reads, and each served read is still observed.
                foreach (TickResult memo in new[] { optimized, sampled })
                {
                    int repeats = memo.Reads.Length - memo.Reads.Select(Key).Distinct().Count();
                    Assert.True(repeats == memo.Counts.FindMemoHits,
                        $"{name}: {repeats} repeats, {memo.Counts.FindMemoHits} memo hits");
                    Assert.Equal(memo.Reads.Length, memo.Counts.Find);
                }
                served += optimized.Counts.FindMemoHits;
                compared++;
            }
        output.WriteLine($"{compared} ticks compared, {skipped} with the look-ahead skipped, {served} reads served.");
        Assert.True(skipped > 0 && served > 0, $"{skipped} skipped, {served} served");
    }

    static TickResult Tick(SupportCostExecution execution, SupportCostRow row, bool reference, bool sample)
    {
        var reads = new List<SupportFindObservation>();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        IDisposable? uncached = reference ? SupportReferenceEvaluation.Begin() : null;
        IDisposable? forced = sample ? GroundSignals.AlwaysSample() : null;
        try
        {
            SupportQueryDiagnostics.SetFindObserver(reads.Add);
            object result;
            if (row.Explicit)
            {
                (MovementAvailability status, IDisposable? acquired) = execution.Producer.Acquire();
                Assert.Equal(MovementAvailability.Known, status);
                using var lease = (MovementQueryLease)acquired!;
                var framed = new FramedMovementState(row.Input, lease.Frame, new(Room, null, lease.Identity));
                result = ExplicitCharacterMovement.StepTowards(framed, row.Direction, false, row.Dt,
                    SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
            }
            else result = execution.Invoke().State!.Value;
            return new(result, SupportQueryDiagnostics.Snapshot(), [.. reads]);
        }
        finally
        {
            forced?.Dispose();
            uncached?.Dispose();
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    // Look-ahead reaches StepHeight + 2 k CapsuleRadius for k = 1, 2, 3, beyond any other read's reach.
    static bool IsLookAhead(float reachUp)
    {
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        for (int k = 1; k <= GroundSignals.Samples; k++)
            if (reachUp == tuning.StepHeight + 2 * (k * tuning.CapsuleRadius)) return true;
        return false;
    }

    static SupportFindObservation Key(SupportFindObservation read) => read with { Status = 0, Height = 0, HeightError = 0 };

    static void AssertSameReads(SupportFindObservation[] expected, SupportFindObservation[] actual, string path)
    {
        Assert.True(expected.Length == actual.Length, $"{path}: {expected.Length} reads against {actual.Length}");
        for (int i = 0; i < expected.Length; i++) AssertSameBits(expected[i], actual[i], $"{path} read {i}");
    }

    static void AssertSameBits(object expected, object actual, string path)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float f)
        {
            Assert.True(BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)actual),
                $"{path}: expected {f:R}, actual {actual}");
            return;
        }
        if (expected is double d)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)actual),
                $"{path}: expected {d:R}, actual {actual}");
            return;
        }
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
        {
            Assert.True(expected.Equals(actual), $"{path}: {expected} against {actual}");
            return;
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            if (left is null) Assert.Null(right);
            else AssertSameBits(left, right!, path + "." + field.Name);
        }
    }
}
