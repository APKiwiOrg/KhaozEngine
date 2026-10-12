using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationRefusalTests
{
    static MoveTuning Contact => MoveTuning.Default with { Controller = MovementControllerKind.Contact };
    static NavBakeSources Sources() => new NavBakeSources().AddHashOf("fixture", new byte[] { 1 });
    static ExplicitMovementProfile Profile(MoveTuning tuning, WaterTraversalPolicy water) =>
        new("fixture", tuning, water, Sources());
    static PhysicsNavBake Capture(BepuPhysicsWorld world) => PhysicsNavBake.Capture(
        new GroundMoveContext((_, _) => 0, physics: world),
        new PhysicsNavBakeOptions(0, 0, 1, 1, 1, 2, 4, MoveTuning.Default.MaxSlopeRadians, 4, 4), _ => 0);

    [Fact]
    public void ExplicitPolicyOwnsWaterAndSelectableFootParticipatesInFingerprint()
    {
        WaterTraversalPolicy water = new(WaterTraversalMode.WadeOnly);
        string defaultFingerprint = Profile(MoveTuning.Default, water).Fingerprint;
        Assert.Equal(defaultFingerprint, Profile(Contact, water).Fingerprint);
        Assert.Equal(defaultFingerprint, Profile(Contact with { Water = water }, water).Fingerprint);
        Assert.NotEqual(defaultFingerprint, Profile(Contact with { FootRadiusFraction = 0.75f }, water).Fingerprint);
        Assert.Throws<ArgumentException>(() => Profile(Contact with { Water = new(WaterTraversalMode.DryOnly) }, water));
        Assert.Throws<ArgumentException>(() => Profile(Contact with { Water = new(WaterTraversalMode.Legacy, 1) }, water));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContactBuildProfileRefusesBeforeOtherProfileChecks(bool optionsOverload)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        MoveTuning tuning = Contact with { MaxSlopeRadians = 0 };
        var failure = Assert.Throws<NotSupportedException>(() => optionsOverload
            ? capture.BuildProfile(tuning, default, GroundProfileOptions.Default)
            : capture.BuildProfile(tuning, default));
        Assert.Contains("phase 5b", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContactInMixedBakeRefusesBeforeIdentityEncoding(bool contactFirst)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        NavBakeProfile legacy = new("legacy", MoveTuning.Default, default);
        NavBakeProfile contact = new("contact", Contact, default);
        // Empty sources would fail identity encoding if the selector refusal ran too late.
        var failure = Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Create(capture,
            new NavBakeSources(), contactFirst ? [contact, legacy] : [legacy, contact]));
        Assert.Contains("phase 5b", failure.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UndefinedSelectorsRefuseBeforeNavigationEncoding(int selector)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        MoveTuning tuning = MoveTuning.Default with { Controller = (MovementControllerKind)selector };
        Assert.Throws<ArgumentOutOfRangeException>(() => capture.BuildProfile(tuning, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundNavigationBake.Create(capture,
            Sources(), [new("invalid", tuning, default)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Profile(tuning, new(WaterTraversalMode.WadeOnly)));
    }

    [Fact]
    public void NullBakeProfilePreservesTheIdentityArgumentRefusal()
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Create(capture,
            Sources(), [new("legacy", MoveTuning.Default, default), null!]));
    }
}
