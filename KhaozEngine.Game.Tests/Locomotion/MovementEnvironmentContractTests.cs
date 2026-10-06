using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentContractTests
{
    [Theory]
    [InlineData("MovementDomainKey", "", "water")]
    [InlineData("MovementDomainKey", "world", " ")]
    [InlineData("MovementSpaceKey", "", "cave")]
    [InlineData("MovementSpaceKey", "world", " ")]
    [InlineData("MovementSupportKey", "", "floor")]
    [InlineData("MovementSupportKey", "world", " ")]
    public void StableKeysRejectMissingIdentity(string type, string world, string local)
    {
        Assert.ThrowsAny<ArgumentException>(() => Key(type, world, local));
    }

    [Theory]
    [InlineData("MovementDomainKey")]
    [InlineData("MovementSpaceKey")]
    [InlineData("MovementSupportKey")]
    public void StableKeysCompareOrdinallyWithoutLosingWorldIdentity(string type)
    {
        object first = Key(type, "world", "Cave");
        Assert.Equal(first, Key(type, "world", "Cave"));
        Assert.NotEqual(first, Key(type, "world", "cave"));
        Assert.NotEqual(first, Key(type, "other-world", "Cave"));
    }

    [Fact]
    public void DefaultAvailabilityAndDefaultWaterPointStayUnresolved()
    {
        Assert.Equal(0, (int)MovementAvailability.Unresolved);
        MovementWaterPoint point = default;
        Assert.Equal(MovementAvailability.Unresolved, point.Availability);
    }

    [Theory]
    [InlineData(float.NaN, 2f, 2f, true)]
    [InlineData(-2f, float.PositiveInfinity, 2f, true)]
    [InlineData(2f, -2f, 2f, true)]
    [InlineData(2f, 2f, 2f, true)]
    [InlineData(-2f, 2f, 1f, false)]
    [InlineData(-2f, 2f, 4f, true)]
    public void WaterIntervalsRejectNonfiniteEmptyOrContradictoryContainment(
        float lower, float upper, float nominalSurface, bool freeSurface)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementWaterInterval(lower, upper, nominalSurface, freeSurface, "bed", "top"));
    }

    [Fact]
    public void FloodedCeilingAndNominalSurfaceRemainDistinctFacts()
    {
        MovementWaterInterval interval = new MovementWaterInterval(-20f, -10f, 0f, false, "cave-floor", "cave-ceiling");
        Assert.Equal(-10f, interval.UpperY);
        Assert.Equal(0f, interval.NominalSurfaceY);
        Assert.False(interval.UpperIsFreeSurface);
    }

    [Theory]
    [InlineData(0f, 0.75f)]
    [InlineData(-0.3f, 0.75f)]
    [InlineData(0.3f, 0.2f)]
    [InlineData(float.NaN, 0.75f)]
    [InlineData(0.3f, float.PositiveInfinity)]
    public void BodyQueryRejectsInvalidCapsuleExtents(float radius, float halfHeight)
    {
        var space = new MovementSpaceKey("world", "surface");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementBodyQuery(Vector3.Zero, radius, halfHeight, space, null));
    }

    [Fact]
    public void BodyQueryCannotTreatDefaultSpaceAsKnownMembership()
    {
        MovementSpaceKey space = default;
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementBodyQuery(Vector3.Zero, 0.3f, 0.75f, space, null));
    }

    [Fact]
    public void BodyQueryRejectsNonfinitePosition()
    {
        var space = new MovementSpaceKey("world", "surface");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementBodyQuery(new Vector3(float.NaN, 0f, 0f), 0.3f, 0.75f, space, null));
    }

    [Fact]
    public void KnownWetPointRequiresItsSelectedDomainAndContainingInterval()
    {
        const MovementAvailability known = MovementAvailability.Known;
        var space = new MovementSpaceKey("world", "surface");
        var domain = new MovementDomainKey("world", "river");
        MovementWaterInterval interval = new MovementWaterInterval(-4f, 0f, 0f, true, "bed", "surface");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementWaterPoint(known, space, null, true, 1f, interval));
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementWaterPoint(known, space, domain, true, 1f, null));
    }

    [Fact]
    public void KnownDryPointCannotCarryASelectedWaterDomain()
    {
        const MovementAvailability known = MovementAvailability.Known;
        var space = new MovementSpaceKey("world", "bridge");
        var domain = new MovementDomainKey("world", "river");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementWaterPoint(known, space, domain, false, 1f, null));
    }

    [Fact]
    public void KnownWetPointCannotMixWorldIdentities()
    {
        const MovementAvailability known = MovementAvailability.Known;
        var space = new MovementSpaceKey("world", "surface");
        var domain = new MovementDomainKey("other-world", "river");
        MovementWaterInterval interval = new MovementWaterInterval(-4f, 0f, 0f, true, "bed", "surface");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementWaterPoint(known, space, domain, true, 1f, interval));
    }

    static object Key(string type, string world, string local) => type switch
    {
        "MovementDomainKey" => new MovementDomainKey(world, local),
        "MovementSpaceKey" => new MovementSpaceKey(world, local),
        "MovementSupportKey" => new MovementSupportKey(world, local),
        _ => throw new ArgumentException("Unknown fixture key type.", nameof(type))
    };
}
