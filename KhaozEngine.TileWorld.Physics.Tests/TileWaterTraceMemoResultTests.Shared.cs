using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

// The exact-result memo fixture, linked into KhaozEngine.TileWorld.Physics.FloatingPoint.Tests without the tests.
public sealed partial class TileWaterTraceMemoResultTests
{
    internal static WaterTraceMemoComparison.Observation Check(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256, MovementQueryIdentity? identity = null) =>
        WaterTraceMemoComparison.Check(field, query, origin, spanCapacity, contactCapacity, identity,
            new FrozenWaterReference(field.CaptureTraceForTests()));

    internal static TileMediumSampler Field(bool flat)
    {
        var geometry = WaterKernelTraceFixture.RepeatedFaces(64, wet: true);
        if (flat)
        {
            var columns = geometry.Nonuniform;
            geometry = geometry with
            {
                Columns = columns,
                Nonuniform = [],
                Boundaries = TileWaterBoundary.Build(columns, true),
                Exterior = TileWaterBoundary.Build(columns, false)
            };
        }
        return TileMediumSampler.FromTraceForTests(geometry, "water-world");
    }

    internal static MovementMediumSweepQuery Query(int i = 0)
    {
        var q = WaterKernelTraceFixture.Query();
        return new(new(q.Body.Centre + new Vector3(0, i * .0001f, 0), q.Body.Radius, q.Body.HalfHeight,
            q.Body.CurrentSpace, null), q.Delta);
    }

    internal static int Work(WaterTraceMemoComparison.Observation read) => read.Solves + read.FlatSolves;
}
