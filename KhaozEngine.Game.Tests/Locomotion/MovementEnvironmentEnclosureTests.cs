using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentEnclosureTests
{
    [Theory]
    [InlineData(0.25f, 0.4f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(0.251f, 0.401f)]
    [InlineData(0.251f, 0.751f)]
    [InlineData(0.252f, 0.4f)]
    [InlineData(0.252f, 0.752f)]
    public void BoundedEnclosingCapsulesRetainTheAnalyticWaterEntry(float radius, float halfHeight)
    {
        var bounds = new AnalyticBox(new(-8), new(8));
        using var scene = new AnalyticMovementEnvironment([new Room("room", bounds)],
            [new Water("lake", "room", new(new(1, -8, -8), new(8, 4, 8)), 4)]);
        using var lease = scene.Acquire("room");
        var body = new MovementBodyQuery(Vector3.Zero, radius, halfHeight, Space("room"), null);
        MovementCoverageSpan[] spans = new MovementCoverageSpan[64];
        MovementDomainContact[] contacts = new MovementDomainContact[256];
        var result = lease.TraceWater(new(body, new(2, 0, 0)), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.True(result.ContactsWritten > 0);
        // The vertical wall is x=1. The inflated radial boundary reaches it first.
        double expected = (1d - radius - (double)0.001f) / 2d;
        Assert.InRange(Math.Abs(contacts[0].Fraction - expected) * 2d, 0, 0.00001d);
        Assert.Equal(-Vector3.UnitX, contacts[0].Normal);
        Assert.Equal(0, spans[0].ContactCount);
        Assert.True(spans[0].HasDryCoverage);
    }
}
