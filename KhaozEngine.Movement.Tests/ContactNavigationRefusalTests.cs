using System;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
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
    static MoveTuning ValidContact => OrdinaryContactScene.DefaultTuning;

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
    public void ContactGeometryValidationPrecedesCapabilityRefusal(bool optionsOverload)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        MoveTuning tuning = ValidContact with { StepHeight = 2f };
        var failure = Assert.Throws<ArgumentException>(() => optionsOverload
            ? capture.BuildProfile(tuning, default, GroundProfileOptions.Default)
            : capture.BuildProfile(tuning, default));
        Assert.Contains("shell span", failure.Message);
        NavBakeProfile profile = new("contact", tuning, default) { Aquatic = true };
        Assert.Contains("shell span", Assert.Throws<ArgumentException>(() =>
            GroundNavigationBake.Create(capture, Sources(), [profile])).Message);
        using var stream = new MemoryStream(new byte[64]);
        Assert.Contains("shell span", Assert.Throws<ArgumentException>(() =>
            GroundNavigationBake.Load(stream, new(capture.Options, Sources(), [profile]))).Message);
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContactAquaticRefusesAtEveryDoor(bool contactFirst)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        NavBakeProfile legacy = new("legacy", MoveTuning.Default, default);
        MoveTuning tuning = ValidContact with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        NavBakeProfile contact = new("contact", tuning, default) { Aquatic = true };
        NavBakeProfile[] profiles = contactFirst ? [contact, legacy] : [legacy, contact];
        Assert.Equal("Contact aquatic navigation is not supported yet. See https://github.com/APKiwiOrg/KhaozEngine/issues/1389.",
            NavBakeProfile.DeferredAquaticMessage);
        Assert.Equal(NavBakeProfile.DeferredAquaticMessage,
            Assert.Throws<NotSupportedException>(() => capture.BuildProfile(tuning, default,
                new GroundProfileOptions { Aquatic = true })).Message);
        // Empty sources would fail identity encoding if the selector refusal ran too late.
        Assert.Equal(NavBakeProfile.DeferredAquaticMessage,
            Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Create(capture,
                new NavBakeSources(), profiles)).Message);
        using var stream = new MemoryStream(new byte[64]);
        Assert.Equal(NavBakeProfile.DeferredAquaticMessage,
            Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Load(stream,
                new(capture.Options, new NavBakeSources(), profiles))).Message);
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedContactSelectionThrowsArgumentBeforeStreamRead(bool aquatic)
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        MoveTuning tuning = ValidContact with { Water = default };
        NavBakeProfile profile = new("contact", tuning, default) { Aquatic = aquatic };
        var options = new GroundProfileOptions { Aquatic = aquatic };
        Assert.Throws<ArgumentException>(() => capture.BuildProfile(tuning, default, options));
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Create(capture, Sources(), [profile]));
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Load(stream,
            new(capture.Options, Sources(), [profile])));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void LegacyConstructedContextRefusesContactProfile()
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        var failure = Assert.Throws<NotSupportedException>(() => capture.BuildProfile(ValidContact, default));
        Assert.Contains("GroundMoveContext constructed with its ordinary environment", failure.Message);
    }

    [Fact]
    public void ProfilesResolveTheirEffectiveBindingKind()
    {
        Assert.Equal(EffectiveMovementBindingKind.Legacy,
            new NavBakeProfile("legacy", MoveTuning.Default, default).ResolveKind());
        Assert.Equal(EffectiveMovementBindingKind.OrdinaryContact,
            new NavBakeProfile("contact", ValidContact, default).ResolveKind());
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
