using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SC-13. A dry tick whose ascent is already decided skips its look-ahead Finds, and the skip must decide
/// exactly what the sampled path decides.</summary>
public sealed class LookAheadShortCircuitTests(ITestOutputHelper output)
{
    static readonly MovementSpaceKey Room = new("world", "room");

    readonly record struct TickResult(MoveState State, MovementStepOutcome? Outcome, int Finds,
        int LookAheadReads, int AnalyticSamples, int Bindings);

    // Every sparse cost scene, through the dry and the explicit door, with the row's own input and with an ascending,
    // a descending and a flat carried run, walked forward and back.
    [Fact]
    public void LookAheadShortCircuitMatchesSampledPath()
    {
        int compared = 0, skipped = 0, explicitSkipped = 0;
        var seen = new HashSet<string>();
        foreach (SupportCostRow scene in SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Where(r => !r.Dense))
            foreach (bool explicitDoor in new[] { false, true })
            {
                SupportCostRow row = scene with { Explicit = explicitDoor };
                using var execution = new SupportCostExecution(row, explicitDoor ? "ExplicitTick" : "DryTick");
                foreach ((string run, MoveState input) in Inputs(row))
                    foreach (Vector2 direction in new[] { row.Direction, -row.Direction }.Distinct())
                    {
                        string door = explicitDoor ? "explicit" : "dry";
                        string name = $"{row.Name} {door} {run} {direction}";
                        TickResult skipping = Tick(execution, row, input, direction, sample: false);
                        TickResult sampled = Tick(execution, row, input, direction, sample: true);
                        Assert.Equal(sampled.Outcome, skipping.Outcome);
                        AssertSameBits(sampled.State, skipping.State, name);
                        Assert.True(skipping.Finds <= sampled.Finds, name);
                        Assert.Equal(sampled.Finds - skipping.Finds, sampled.LookAheadReads - skipping.LookAheadReads);
                        compared++;
                        if (skipping.Finds < sampled.Finds)
                        {
                            Assert.Equal(GroundSignals.Samples, sampled.LookAheadReads);
                            Assert.Equal(0, skipping.LookAheadReads);
                            Assert.True(GroundSignals.Ascending(input.ClimbRate), name);
                            skipped++;
                            if (explicitDoor)
                            {
                                // The explicit door also skips each sample's producer ground sample and binding.
                                Assert.Equal(GroundSignals.Samples, sampled.AnalyticSamples - skipping.AnalyticSamples);
                                Assert.InRange(sampled.Bindings - skipping.Bindings, 1, GroundSignals.Samples);
                                output.WriteLine($"{name}: {sampled.Bindings - skipping.Bindings} bindings skipped.");
                                explicitSkipped++;
                            }
                            seen.Add($"{row.Name} {door} {run}");
                        }
                    }
            }
        output.WriteLine($"{compared} ticks compared, {skipped} skipped, {explicitSkipped} explicit. " +
            string.Join(", ", seen.Order()));
        Assert.True(compared > 300, $"{compared} ticks compared");
        Assert.True(skipped > 0 && explicitSkipped > 0, $"{skipped} skipped, {explicitSkipped} explicit");
        // The stairs row carries in an ascent its owed climb continues, and both doors skip on it.
        Assert.Contains("stairs box dry own", seen);
        Assert.Contains("stairs box explicit own", seen);
    }

    [Fact]
    public void LookAheadSkipsSamplesWhenAscentIsDecided()
    {
        SupportCostRow stairs = Row("stairs box");
        using (var execution = new SupportCostExecution(stairs, "DryTick"))
        {
            TickResult skipping = Tick(execution, stairs, stairs.Input, stairs.Direction, sample: false);
            TickResult sampled = Tick(execution, stairs, stairs.Input, stairs.Direction, sample: true);
            // The carried run ascends and the feet still owe climb, so ascent is decided before any sample.
            Assert.True(GroundSignals.Ascending(stairs.Input.ClimbRate));
            Assert.Equal(GroundSignals.Samples, sampled.LookAheadReads);
            Assert.Equal(0, skipping.LookAheadReads);
            Assert.Equal(sampled.Finds - GroundSignals.Samples, skipping.Finds);
            Assert.True(skipping.State.ClimbRate > stairs.Input.ClimbRate);
        }

        // The pushed seat carries no run in, so only its samples can start the ascent it reports. It keeps all three.
        SupportCostRow seat = Row("pushed seat");
        using (var execution = new SupportCostExecution(seat, "DryTick"))
        {
            TickResult result = Tick(execution, seat, seat.Input, seat.Direction, sample: false);
            Assert.False(GroundSignals.Ascending(seat.Input.ClimbRate));
            Assert.Equal(GroundSignals.Samples, result.LookAheadReads);
            Assert.True(GroundSignals.Ascending(result.State.ClimbRate));
        }
    }

    static SupportCostRow Row(string name) =>
        SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);

    static IEnumerable<(string Run, MoveState Input)> Inputs(SupportCostRow row)
    {
        MoveState own = row.Input;
        yield return ("own", own);
        yield return ("ascending", own with { ClimbRate = 1, ClimbRateEwma = 1 });
        yield return ("descending", own with { ClimbRate = -1, ClimbRateEwma = 0 });
        yield return ("flat", own with { ClimbRate = 0, ClimbRateEwma = 0 });
    }

    static TickResult Tick(SupportCostExecution execution, SupportCostRow row, MoveState input, Vector2 direction,
        bool sample)
    {
        int reads = 0;
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportQueryDiagnostics.SetFindObserver(find =>
            {
                if (IsLookAhead(find.ReachUp)) reads++;
            });
            MoveState state;
            MovementStepOutcome? outcome = null;
            using (sample ? GroundSignals.AlwaysSample() : default)
            {
                if (row.Explicit)
                {
                    (MovementAvailability status, IDisposable? acquired) = execution.Producer.Acquire();
                    Assert.Equal(MovementAvailability.Known, status);
                    using var lease = (MovementQueryLease)acquired!;
                    var framed = new FramedMovementState(input, lease.Frame, new(Room, null, lease.Identity));
                    MovementStepResult result = ExplicitCharacterMovement.StepTowards(framed, direction, false,
                        row.Dt, SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
                    (state, outcome) = (result.State.State, result.Outcome);
                }
                else
                    state = ContactController.Step(input, direction, direction == Vector2.Zero ? 0 : 1, false, false,
                        row.Dt, (x, _) => row.HeightAt(x), SupportGateCostScenes.Tuning, (x, _) => row.NormalAt(x),
                        execution.View, null, null);
            }
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            return new(state, outcome, counts.Find, reads, counts.AnalyticSamples, counts.Bindings);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // Look-ahead reaches StepHeight + 2 k CapsuleRadius for k = 1, 2, 3, beyond any other read's reach.
    static bool IsLookAhead(float reachUp)
    {
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        for (int k = 1; k <= GroundSignals.Samples; k++)
            if (reachUp == tuning.StepHeight + 2 * (k * tuning.CapsuleRadius)) return true;
        return false;
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
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum) { Assert.True(expected.Equals(actual), $"{path}: {expected} against {actual}"); return; }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            if (left is null) Assert.Null(right);
            else AssertSameBits(left, right!, path + "." + field.Name);
        }
    }
}
