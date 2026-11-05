using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactDropPrecisionTests
{
    [Fact]
    public void RealShoreSeatingTraceNamesPrecisionRefusalWithoutPublishingCoverage()
    {
        // #1697. Candidate 7357's run-mode seating sweep reaches a grazing shore-water boundary.
        var doc = Shore();
        var field = MovementWaterFieldTests.Field(doc);
        var body = new MovementBodyQuery(new(99.11088f, 0.4276524f, -296.68784f), 0.3f, 0.75f,
            MovementWaterFieldTests.Space, null);
        var spans = new MovementCoverageSpan[256];
        var contacts = new MovementDomainContact[256];
        var control = field.TraceWater(new(body, Vector3.Zero), Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, control.Availability);
        System.Array.Clear(spans); System.Array.Clear(contacts);
        var result = field.TraceWater(new(body, new(0, -0.42954257f, 0)), Vector3.Zero,
            MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.PrecisionExceeded, result.Availability);
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(0, result.ContactsWritten);
        Assert.All(spans, item => Assert.Equal(default, item));
        Assert.All(contacts, item => Assert.Equal(default, item));
    }

    [Theory]
    [InlineData(MovementStepOutcome.PrecisionRefused, RangeMoveStatus.Blocked)]
    [InlineData(MovementStepOutcome.EnvironmentUnresolved, RangeMoveStatus.Suspended)]
    [InlineData(MovementStepOutcome.EnvironmentInvalid, RangeMoveStatus.Suspended)]
    [InlineData(MovementStepOutcome.FrameMismatch, RangeMoveStatus.Suspended)]
    public void SharedDropStatusSeparatesGeometryFromUnavailableEnvironment(MovementStepOutcome outcome, RangeMoveStatus expected)
    {
        // #1697. Both sharp and terrain runtime consumers use this policy, including supported alignment.
        var result = new MovementStepResult(default, outcome);
        Assert.Equal(expected, ContactDropFollower.Status(result, RangeMoveStatus.Following));
    }

    [Theory]
    [InlineData(MovementAvailability.PrecisionExceeded, MovementStepOutcome.PrecisionRefused)]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Stale, MovementStepOutcome.EnvironmentUnresolved)]
    public void ContactQueryFailureKeepsPrecisionDistinctFromUnavailableEvidence(MovementAvailability availability, MovementStepOutcome outcome)
    {
        // #1697. The controller uses this typed failure, not an exception-message filter.
        Assert.Equal(outcome, new ContactQueryFailure(availability).Outcome);
    }

    [Fact]
    public void TerrainPolicyNamesThePrecisionRefusal()
    {
        // #1697. A resolved geometric refusal must not masquerade as generic movement failure.
        using var fixture = new ContactTerrainPolicyFixture();
        var policy = new ContactTerrainDropPolicy(fixture.Certificate(), fixture.Scene.Context.NavigationFrame,
            ContactTerrainScene.Tuning, 6);
        Assert.False(policy.AcceptTick(new(default, MovementStepOutcome.PrecisionRefused), default, false, false));
        Assert.Equal(ContactTerrainDropRefusal.PrecisionExceeded, policy.Refusal);
    }

    internal static TileWorldDocument Shore()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(1, 4));
        for (int z = 295; z <= 298; z++)
            for (int x = 97; x <= 101; x++)
            {
                short height = z <= 296 ? (short)-130 : z == 297 ? (short)(x <= 99 ? -35 : -130) : (short)(x <= 98 ? 0 : -35);
                doc.SetCornerHeightCm(x, z, 0, height);
                if (z <= 296 || z == 297 && x >= 99) doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        return doc;
    }
}
