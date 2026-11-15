using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class ContactQueryCostOracleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dry-stand")]
    [InlineData("dry-walk")]
    [InlineData("dry-colliders")]
    [InlineData("flat-water")]
    [InlineData("shore-stand")]
    [InlineData("shore-walk")]
    public void FixedAndAdvancingAuthorityQueriesMatchIndependentWaterOracles(string name)
    {
        // #1756. A repeated basis hides fresh moving queries and an empty collider fixture hides support work.
        using var scene = ContactQueryCostCases.Create(name);
        var frozen = new FrozenWaterReference(scene.Colliders.Medium.CaptureTraceForTests());
        var queries = new List<(MovementMediumSweepQuery Query, MovementCoverageResult Result,
            MovementCoverageSpan[] Spans, MovementDomainContact[] Contacts)>();
        var recorded = new HashSet<MovementMediumSweepQuery>();
        scene.Field.Recorded = (q, r, s, c) => queries.Add((q, r, s, c));
        var command = ContactQueryCostCases.Command(name);
        var state = scene.Basis;
        int total = 0, large = 0;
        long selected = 0;
        foreach (bool advancing in new[] { false, true })
        {
            state = scene.Basis;
            int ticks = advancing ? 32 : 2;
            for (int tick = 0; tick < ticks; tick++)
            {
                queries.Clear();
                using (var capture = SupportQueryCapture.Begin(name))
                {
                    var next = scene.Step(state, command);
                    selected += capture.Snapshot().Total[SupportQueryCounter.SelectedStatics];
                    if (advancing) state = next;
                }
                Assert.NotEmpty(queries);
                foreach (var observation in queries)
                {
                    var query = observation.Query;
                    recorded.Add(query);
                    var spans = new MovementCoverageSpan[64];
                    var contacts = new MovementDomainContact[256];
                    var authority = frozen.Trace(query, Vector3.Zero, observation.Result.Identity, spans, contacts);
                    WaterCoverageComparison.Equal(authority, observation.Result, spans[..authority.SpansWritten],
                        observation.Spans, contacts[..authority.ContactsWritten], observation.Contacts);
                    var result = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query);
                    Assert.Equal(MovementAvailability.Known, result.Availability);
                    if (result.SpansWritten > TileWaterTraceMemo.SpanLimit || result.ContactsWritten > TileWaterTraceMemo.ContactLimit)
                        large++;
                    // Refusals keep the exact capacities, current identity and completely untouched caller buffers.
                    _ = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query, spanCapacity: 0);
                    if (result.ContactsWritten > 0)
                        _ = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query,
                            contactCapacity: result.ContactsWritten - 1);
                    if (total == 0) CheckValidationAndCapacities(scene.Colliders.Medium, frozen, query, result);
                    total++;
                }
            }
        }
        Assert.True(total > 0);
        if (name.EndsWith("stand", StringComparison.Ordinal)) Assert.Equal(scene.Basis.Position, state.Position);
        else
        {
            Assert.NotEqual(scene.Basis.Position, state.Position);
            Assert.True(recorded.Count > 32);
        }
        if (name == "dry-colliders") Assert.True(selected > 0);
        if (name == "shore-walk") Assert.True(large > 0);
        output.WriteLine($"QUERY_COST_ORACLE case={name}, traces={total}, distinct={recorded.Count}, large={large}, selected-statics={selected}");
    }

    [Fact]
    public void DiagnosticRosterRetainsAllSixIndependentlyNamedCases() =>
        Assert.Equal(new[] { "dry-stand", "dry-walk", "dry-colliders", "flat-water", "shore-stand", "shore-walk" },
            ContactQueryCostCases.Names);

    static void CheckValidationAndCapacities(TileMediumSampler field, FrozenWaterReference frozen,
        MovementMediumSweepQuery query, MovementCoverageResult known)
    {
        _ = FrozenWaterComparison.Check(field, frozen, query,
            spanCapacity: known.SpansWritten, contactCapacity: known.ContactsWritten);
        _ = FrozenWaterComparison.Check(field, frozen, query, spanCapacity: 65, contactCapacity: 257);
        var space = new MovementSpaceKey(new(query.Body.CurrentSpace.WorldId.ToCharArray()),
            new(query.Body.CurrentSpace.LocalId.ToCharArray()));
        var rebound = new MovementMediumSweepQuery(new(query.Body.Centre, query.Body.Radius, query.Body.HalfHeight,
            space, query.Body.CurrentSupport), query.Delta);
        _ = FrozenWaterComparison.Check(field, frozen, rebound, identity: new MovementQueryIdentity(
            new("query-cost-closure".ToCharArray()), 2, new("query-cost-scope".ToCharArray())));
        var invalidIdentity = FrozenWaterComparison.Check(field, frozen, query, identity: default(MovementQueryIdentity));
        Assert.Equal(MovementAvailability.Invalid, invalidIdentity.Availability);
        var invalidOrigin = FrozenWaterComparison.Check(field, frozen, query, new(float.NaN, 0, 0));
        Assert.Equal(MovementAvailability.Invalid, invalidOrigin.Availability);
    }
}
