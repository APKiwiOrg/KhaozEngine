using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 6)]
    public void WaterBoundaryReusesIntervalBuffersAndClearsOnlyInitializedContacts(bool wet, int cleared)
    {
        // #1728. Multiple exact water certificates in one interval reuse bounded private storage.
        using var scene = new QueryScene(_ => 0);
        var body = new MovementBodyQuery(new(0, .751f, 0), .25f, .75f, Room, null);
        if (wet)
            scene.Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new(0, 1, 0, 1, false);
                contacts[0] = new(new("world", "lake"), Room, new(-2, 2, 2, true, "bed", "surface"),
                    Vector2.Zero, Vector3.UnitY, 0, "edge", 0,
                    MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
                return new(MovementAvailability.Known, 1, 1, 1, 1, 0, scene.Lease.Identity);
            };
        Span<Vector3> normals = stackalloc Vector3[4];
        scene.Lease.BeginContactTick();
        MovementWaterBufferWork.Reset();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(MovementAvailability.Known, MovementWaterBoundary.Find(body, Vector3.UnitX,
                    .75f, .5f, WaterTraversalMode.DryOnly, scene.Lease, normals, out int count, out var hit));
                Assert.Equal(wet ? 1 : 0, count);
                Assert.Equal(wet, hit.Blocked);
            }
            Assert.Equal(0, MovementWaterBufferWork.Rents);
            Assert.Equal(cleared, MovementWaterBufferWork.ClearedReferences);
        }
        finally
        {
            MovementWaterBufferWork.Enabled = false;
            scene.Lease.EndContactTick();
        }
    }
}
