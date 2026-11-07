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
        var conflict = Assert.Throws<MovementConfigurationException>(() =>
            Profile(Contact with { Water = new(WaterTraversalMode.DryOnly) }, water));
        Assert.Equal(MovementConfigurationRefusal.WaterPoliciesConflict, conflict.Reason);
        Assert.Equal(water, conflict.ExpectedWater);
        Assert.Equal(new(WaterTraversalMode.DryOnly), conflict.ActualWater);
        Assert.Equal("tuning", conflict.ParamName);
        Assert.Equal(MovementConfigurationRefusal.WaterPoliciesConflict,
            Assert.Throws<MovementConfigurationException>(() => Profile(Contact with { Water = new(WaterTraversalMode.Legacy, 1) }, water)).Reason);
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
        Assert.Equal("tuning", failure.ParamName);
        NavBakeProfile profile = new("contact", tuning, default) { Aquatic = true };
        Assert.Equal("tuning", Assert.Throws<ArgumentException>(() =>
            GroundNavigationBake.Create(capture, Sources(), [profile])).ParamName);
        using var stream = new MemoryStream(new byte[64]);
        Assert.Equal("tuning", Assert.Throws<ArgumentException>(() =>
            GroundNavigationBake.Load(stream, new(capture.Options, Sources(), [profile]))).ParamName);
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
        var build = Assert.Throws<NavigationProfileCapabilityException>(() => capture.BuildProfile(tuning, default,
            new GroundProfileOptions { Aquatic = true }));
        Assert.Equal(NavigationProfileRefusal.ContactAquaticUnsupported, build.Reason);
        Assert.Equal(new Uri("https://github.com/APKiwiOrg/KhaozEngine/issues/1389"), build.IssueUrl);
        Assert.Null(build.ProfileName);
        // Empty sources would fail identity encoding if the selector refusal ran too late.
        var create = Assert.Throws<NavigationProfileCapabilityException>(() => GroundNavigationBake.Create(capture,
            new NavBakeSources(), profiles));
        Assert.Equal(NavigationProfileRefusal.ContactAquaticUnsupported, create.Reason);
        Assert.Equal(new Uri("https://github.com/APKiwiOrg/KhaozEngine/issues/1389"), create.IssueUrl);
        Assert.Equal("contact", create.ProfileName);
        using var stream = new MemoryStream(new byte[64]);
        var load = Assert.Throws<NavigationProfileCapabilityException>(() => GroundNavigationBake.Load(stream,
            new(capture.Options, new NavBakeSources(), profiles)));
        Assert.Equal(NavigationProfileRefusal.ContactAquaticUnsupported, load.Reason);
        Assert.Equal(new Uri("https://github.com/APKiwiOrg/KhaozEngine/issues/1389"), load.IssueUrl);
        Assert.Equal("contact", load.ProfileName);
        Assert.IsAssignableFrom<NotSupportedException>(load);
        Assert.Equal(0, stream.Position);
        Assert.Equal(EffectiveMovementBindingKind.OrdinaryContact, (contact with { Aquatic = false }).ResolveKind());
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
        Assert.Equal(MovementConfigurationRefusal.ExplicitWaterModeRequired,
            Assert.Throws<MovementConfigurationException>(() => capture.BuildProfile(tuning, default, options)).Reason);
        Assert.Equal(MovementConfigurationRefusal.ExplicitWaterModeRequired,
            Assert.Throws<MovementConfigurationException>(() => GroundNavigationBake.Create(capture, Sources(), [profile])).Reason);
        using var stream = new MemoryStream(new byte[64]);
        Assert.Equal(MovementConfigurationRefusal.ExplicitWaterModeRequired,
            Assert.Throws<MovementConfigurationException>(() => GroundNavigationBake.Load(stream,
                new(capture.Options, Sources(), [profile]))).Reason);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void LegacyConstructedContextRefusesContactProfile()
    {
        using var world = new BepuPhysicsWorld();
        using PhysicsNavBake capture = Capture(world);
        Assert.Throws<NotSupportedException>(() => capture.BuildProfile(ValidContact, default));
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
