using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class MovementOverlapProducerTests
{
    [Theory]
    [InlineData(0.251f, MovementContactOverlap.Tangent)]
    [InlineData(0.25f, MovementContactOverlap.Overlapping)]
    [InlineData(0.2f, MovementContactOverlap.Overlapping)]
    public void BoxCoverageDistinguishesSkinOnlyContactFromPositiveOrUncertainOverlap(float x, MovementContactOverlap expected)
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", new(new(-8), new(8)))],
            [new Water("lake", "room", new(new(-8, -4, -8), new(0, 1, 8)), 1)]);
        using var lease = scene.Acquire("room");
        var body = new MovementBodyQuery(new(x, 0.75f, 0), 0.25f, 0.75f, Space("room"), null);
        MovementCoverageSpan[] spans = new MovementCoverageSpan[64];
        MovementDomainContact[] contacts = new MovementDomainContact[256];
        var result = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.True(result.ContactsWritten > 0);
        foreach (var contact in contacts.AsSpan(0, result.ContactsWritten)) Assert.Equal(expected, contact.Overlap);
    }

    [Theory]
    [InlineData(-0.751f, MovementContactOverlap.Tangent)]
    [InlineData(-0.75f, MovementContactOverlap.Overlapping)]
    [InlineData(-0.7f, MovementContactOverlap.Overlapping)]
    public void PlaneCoverageUsesTheUninflatedBodyAndTreatsTheErrorTieAsOverlap(float y, MovementContactOverlap expected)
    {
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling: false, slope: 0);
        var body = new MovementBodyQuery(new(0, y, 0), 0.25f, 0.75f, AnalyticSlopedMovementEnvironment.Room, null);
        MovementCoverageSpan[] spans = new MovementCoverageSpan[64];
        MovementDomainContact[] contacts = new MovementDomainContact[256];
        var result = scene.Lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.True(result.ContactsWritten > 0);
        foreach (var contact in contacts.AsSpan(0, result.ContactsWritten)) Assert.Equal(expected, contact.Overlap);
    }
}
