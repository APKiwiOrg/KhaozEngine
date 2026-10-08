using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementSpanOverlapTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementBodyQuery Body = new(new(0, 0.75f, 0), 0.25f, 0.75f, Room, null);
    static MovementDomainContact Contact(float at, MovementContactOverlap point, MovementContactOverlap span) =>
        new(new("world", "lake"), Room, new(-1, 1, 1, true, "bed", "surface"),
            new(0.251f, 0), Vector3.UnitX, at, "edge", 1, point, span);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeSpanTangencyCannotContradictItsPoint(bool zeroLength)
    {
        AssertTrace(zeroLength ? [new(0, 0, 0, 1, true), new(0, 1, 1, 0, true)] : [new(0, 1, 0, 1, true)],
            [Contact(0, MovementContactOverlap.Overlapping, MovementContactOverlap.Tangent)], MovementAvailability.Invalid);
    }

    [Fact]
    public void AZeroLengthSpanCannotContradictThePointInEitherDirection() =>
        AssertTrace([new(0, 0, 0, 1, true), new(0, 1, 1, 0, true)],
            [Contact(0, MovementContactOverlap.Tangent, MovementContactOverlap.Overlapping)], MovementAvailability.Invalid);

    [Fact]
    public void OneContactCannotBeOwnedByTwoSpans() =>
        AssertTrace([new(0, 0.5f, 0, 1, true), new(0.5f, 1, 0, 1, true)],
            [Contact(0.5f, MovementContactOverlap.Tangent, MovementContactOverlap.Tangent)], MovementAvailability.Invalid);

    [Fact]
    public void ThePointMustBeInsideItsOwningSpan() =>
        AssertTrace([new(0, 0.5f, 0, 0, true), new(0.5f, 1, 0, 1, true)],
            [Contact(0, MovementContactOverlap.Tangent, MovementContactOverlap.Tangent)], MovementAvailability.Invalid);

    [Fact]
    public void AnUnownedContactIsNotAUsableCoverageFact() =>
        AssertTrace([new(0, 1, 0, 0, true)],
            [Contact(0, MovementContactOverlap.Tangent, MovementContactOverlap.Tangent)], MovementAvailability.Invalid);

    [Fact]
    public void APointMayBeTangentWhileTheRestOfItsSpanOverlaps() =>
        AssertTrace([new(0, 1, 0, 1, true)],
            [Contact(0, MovementContactOverlap.Tangent, MovementContactOverlap.Overlapping)], MovementAvailability.Known);

    [Fact]
    public void ACertifiedTangentSpanRemainsKnown() =>
        AssertTrace([new(0, 1, 0, 1, true)],
            [Contact(0, MovementContactOverlap.Tangent, MovementContactOverlap.Tangent)], MovementAvailability.Known);

    static void AssertTrace(MovementCoverageSpan[] sourceSpans, MovementDomainContact[] sourceContacts, MovementAvailability expected)
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var view = physics.CreateQueryViewExcludingStatics([]);
        var source = new EnvironmentAcquisitionFixture(view);
        source.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        {
            sourceSpans.CopyTo(spans);
            sourceContacts.CopyTo(contacts);
            return new(MovementAvailability.Known, sourceSpans.Length, sourceSpans.Length,
                sourceContacts.Length, sourceContacts.Length, 0.00001f, source.Identity);
        };
        using var lease = Assert.IsType<MovementQueryLease>(source.Acquire().Lease);
        var sentinel = Contact(0, MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
        MovementCoverageSpan[] outputSpans = [new(0, 1, 0, 0, true), new(0, 1, 0, 0, true)];
        MovementDomainContact[] outputContacts = [sentinel, sentinel];
        var result = lease.TraceWater(new(Body, Vector3.Zero), outputSpans, outputContacts);
        Assert.Equal(expected, result.Availability);
        if (expected == MovementAvailability.Known) return;
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(0, result.ContactsWritten);
        Assert.All(outputContacts, value => Assert.Equal(sentinel, value));
        Assert.All(outputSpans, value => Assert.Equal(new MovementCoverageSpan(0, 1, 0, 0, true), value));
    }
}
