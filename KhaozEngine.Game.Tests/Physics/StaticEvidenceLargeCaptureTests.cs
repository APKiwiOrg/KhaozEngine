using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class StaticEvidenceLargeCaptureTests
{
    // #1697. Complete enumeration is bounded by emitted features, not the unrelated local-sweep budget.
    [Fact]
    public void CompleteCaptureRetainsEveryOverlappingStaticAndTheLastNonstandableFace()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        const int boxes = 4096, required = boxes * 6 + 1;
        var handles = new List<StaticHandle>();
        for (int i = 0; i < boxes; i++) handles.Add(world.AddStatic(new BoxShape(new(0.25f)), Pose.Identity));
        var last = world.AddStatic(new TriangleMeshShape([new(0, -3, -0.5f), new(0, 3, -0.5f), new(0, 3, 0.5f)], [0, 1, 2]), Pose.Identity);
        handles.Add(last);
        using var lease = world.AcquireQueryReadLease();
        var faces = new List<(int Static, int Leaf, int Feature)>();
        bool lastNonstandable = false;
        var complete = world.CaptureStaticEvidence(lease, new(-1, -5, -1), new(1, 5, 1), required, Visit);
        Assert.Equal(PhysicsEvidenceStatus.Complete, complete);
        Assert.Equal(required, faces.Count);
        Assert.Equal(required, faces.Distinct().Count());
        Assert.Equal(handles.Select(h => h.Value).Order().ToArray(), faces.Select(f => f.Static).Distinct().ToArray());
        Assert.Equal(faces.Order().ToArray(), faces.ToArray());
        Assert.True(lastNonstandable);

        faces.Clear();
        var shortBudget = world.CaptureStaticEvidence(lease, new(-1, -5, -1), new(1, 5, 1), required - 1, Visit);
        Assert.Equal(PhysicsEvidenceStatus.CapacityExceeded, shortBudget);
        Assert.Equal(required - 1, faces.Count);
        // The observed callback prefix remains provisional because the result did not certify Complete.

        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points)
        {
            lease.AssertCurrent();
            faces.Add((face.Static.Value, face.Leaf, face.Feature));
            if (face.Static == last)
            {
                Assert.Equal(PhysicsEvidenceKind.Triangle, face.Kind);
                Assert.Equal(3, points.Length);
                lastNonstandable = points[0].X == points[1].X && points[1].X == points[2].X;
            }
            return true;
        }
    }
}
