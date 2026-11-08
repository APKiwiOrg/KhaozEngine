using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class GroundNavigationValidateTests
{
    static readonly Lazy<GroundNavigation> ContactFresh = new(() => FlatGraph(true, false));
    static readonly Lazy<GroundNavigation> ContactLoaded = new(() => FlatGraph(true, true));
    static readonly Lazy<GroundNavigation> LegacyFresh = new(() => FlatGraph(false, false));
    static readonly Lazy<GroundNavigation> LegacyLoaded = new(() => FlatGraph(false, true));

    static GroundNavigation Graph(bool contact, bool loaded) => (contact
        ? loaded ? ContactLoaded : ContactFresh
        : loaded ? LegacyLoaded : LegacyFresh).Value;

    static GroundNavigation FlatGraph(bool contact, bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        MoveTuning tuning = contact ? Tuning : Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var profile = new NavBakeProfile("player", tuning, default);
        var bake = Bake(context, FlatOptions, profile);
        return (loaded ? Load(Write(bake), new(FlatOptions, Sources(), [profile])) : bake).GetProfile("player");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void KindMismatchBothWays(bool contactGraph, bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        MoveTuning legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        GroundNavigation graph = Graph(contactGraph, loaded);
        using var binding = contactGraph ? EffectiveMovementBinding.Legacy(legacy) : context.CreateNavigationBinding(Tuning);
        var bound = Assert.Throws<NavigationProfileException>(() => Validate(graph, binding));
        Assert.Equal("binding", bound.ParamName);
        Assert.Equal(NavigationProfileRefusal.BindingKindMismatch, bound.Reason);
        MoveTuning wrong = contactGraph ? legacy : Tuning;
        var failure = Assert.Throws<NavigationProfileException>(() => Validate(graph, wrong));
        Assert.Equal("tuning", failure.ParamName);
        Assert.Equal(NavigationProfileRefusal.BindingKindMismatch, failure.Reason);
        Assert.Equal(contactGraph ? EffectiveMovementBindingKind.OrdinaryContact : EffectiveMovementBindingKind.Legacy, failure.ExpectedKind);
        Assert.Equal(binding.Kind, failure.ActualKind);
        Assert.IsAssignableFrom<ArgumentException>(failure);
        Validate(graph, contactGraph ? Tuning : legacy);
    }

    public static IEnumerable<object[]> ContactFields => NavBakeIdentity.TuningFieldNames
        .Concat(new[] { "FootRadiusFraction", "Water.Mode", "Water.SurfaceJumpSpeed" })
        .SelectMany(name => new[] { new object[] { name, false }, new object[] { name, true } });

    [Theory]
    [MemberData(nameof(ContactFields))]
    public void EachContactIdentityFieldMismatchIsRefused(string field, bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        GroundNavigation graph = Graph(true, loaded);
        Validate(graph, Tuning);
        MoveTuning wrong = Changed(Tuning, field);
        using var binding = context.CreateNavigationBinding(wrong);
        var bound = Assert.Throws<NavigationProfileException>(() => Validate(graph, binding));
        Assert.Equal("binding", bound.ParamName);
        Assert.Equal(NavigationProfileRefusal.TuningFieldMismatch, bound.Reason);
        Assert.Equal(field, bound.FieldName);
        var failure = Assert.Throws<NavigationProfileException>(() => Validate(graph, wrong));
        Assert.Equal("tuning", failure.ParamName);
        Assert.Equal(NavigationProfileRefusal.TuningFieldMismatch, failure.Reason);
        Assert.Equal(field, failure.FieldName);
        Assert.Equal(FieldBits(Tuning, field), failure.ExpectedBits);
        Assert.Equal(FieldBits(wrong, field), failure.ActualBits);
        Validate(graph, Tuning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicExplicitBindingIsRefusedEvenWhenLegacyTuningMatches(bool contactGraph)
    {
        using var scene = new OrdinaryContactScene(Tuning);
        using var context = Context(scene);
        MoveTuning legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var profile = new NavBakeProfile("player", contactGraph ? Tuning : legacy, default);
        var bake = Bake(context, FlatOptions, profile);
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        var provider = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning, scene.Height, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20));
        var environment = new MovementEnvironmentContext(view, provider, provider.Identity);
        using var binding = EffectiveMovementBinding.PublicExplicit(legacy, environment, new(WaterTraversalMode.DryOnly));
        foreach (GroundNavigation graph in new[] { bake.GetProfile("player"), Load(Write(bake), new(FlatOptions, Sources(), [profile])).GetProfile("player") })
        {
            var failure = Assert.Throws<NavigationProfileCapabilityException>(() => Validate(graph, binding));
            Assert.Equal(NavigationProfileRefusal.PublicExplicitProfileUnsupported, failure.Reason);
            Assert.Equal(new Uri("https://github.com/APKiwiOrg/KhaozEngine/issues/1389"), failure.IssueUrl);
            Assert.Equal(EffectiveMovementBindingKind.PublicExplicit, failure.ActualKind);
            graph.Validate(profile.Tuning);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbeOverridesAreOutsideContactIdentity(bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        GroundNavigation graph = Graph(true, loaded);
        MoveTuning runtime = Tuning with { WalkSpeed = 5, RunSpeed = 7, AirMomentum = true };
        using var binding = context.CreateNavigationBinding(runtime);
        Validate(graph, binding);
        Validate(graph, runtime);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainedJumpSpeedSignedZeroIsCompatible(bool reverse, bool loaded)
    {
        float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        MoveTuning baked = Tuning with { JumpSpeed = reverse ? negativeZero : 0f };
        MoveTuning runtime = Tuning with { JumpSpeed = reverse ? 0f : negativeZero };
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        var profile = new NavBakeProfile("player", baked, default);
        var bake = Bake(context, FlatOptions, profile);
        var graph = (loaded ? Load(Write(bake), new(FlatOptions, Sources(), [profile])) : bake).GetProfile("player");
        using var binding = context.CreateNavigationBinding(runtime);
        graph.Validate(binding);
        graph.Validate(runtime);
    }

    static uint FieldBits(MoveTuning tuning, string field)
    {
        if (field == "Water.Mode") return (uint)tuning.Water.Mode;
        if (field == "Water.SurfaceJumpSpeed") return BitConverter.SingleToUInt32Bits(tuning.Water.SurfaceJumpSpeed);
        object value = typeof(MoveTuning).GetProperty(field)!.GetValue(tuning)!;
        return value is bool flag ? flag ? 1u : 0u : BitConverter.SingleToUInt32Bits((float)value);
    }

    static MoveTuning Changed(MoveTuning tuning, string field)
    {
        if (field == "Water.Mode") return tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        if (field == "Water.SurfaceJumpSpeed") return tuning with { Water = new(tuning.Water.Mode, 1) };
        if (field == "FootRadiusFraction") return tuning with { FootRadiusFraction = 0.6f };
        PropertyInfo property = typeof(MoveTuning).GetProperty(field)!;
        object value = property.GetValue(tuning)!;
        object changed = value is bool flag ? !flag : float.IsPositiveInfinity((float)value)
            ? float.MaxValue : (float)value == 1f ? MathF.BitDecrement(1f) : MathF.BitIncrement((float)value);
        object boxed = tuning;
        property.SetValue(boxed, changed);
        return (MoveTuning)boxed;
    }

    internal static void Validate(GroundNavigation navigation, EffectiveMovementBinding binding)
        => navigation.Validate(binding);

    internal static void Validate(GroundNavigation navigation, MoveTuning tuning)
        => navigation.Validate(tuning);
}
