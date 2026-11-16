using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class MovingShoreProjectionOracleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bank-along")]
    [InlineData("shore-traverse")]
    public void AdvancingAndReturningHistoriesKeepEveryTraceBit(string name)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.Set("nearest"));
        using var scene = new WaterPrismScenario(name);
        var frozen = new FrozenWaterReference(scene.Colliders.Medium.CaptureTraceForTests(), rebuildGeometry: true);
        var queries = new List<MovementMediumSweepQuery>();
        int known = 0, refusals = 0, contacts = 0, shared = 0;
        scene.Field.Recorded = (query, actual, spans, touches) =>
        {
            // Read before the comparisons below trace again. A memo hit repeats an earlier trace's count.
            shared += TileWaterPrismCoverage.CaptureSharedProjectionsForTests().Copies;
            queries.Add(query);
            var expectedSpans = new MovementCoverageSpan[spans.Length];
            var expectedContacts = new MovementDomainContact[touches.Length];
            var expected = frozen.Trace(query, Vector3.Zero, actual.Identity, expectedSpans, expectedContacts);
            WaterCoverageComparison.Equal(expected, actual, expectedSpans, spans, expectedContacts, touches);
            var full = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query);
            if (full.Availability == MovementAvailability.Known) known++;
            contacts += full.ContactsWritten;
        };
        var state = scene.Basis;
        bool returning = false, swam = false, exited = false;
        int ticks = name == "bank-along" ? 64 : 400;
        for (int tick = 0; tick < ticks; tick++)
        {
            if (name == "bank-along" ? tick == 32 : state.Move.Position.Z < -244) returning = true;
            var axis = name == "bank-along" ? returning ? -Vector2.UnitX : Vector2.UnitX
                : returning ? -Vector2.UnitY : Vector2.UnitY;
            state = scene.Step(state, new MoveCommand(axis, false, 0));
            swam |= state.Move.Swimming;
            exited |= returning && !state.Move.Swimming && state.Move.Grounded;
            if (name == "shore-traverse" && returning && state.Move.Position.Z >= -235.8f) break;
        }
        Assert.True(returning);
        Assert.True(name == "bank-along" ? state.Move.Position.X <= scene.Basis.Move.Position.X + 0.1f : exited);
        if (name == "shore-traverse")
        {
            Assert.True(swam);
            Assert.True(state.Move.Position.Z >= -235.8f);
        }
        Assert.True(known > 0);
        Assert.True(contacts > 0);
        // The advancing history must reach the shared path, unless the corpus run forces the original one.
        Assert.Equal(!TileWaterSharedProjections.BypassForTests, shared > 0);
        Assert.True(queries.Distinct().Count() > 32);
        var history = queries.Distinct().Reverse().ToArray();
        foreach (string mode in new[] { "nearest", "up", "down", "zero", "flush-output", "flush-input" })
        {
            bool supported = environment.Set(mode);
            output.WriteLine($"PROJECTION_FP {mode}: supported={supported}");
            if (!supported) continue;
            int modeRefusals = 0;
            foreach (var query in history)
            {
                _ = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query);
                if (FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query, spanCapacity: 0).Availability ==
                    MovementAvailability.CapacityExceeded) modeRefusals++;
                if (FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query, contactCapacity: 0).Availability ==
                    MovementAvailability.CapacityExceeded) modeRefusals++;
            }
            Assert.True(modeRefusals > 0);
            refusals += modeRefusals;
        }
        Assert.True(refusals > 0);
        output.WriteLine($"MOVING_PROJECTION {name}: traces={queries.Count}, distinct={queries.Distinct().Count()}, known={known}, contacts={contacts}, shared={shared}, refusals={refusals}, swam={swam}, exited={exited}");
    }
}
