using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.37f)]
    public void BatchedSourceFanKeepsEveryReleasedRawPairVerdict(float yaw)
    {
        var pose = new RigidPose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
        var polygons = new SupportPolygon[96];
        for (int i = 0; i < polygons.Length; i++)
        {
            float a = 2 * MathF.PI * i / 96, b = 2 * MathF.PI * (i + 1) / 96;
            polygons[i] = IncidencePolygon([Vector3.Zero, new(MathF.Cos(a), 0, MathF.Sin(a)),
                new(MathF.Cos(b), 0, MathF.Sin(b))], pose, i + 1);
        }
        var incidence = new SupportSourceIncidence();
        incidence.Build(polygons);
        var kernel = new SupportSegmentPolygon();
        int pairs = 0;
        for (int first = 0; first < polygons.Length; first++)
            for (int second = first + 1; second < polygons.Length; second++)
            {
                Assert.True(incidence.Offers(first, second));
                Assert.True(incidence.ProvesMeeting(first, second));
                Assert.Equal(ReleasedJoin(polygons[first], polygons[second], 0.0001f, kernel),
                    SupportNeighborhoodJoins.Joined(polygons[first], polygons[second], 0.0001f,
                        kernel, incidence, first, second));
                pairs++;
            }
        Assert.Equal(4560, pairs);
        incidence.Build([]);
        Assert.False(incidence.Offers(0, 1));
        incidence.Build(polygons);
        Assert.True(incidence.ProvesMeeting(0, 95));
    }

    [Fact]
    public void SourceIncidenceCannotHideAnEarlierReleasedMeetingRefusal()
    {
        Vector3[] Triangle() => [Vector3.Zero, new(1e-20f, 0, 0), new(0, 0, 1e-20f)];
        var first = IncidencePolygon(Triangle(), new(Vector3.Zero, Quaternion.Identity), 1);
        var second = IncidencePolygon(Triangle(), new(Vector3.Zero, Quaternion.Identity), 2);
        var broadNormal = new FeaturePoint(FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-35, 1e-35)),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(0.5e-40, 2e-40)),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-35, 1e-35)));
        first.Finish(broadNormal);
        second.Finish(broadNormal);
        Assert.True(first.IsResolved);
        Assert.Equal(GeometrySign.Unresolved, FeaturePoint.Dot(first.Normal, first.Normal).Sign);
        var incidence = new SupportSourceIncidence();
        incidence.Build([first, second]);
        Assert.True(incidence.Offers(0, 1));
        Assert.False(incidence.ProvesMeeting(0, 1));
        var kernel = new SupportSegmentPolygon();
        Assert.Equal(GeometrySign.Unresolved, ReleasedJoin(first, second, 0.0001f, kernel));
        Assert.Equal(GeometrySign.Unresolved,
            SupportNeighborhoodJoins.Joined(first, second, 0.0001f, kernel, incidence, 0, 1));
    }

    [Fact]
    public void BatchedSourceIncidenceKeepsCracksRemoteMeetingsAndCreaseVerdicts()
    {
        RigidPose identity = new(Vector3.Zero, Quaternion.Identity);
        Vector3[] floor = [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)];
        Vector3[][] shapes =
        [
            floor,
            [new(1, 0, -1), new(2, 0, -1), new(2, 0, 1), new(1, 0, 1)],
            [new(0, 0, 3), new(-1, 0, -1), new(-1, 0, 1)],
            [new(0, 0, 3), new(1, 0, 1), new(1, 0, -1)],
            [Vector3.Zero, new(1, 0.5f, 0), new(0, 0, 1)],
            [Vector3.Zero, new(1, -0.5f, 0), new(0, 0, 1)],
            [Vector3.Zero, new(-1, 0, 1), new(-1, 0.25f, -1)],
            [Vector3.Zero, new(1, 0, 1), new(1, -0.25f, -1)],
            floor,
        ];
        var polygons = shapes.Select((s, i) => IncidencePolygon((Vector3[])s.Clone(), identity, i + 1)).ToList();
        foreach (float crack in new[] { MathF.BitDecrement(0.0001f), MathF.BitIncrement(0.0001f) })
            polygons.Add(IncidencePolygon([new(1, 0, -1), new(2, 0, -1), new(2, 0, 1), new(1, 0, 1)],
                new RigidPose(new(crack, 0, 0), Quaternion.Identity), polygons.Count + 1));
        polygons.Add(IncidencePolygon([new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)],
            new RigidPose(Vector3.UnitX, Quaternion.Identity), polygons.Count + 1));
        var incidence = new SupportSourceIncidence();
        var kernel = new SupportSegmentPolygon();
        foreach (SupportPolygon[] ordered in new[] { polygons.ToArray(), polygons.AsEnumerable().Reverse().ToArray(), polygons.ToArray() })
        {
            incidence.Build(ordered);
            for (int first = 0; first < ordered.Length; first++)
                for (int second = first + 1; second < ordered.Length; second++)
                    foreach (double band in new[] { (double)0.0001f, MathF.BitDecrement(0.0001f), MathF.BitIncrement(0.0001f) })
                        Assert.Equal(ReleasedJoin(ordered[first], ordered[second], band, kernel),
                            SupportNeighborhoodJoins.Joined(ordered[first], ordered[second], band, kernel,
                                incidence, first, second));
        }
        incidence.Build(polygons.ToArray());
        Assert.True(incidence.Offers(2, 3));
        Assert.True(incidence.ProvesMeeting(2, 3));
        Assert.Equal(GeometrySign.Negative,
            SupportNeighborhoodJoins.Joined(polygons[0], polygons[4], 0.0001f, kernel, incidence, 0, 4));
        Assert.False(incidence.Offers(0, polygons.Count - 1));
    }

    [Fact]
    public void WarmBatchedSourceIncidenceAndResolutionAllocateNothing()
    {
        RigidPose pose = new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f));
        SupportPolygon?[] polygons =
        [
            IncidencePolygon([Vector3.Zero, new(1, 0, 0), new(1, 0, 1)], pose, 1),
            IncidencePolygon([Vector3.Zero, new(-1, 0, 0), new(-1, 0, 1)], pose, 2),
        ];
        var incidence = new SupportSourceIncidence();
        var kernel = new SupportSegmentPolygon();
        incidence.Build(polygons);
        Assert.Equal(GeometrySign.Positive,
            SupportNeighborhoodJoins.Joined(polygons[0]!, polygons[1]!, 0.0001f, kernel, incidence, 0, 1));
        GeometrySign joined = default;
        long bytes = AllocationMeasurement.Measure(nameof(WarmBatchedSourceIncidenceAndResolutionAllocateNothing), () =>
        {
            incidence.Build(polygons);
            joined = SupportNeighborhoodJoins.Joined(polygons[0]!, polygons[1]!, 0.0001f, kernel, incidence, 0, 1);
        });
        Assert.Equal(GeometrySign.Positive, joined);
        Assert.Equal(0, bytes);
    }

    static SupportPolygon IncidencePolygon(Vector3[] local, in RigidPose pose, int owner)
    {
        if (Vector3.Cross(local[1] - local[0], local[2] - local[0]).Y < 0) Array.Reverse(local);
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var map));
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(owner), 0, local.Length);
        polygon.BeginSource(map);
        for (int i = 0; i < local.Length; i++)
        {
            polygon.SetSource(i, local[i]);
            polygon.Set(i, map.Point(local[i], exactZeroProducts: true),
                map.Edge(local[i], local[(i + 1) % local.Length], exactZeroProducts: true));
        }
        polygon.Finish(FeaturePoint.Cross(map.Edge(local[0], local[1], true),
            map.Edge(local[0], local[2], true), exactZeroProducts: true));
        return polygon;
    }
}
