using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class ContactSupportProducerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Q1CapturedRegistrationHonoursTheSelectedViewsBankExclusion(bool excluded)
    {
        using var environment = Environment();
        StaticHandle floor = AddBox(environment, "floor", "room", new(0, -0.125f, 0), new(8, 0.125f, 8));
        StaticHandle bank = AddBox(environment, "bank", "room", new(3, 0.125f, 0), new(2, 0.125f, 4));
        using var lease = environment.Acquire("room", 0.4f, 0, excludedStatics: excluded ? [bank] : []);
        Assert.Equal(MovementAvailability.Known, lease.BindContactSupport(Request(), Sample(floor, 0), out var floorBinding));
        Assert.Equal(new MovementSupportKey("world", "floor"), floorBinding.Owner);
        Assert.True(lease.UsesPhysicsView(environment.View));
        Assert.Equal(excluded ? MovementAvailability.Unresolved : MovementAvailability.Known,
            lease.BindContactSupport(Request(0.9f), Sample(bank, 0.25f), out var bankBinding));
        if (excluded) Assert.Equal(default, bankBinding);
        else Assert.Equal(new MovementSupportKey("world", "bank"), bankBinding.Owner);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("feature")]
    [InlineData("ambiguous")]
    public void Q4RegistrationCannotInventAnOwnerForMissingAmbiguousOrUnknownFeatures(string fault)
    {
        using var environment = Environment();
        StaticHandle floor = AddBox(environment, "floor", "room", new(0, -0.125f, 0), new(8, 0.125f, 8));
        StaticHandle bank = environment.Physics.AddStatic(new BoxShape(new(2, 0.125f, 4)), Pose.At(new(3, 0.125f, 0)));
        if (fault != "missing") environment.RegisterBoxSupport(bank, "bank", "room");
        if (fault == "ambiguous") environment.RegisterBoxSupport(bank, "other-bank", "room");
        using var lease = environment.Acquire("room", 0.4f, 0);
        Assert.Equal(MovementAvailability.Known, lease.BindContactSupport(Request(), Sample(floor, 0), out _));
        var sample = Sample(bank, 0.25f) with { FeatureId = fault == "feature" ? 1000 : 3 };
        Assert.Equal(fault == "ambiguous" ? MovementAvailability.Invalid : MovementAvailability.Unresolved,
            lease.BindContactSupport(Request(0.9f), sample, out var result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Q4CrossRoomSupportRequiresADeclaredLinkFromTheActualOrigin(bool linked)
    {
        using var environment = new AnalyticMovementEnvironment(
            [new("a", new(new(-16), new(0, 16, 16))), new("b", new(new(0, -16, -16), new(16)))], [],
            linked ? [new("a", "b")] : []);
        StaticHandle floor = AddBox(environment, "floor", "a", new(-4, -0.125f, 0), new(4, 0.125f, 4));
        StaticHandle bank = AddBox(environment, "bank", "b", new(3, 0.125f, 0), new(2, 0.125f, 4));
        using var lease = environment.Acquire("a", 0.4f, 0);
        Assert.Equal(MovementAvailability.Known,
            lease.BindContactSupport(Request(-1, "a"), Sample(floor, 0), out _));
        Assert.Equal(linked ? MovementAvailability.Known : MovementAvailability.Unresolved,
            lease.BindContactSupport(Request(0.9f, "a", new(-1, 0, 0)), Sample(bank, 0.25f), out var result));
        if (linked)
        {
            Assert.Equal(Space("b"), result.Space);
            Assert.Equal("a/b", result.TraversedLinkId);
        }
        else Assert.Equal(default, result);
        Assert.Equal(MovementAvailability.Unresolved,
            lease.BindContactSupport(Request(0.9f, "a", new(1, 0, 0)), Sample(bank, 0.25f), out result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void AnalyticFieldsAtOneXZRemainSeparateAcrossStackedRooms()
    {
        using var environment = new AnalyticMovementEnvironment(
            [new("lower", new(new(-16, -4, -16), new(16, 2, 16))),
                new("upper", new(new(-16, 4, -16), new(16, 12, 16)))], []);
        environment.RegisterAnalyticGround("lower", "lower-floor", 0);
        environment.RegisterAnalyticGround("upper", "upper-floor", 5);
        using var lease = environment.Acquire("lower", 0.4f, 0);
        Assert.Equal(MovementAvailability.Known, lease.SampleAnalyticGround(Request(room: "lower"), out var lower));
        Assert.Equal(0, lower!.Value.Height);
        Assert.Equal("lower-floor", lower.Value.Owner.LocalId);
        Assert.Equal(MovementAvailability.Known, lease.SampleAnalyticGround(Request(room: "upper", feetY: 5), out var upper));
        Assert.Equal(5, upper!.Value.Height);
        Assert.Equal("upper-floor", upper.Value.Owner.LocalId);
        Assert.Equal(Space("upper"), upper.Value.Space);
    }

    [Fact]
    public void CapturedProducerCertifiesAnalyticAbsenceWithoutUsingItsSolidFloor()
    {
        using var environment = Environment();
        StaticHandle floor = AddBox(environment, "floor", "room", new(0, -0.125f, 0), new(8, 0.125f, 8));
        using var lease = environment.Acquire("room", 0.4f, 0);
        Assert.Equal(MovementAvailability.Known, lease.BindContactSupport(Request(), Sample(floor, 0), out _));
        Assert.Equal(MovementAvailability.Known, lease.SampleAnalyticGround(Request(), out var ground));
        Assert.Null(ground);
    }

    static AnalyticMovementEnvironment Environment() => new([new("room", new(new(-16), new(16)))], []);
    static StaticHandle AddBox(AnalyticMovementEnvironment environment, string owner, string room, Vector3 centre, Vector3 half)
    {
        StaticHandle handle = environment.Physics.AddStatic(new BoxShape(half), Pose.At(centre));
        environment.RegisterBoxSupport(handle, owner, room);
        return handle;
    }
    static SupportSample Sample(StaticHandle handle, float height) =>
        new(SupportStatus.Walkable, height, 0, Vector3.UnitY, handle, 3, new(1, height, 0));
    static MovementSupportRequest Request(float x = 0, string room = "room", Vector3? start = null, float feetY = 0)
    {
        var body = new MovementBodyQuery(new(x, feetY + 0.75f, 0), 0.25f, 0.75f, Space(room), null);
        return new(body, 0.4f, 0, 0.8f, new(Space(room), start ?? body.Feet));
    }
}
