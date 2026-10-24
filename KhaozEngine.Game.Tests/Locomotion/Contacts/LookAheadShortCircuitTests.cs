using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SC-13. A tick whose ascent is already decided skips its look-ahead Finds, and the skip must decide
/// exactly what the sampled path decides.</summary>
public sealed class LookAheadShortCircuitTests(ITestOutputHelper output)
{
    static readonly MovementSpaceKey Room = new("world", "room");

    /// <summary>One tick. <paramref name="Result"/> is the whole explicit step result, or the dry state.</summary>
    readonly record struct TickResult(object Result, MoveState State, MovementStepOutcome? Outcome, long Finds,
        int LookAheadReads, long AnalyticSamples, long Bindings);

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
                        AssertSameBits(sampled.Result, skipping.Result, name);
                        Assert.True(skipping.Finds <= sampled.Finds, name);
                        Assert.Equal(sampled.Finds - skipping.Finds, sampled.LookAheadReads - skipping.LookAheadReads);
                        compared++;
                        if (row.Name.StartsWith("stairs ", StringComparison.Ordinal) || row.Name == "hidden crate")
                        {
                            // Every stairs and crate tick that reaches its signals travels on walkable ground with a
                            // trigger, so its sampled path takes all three samples. A refused explicit tick never
                            // reaches them.
                            bool reached = sampled.Outcome is null or MovementStepOutcome.Advanced;
                            Assert.True(GroundSignals.Samples * (reached ? 1 : 0) == sampled.LookAheadReads,
                                $"{name}: {sampled.Outcome}, {sampled.LookAheadReads} sampled reads");
                            if (!reached) output.WriteLine($"{name}: refused {sampled.Outcome} before its signals.");
                        }
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

    // Each disjunct of a decided ascent alone skips the samples, on the stairs where the window would see a rise. The
    // seeded average and the ascent the wire cannot show are covered too. With none of them the samples are taken.
    [Theory]
    [InlineData("rise seen", 1f, 0.1f)]
    [InlineData("lift over lag", 1f, 0.1f)]
    [InlineData("owed climb", 1f, 0.1f)]
    [InlineData("rise seen", 0f, 0.1f)]
    [InlineData("lift over lag", 0f, 0.0001f)]
    [InlineData("owed climb", 0f, 0.0001f)]
    [InlineData("none", 1f, 0.1f)]
    public void EachAscentDisjunctSkipsAloneAndMatchesTheSampledPath(string disjunct, float ewma, float moveRise)
    {
        SupportCostRow row = Row("stairs box");
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        using SupportFootprintScene scene = SupportGateCostScenes.Build(row);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, lease, tuning);
        var axis = new Vector2(row.Feet.X, row.Feet.Z);
        FootSupportQuery query = row.Query;
        SupportSample owed = FootSupport.Find(null, null, scene.World, lease, query);
        // Feet on the support owe nothing. The row's own feet sit a riser below it.
        float feetY = disjunct == "owed climb" ? row.Feet.Y : owed.Height;
        SupportSample support = FootSupport.Find(null, null, scene.World, lease, query with { FeetY = feetY });
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(disjunct == "owed climb", support.Height - feetY > 0.01f);
        bool riseSeen = disjunct == "rise seen";
        float carriedLag = disjunct == "lift over lag" ? 0.1f : 0f;
        float stepRise = riseSeen ? moveRise : 0f;
        var input = new MoveState { ClimbRate = 1, ClimbRateEwma = ewma };
        var feet = new Vector3(axis.X, feetY, axis.Y);

        (int Reads, (float, float, float) Signals) Resolve(bool sample)
        {
            int reads = 0;
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
            try
            {
                SupportQueryDiagnostics.SetFindObserver(find =>
                {
                    if (IsLookAhead(find.ReachUp)) reads++;
                });
                IDisposable? forced = sample ? GroundSignals.AlwaysSample() : null;
                try
                {
                    var signals = GroundSignals.Resolve(input, stepRise, moveRise, riseSeen, false, carriedLag,
                        0.2f * Vector2.UnitX, feet, support, true, row.Dt, tuning, query.FootRadius, query.CosMaxSlope,
                        queries);
                    return (reads, signals);
                }
                finally { forced?.Dispose(); }
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
        }

        var skipping = Resolve(sample: false);
        var sampled = Resolve(sample: true);
        output.WriteLine($"{disjunct}, EWMA {ewma}, rise {moveRise}: {sampled.Signals}, reads {skipping.Reads} of {sampled.Reads}.");
        Assert.Equal(GroundSignals.Samples, sampled.Reads);
        Assert.Equal(disjunct == "none" ? GroundSignals.Samples : 0, skipping.Reads);
        AssertSameBits(sampled.Signals, skipping.Signals, disjunct);
        // A decided ascent reports its rate, except a seeded rise too small for the wire, which ends the run.
        if (disjunct != "none") Assert.Equal(moveRise > 0.01f, sampled.Signals.Item1 > 0);
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

    [Fact]
    public void SamplingScopesNest()
    {
        SupportCostRow stairs = Row("stairs box");
        using var execution = new SupportCostExecution(stairs, "DryTick");
        using (GroundSignals.AlwaysSample())
        {
            using (GroundSignals.AlwaysSample()) { }
            Assert.Equal(GroundSignals.Samples, Tick(execution, stairs, stairs.Input, stairs.Direction, false).LookAheadReads);
        }
        Assert.Equal(0, Tick(execution, stairs, stairs.Input, stairs.Direction, false).LookAheadReads);
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
        IDisposable? forced = sample ? GroundSignals.AlwaysSample() : null;
        try
        {
            SupportQueryDiagnostics.SetFindObserver(find =>
            {
                if (IsLookAhead(find.ReachUp)) reads++;
            });
            object result;
            MoveState state;
            MovementStepOutcome? outcome = null;
            if (row.Explicit)
            {
                (MovementAvailability status, IDisposable? acquired) = execution.Producer.Acquire();
                Assert.Equal(MovementAvailability.Known, status);
                using var lease = (MovementQueryLease)acquired!;
                var framed = new FramedMovementState(input, lease.Frame, new(Room, null, lease.Identity));
                MovementStepResult step = ExplicitCharacterMovement.StepTowards(framed, direction, false,
                    row.Dt, SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
                (result, state, outcome) = (step, step.State.State, step.Outcome);
            }
            else
            {
                state = ContactController.Step(input, direction, direction == Vector2.Zero ? 0 : 1, false, false,
                    row.Dt, (x, _) => row.HeightAt(x), SupportGateCostScenes.Tuning, (x, _) => row.NormalAt(x),
                    execution.View, null, null);
                result = state;
            }
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            return new(result, state, outcome, counts.Find, reads, counts.AnalyticSamples, counts.Bindings);
        }
        finally
        {
            forced?.Dispose();
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
