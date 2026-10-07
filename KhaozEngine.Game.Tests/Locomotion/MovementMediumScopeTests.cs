using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementMediumScopeTests
{
    [Theory]
    [InlineData(0, 1, 0f)]
    [InlineData(0, -1, 0f)]
    [InlineData(1, 1, 0f)]
    [InlineData(1, -1, 0f)]
    [InlineData(2, 1, 0f)]
    [InlineData(2, -1, 0f)]
    [InlineData(0, 1, 0.3f)]
    [InlineData(0, -1, 0.3f)]
    [InlineData(1, 1, 0.3f)]
    [InlineData(1, -1, 0.3f)]
    [InlineData(2, 1, 0.3f)]
    [InlineData(2, -1, 0.3f)]
    public void MediumScopeMustContainTheUnroundedSkinnedPath(int axis, int sign, float distance)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var room = new MovementSpaceKey("world", "room");
        Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var body = new MovementBodyQuery(direction * sign, 0.3f, 0.3f, room, null);
        Vector3 delta = direction * (sign * distance);
        float roundedEdge = (1f + distance) + (0.3f + 0.001f);
        double exactEdge = 1d + distance + (double)0.3f + 0.001f;
        Assert.True(exactEdge > roundedEdge, "Fixture must expose an inward-rounded scope edge.");
        Assert.True(exactEdge < MathF.BitIncrement(roundedEdge));
        MovementQueryIdentity identity = new("closure", 1, "scope");
        MovementFrameDescriptor frame = new(WorldFrame.Origin, Vector3.Zero, 1);
        var sentinel = new MovementCoverageSpan(0, 1, 0, 0, true);

        foreach (bool enough in new[] { false, true })
        {
            float limit = enough ? MathF.BitIncrement(roundedEdge) : roundedEdge;
            Vector3 min = new(-4), max = new(4);
            if (sign > 0) max[axis] = limit;
            else min[axis] = -limit;
            var scope = new MovementQueryScope(min, max, 0, 0, room, identity, frame);
            var environment = new EnvironmentAcquisitionFixture(view, scope);
            environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> _) =>
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
            };
            var acquired = environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            MovementCoverageSpan[] destination = [sentinel];
            var result = lease.TraceWater(new(body, delta), destination, Span<MovementDomainContact>.Empty);
            Assert.Equal(enough ? MovementAvailability.Known : MovementAvailability.Unresolved, result.Availability);
            Assert.Equal(enough ? 1 : 0, environment.CoverageCalls);
            if (!enough)
            {
                Assert.Equal(0, result.SpansWritten);
                Assert.Equal(sentinel, destination[0]);
            }
        }
    }
}
