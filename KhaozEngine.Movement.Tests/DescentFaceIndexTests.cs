using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class DescentFaceIndexTests
{
    [Fact]
    public void InclusiveCandidatesRetainEdgesCornersVerticalAndZeroAreaFacesInOriginalOrder()
    {
        var faces = new[] {
            Face(0, [new(1,0,0),new(1,1,0),new(1,1,1)]),
            Face(1, [new(1,0,1),new(2,0,1),new(1,0,2)]),
            Face(2, [new(0.5,0,0.5),new(0.5,1,0.5),new(0.5,2,0.5)]),
            Face(3, [new(0,0,0),new(1,0,0),new(0,0,1)]),
        }.Concat(Enumerable.Range(4, 20).Select(i => Face(i, [new(10 + i, 0, 10), new(11 + i, 0, 10), new(10 + i, 0, 11)]))).ToArray();
        var index = new DescentFaceIndex(faces);
        Assert.Equal(new[] { 0, 1, 2, 3 }, index.Query(0, 0, 1, 1).ToArray());
        Assert.Equal(new[] { 0, 1, 3 }, index.Query(1, 0, 1, 1).ToArray());
        Assert.Empty(index.Query(-2, -2, -1, -1).ToArray());
    }
    [Fact]
    public void AggregateIndexStorageRefusesBeforeAllocationAtItsExactLimit()
    {
        var faces = new[] { Face(0, [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)]) };
        var complete = new DescentCaptureIndexBudget(88);
        _ = new DescentFaceIndex(faces, complete);
        Assert.Equal(88, complete.UsedBytes);
        var refusal = Assert.Throws<PhysicsNavDescentEvidenceException>(() => new DescentFaceIndex(faces, new(87)));
        Assert.Equal(PhysicsNavDescentEvidenceReason.IndexStorageExceeded, refusal.Reason);
        Assert.Equal(88, refusal.RequestedIndexBytes);
        Assert.Equal(87, refusal.MaximumIndexBytes);
    }
    static DescentFace Face(int id, PhysicsEvidencePoint[] points) => new(new("source", id, 0), new("world", "owner"),
        false, PhysicsEvidenceKind.Triangle, points, default, 0, 0);
}
