using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Physics.SupportNeighborhoodTests;

namespace KhaozEngine.Tests.Physics;

// Tangent elements of sphere, capsule and cylinder statics. Every probe pose is computed from the installed geometry
// in binary64. Expectations come from that geometry or from the binary64 oracle, never from a query result.
public class SupportNeighborhoodCurvedTests
{
    // SupportCertification.ContactBand, which this project cannot reference.
    const float Band = 0.0001f;
    // The probe of the shared helpers: radius 0.2, 0.01 long.
    static double Radius => 0.2;
    // Tangent element ids are leaf * 256 + part: side 0, top cap 1, bottom cap 2.
    const int Leaf = SupportNeighborhoodOracle.TangentLeafStride;
    // A bound that keeps the tight rows meaningful: an element whose closest point is unique is published well
    // inside it.
    const double Tight = 1e-5;
    static readonly OracleVector Up = new(0, 1, 0);

    [Fact]
    public void SphereTopUnderTheAxis()
    {
        using var scene = new Scene();
        StaticHandle sphere = scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0.3f, 0, 0)));
        Query query = scene.Query(scene.World, Lowest(0.3f, 0.25 + Radius, 0));

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement top = Assert.Single(query.Elements);
        Assert.Empty(query.Joins);
        Assert.Equal(sphere, top.Static);
        Assert.Equal(SupportElementKind.Tangent, top.Kind);
        Assert.Equal(0, top.ElementId);
        // The axis passes through the centre, so the closest point is exactly the top.
        Assert.True(Math.Abs(top.Witness.Y - 0.25) <= top.PositionErrorMetres, "The witness is the sphere's top.");
        Assert.True(Distance(top.Witness, new OracleVector(0.3f, 0.25, 0)) <= top.PositionErrorMetres);
        Assert.True(Distance(top.Normal, Up) <= top.NormalError, "The normal is +Y.");
        Assert.InRange(top.PositionErrorMetres, 0, Tight);
        Assert.InRange(top.NormalError, 0, Tight);
        Assert.True(top.SeparationLower <= Band);
    }

    [Fact]
    public void SphereSideContactHasARadialNormal()
    {
        // The probe's lowest point rests one radius out along a direction 50 degrees from vertical. Its axis rises
        // away from the centre, so the lowest point is the axis point nearest the centre.
        using var scene = new Scene();
        var centre = new Vector3(1, 0.2f, -0.5f);
        const double sphereRadius = 0.5;
        StaticHandle sphere = scene.World.AddStatic(new SphereShape((float)sphereRadius), Pose.At(centre));
        double theta = 50 * Math.PI / 180, phi = 30 * Math.PI / 180;
        var direction = new OracleVector(Math.Sin(theta) * Math.Cos(phi), Math.Cos(theta), Math.Sin(theta) * Math.Sin(phi));
        OracleVector lowest = OracleVector.From(centre) + direction * (sphereRadius + Radius);
        Pose pose = Lowest(lowest.X, lowest.Y, lowest.Z);
        Query query = scene.Query(scene.World, pose);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement side = Assert.Single(query.Elements);
        Assert.Equal(sphere, side.Static);
        Assert.Equal(SupportElementKind.Tangent, side.Kind);
        OracleVector normal = (Segment(pose).Lower - OracleVector.From(centre)).Unit;
        AssertTight(side, OracleVector.From(centre) + normal * sphereRadius, normal);
        // Radial, not vertical: the normal leans 50 degrees from +Y.
        Assert.InRange(side.Normal.Y, Math.Cos(theta) - Tight, Math.Cos(theta) + Tight);
    }

    [Fact]
    public void CapsuleBarrelAndCapNormals()
    {
        // A capsule of radius 0.3 and length 1.2 lying along X: its local +Y end is at world (-0.6, 0.5, 0).
        using var scene = new Scene();
        const double capsuleRadius = 0.3;
        var pose = new Pose(new Vector3(0, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        StaticHandle capsule = scene.World.AddStatic(new CapsuleShape((float)capsuleRadius, 1.2f), pose);
        OracleTangent installed = Assert.Single(SupportNeighborhoodOracle.InstalledCurved(scene.World, capsule));
        Assert.True((installed.Axis - new OracleVector(-1, 0, 0)).Length < 1e-6, "The core lies along X.");

        // The barrel: straight above the core at x 0.2.
        Query barrel = scene.Query(scene.World, Lowest(0.2f, 0.5 + capsuleRadius + Radius, 0));
        Assert.Equal(CapsuleFeatureStatus.Complete, barrel.Result.Status);
        SupportElement top = Assert.Single(barrel.Elements);
        Assert.Equal(capsule, top.Static);
        Assert.Equal(0, top.ElementId);
        AssertTight(top, new OracleVector(0.2f, 0.5 + capsuleRadius, 0), Up);

        // The cap: one radius out from the end, 45 degrees up from the core's direction.
        OracleVector end = new(-0.6, 0.5, 0);
        OracleVector outward = new OracleVector(-1, 1, 0).Unit;
        OracleVector lowest = end + outward * (capsuleRadius + Radius);
        Pose capPose = Lowest(lowest.X, lowest.Y, lowest.Z);
        Query cap = scene.Query(scene.World, capPose);
        Assert.Equal(CapsuleFeatureStatus.Complete, cap.Result.Status);
        SupportElement rounded = Assert.Single(cap.Elements);
        Assert.Equal(0, rounded.ElementId);
        OracleVector normal = (Segment(capPose).Lower - end).Unit;
        AssertTight(rounded, end + normal * capsuleRadius, normal);
    }

    [Fact]
    public void UprightCylinderCapAndSide()
    {
        // A cylinder of radius 0.4 and length 1 placed at its base (2, 0.25, 1): it spans y 0.25 to 1.25.
        using var scene = new Scene();
        var basePoint = new Vector3(2, 0.25f, 1);
        StaticHandle cylinder = scene.World.AddStatic(new CylinderShape(0.4f, 1), Pose.At(basePoint));

        // On the cap, off the axis and inside the rim: the cap alone.
        Pose onCap = Lowest(2.1f, 1.25 + Radius, 0.85f);
        Query cap = scene.Query(scene.World, onCap);
        Assert.Equal(CapsuleFeatureStatus.Complete, cap.Result.Status);
        SupportElement top = Assert.Single(cap.Elements);
        Assert.Equal(cylinder, top.Static);
        Assert.Equal(SupportElementKind.Tangent, top.Kind);
        Assert.Equal(1, top.ElementId);
        OracleVector lowest = Segment(onCap).Lower;
        Assert.True(Distance(top.Witness, new OracleVector(lowest.X, 1.25, lowest.Z)) <= top.PositionErrorMetres,
            "The witness lies on the cap at the base plus the length.");
        Assert.True(Distance(top.Normal, Up) <= top.NormalError, "The cap normal is +Y.");
        Assert.InRange(top.PositionErrorMetres, 0, Tight);

        // Beside the side at mid height, 40 degrees round from +X: the side alone. The vertical axis runs parallel
        // to the side, so every axis point is closest, and the witness may lie anywhere along the axis's span.
        double phi = 40 * Math.PI / 180;
        Pose beside = Lowest(2 + (0.4 + Radius) * Math.Cos(phi), 0.6, 1 + (0.4 + Radius) * Math.Sin(phi));
        Query side = scene.Query(scene.World, beside);
        Assert.Equal(CapsuleFeatureStatus.Complete, side.Result.Status);
        SupportElement wall = Assert.Single(side.Elements);
        Assert.Equal(0, wall.ElementId);
        (OracleVector lower, OracleVector upper) = Segment(beside);
        OracleVector radial = new OracleVector(lower.X - 2, 0, lower.Z - 1).Unit;
        Assert.True(Distance(wall.Normal, radial) <= wall.NormalError, "The side normal is radial.");
        Assert.InRange(wall.NormalError, 0, Tight);
        var horizontal = new OracleVector(2 + radial.X * 0.4f, 0, 1 + radial.Z * 0.4f);
        Assert.True(Distance(new Vector3(wall.Witness.X, 0, wall.Witness.Z), horizontal) <= wall.PositionErrorMetres);
        Assert.InRange(wall.Witness.Y, lower.Y - wall.PositionErrorMetres, upper.Y + wall.PositionErrorMetres);
    }

    [Fact]
    public void CylinderRimPublishesCapAndSide()
    {
        // The probe's lowest point is one radius out from the rim point (0.4, 1, 0) along the 45 degree diagonal.
        using var scene = new Scene();
        StaticHandle cylinder = scene.World.AddStatic(new CylinderShape(0.4f, 1), Pose.Identity);
        var rim = new OracleVector(0.4f, 1, 0);
        OracleVector lowest = rim + new OracleVector(1, 1, 0).Unit * Radius;
        Query query = scene.Query(scene.World, Lowest(lowest.X, lowest.Y, lowest.Z));

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal([0, 1], Array.ConvertAll(query.Elements, e => e.ElementId));
        Assert.All(query.Elements, e => Assert.Equal(cylinder, e.Static));
        Assert.All(query.Elements, e => Assert.Equal(SupportElementKind.Tangent, e.Kind));
        Assert.Empty(query.Joins);
        AssertTight(query.Elements[0], rim, new OracleVector(1, 0, 0));
        AssertTight(query.Elements[1], rim, Up);
    }

    [Fact]
    public void LyingLogSupportsItsTopLine()
    {
        // A log of radius 0.3 and length 2 placed at (1, 0.3, 0) and rotated 90 degrees about Z. Its base aligned
        // leaf lifts one half length along local Y, which now points along -X, so the log's centre is (0, 0.3, 0).
        using var scene = new Scene();
        var pose = new Pose(new Vector3(1, 0.3f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        StaticHandle log = scene.World.AddStatic(new CylinderShape(0.3f, 2), pose);
        List<OracleTangent> installed = SupportNeighborhoodOracle.InstalledCurved(scene.World, log);
        Assert.Equal(3, installed.Count);
        OracleVector centre = installed[0].Centre;
        Assert.True((centre - new OracleVector(0, 0.3f, 0)).Length < 1e-6, "The log's centre is (0, 0.3, 0).");

        // The axis probe stands over the log's axis line, a quarter metre from its middle.
        Query query = scene.Query(scene.World, Lowest(0.25f, centre.Y + 0.3f + Radius, 0));
        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement top = Assert.Single(query.Elements);
        Assert.Equal(log, top.Static);
        Assert.Equal(0, top.ElementId);
        Assert.True(Math.Abs(top.Witness.Y - (centre.Y + 0.3f)) <= top.PositionErrorMetres + SupportNeighborhoodOracle.Geometry,
            "The witness lies on the log's top line.");
        AssertTight(top, new OracleVector(0.25f, centre.Y + 0.3f, 0), Up);
    }

    [Fact]
    public void CompoundOfBoxAndCylinderPublishesBoth()
    {
        // A unit cube spanning x -1 to 0 beside an upright cylinder of radius 0.3 whose side touches the cube's +X
        // face along x 0. Both tops are at y 0.5, and the probe stands one radius above their shared line.
        using var scene = new Scene();
        StaticHandle compound = scene.World.AddStatic(new CompoundShape(
        [
            new(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(-0.5f, 0, 0))),
            new(new CylinderShape(0.3f, 1), Pose.At(new Vector3(0.3f, -0.5f, 0))),
        ]), Pose.Identity);
        Query query = scene.Query(scene.World, Lowest(0, 0.5 + Radius, 0));

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.All(query.Elements, e => Assert.Equal(compound, e.Static));
        // The cube's +X face and top, then the cylinder's side and top cap.
        Assert.Equal([1, SupportNeighborhoodOracle.BoxTop, Leaf + 0, Leaf + 1],
            Array.ConvertAll(query.Elements, e => e.ElementId));
        Assert.Equal([SupportElementKind.Polygon, SupportElementKind.Polygon, SupportElementKind.Tangent,
            SupportElementKind.Tangent], Array.ConvertAll(query.Elements, e => e.Kind));
        // The cube's convex edge joins its faces. Tangent elements are never joined.
        Assert.Equal([new Join(0, 1)], query.Joins);
        AssertTight(query.Elements[2], new OracleVector(0, 0.5, 0), new OracleVector(-1, 0, 0));
        AssertTight(query.Elements[3], new OracleVector(0, 0.5, 0), Up);
    }

    [Fact]
    public void ProbeInsideTheSolidIsNotAMember()
    {
        // The axis lies inside a sphere and inside an upright cylinder, its upper end one probe radius below the top.
        // Each surface is within the band of the probe, behind it, so neither is a member.
        foreach (PhysicsShape shape in (PhysicsShape[])[new SphereShape(1), new CylinderShape(1, 2)])
        {
            using var scene = new Scene();
            // The sphere's top and the base aligned cylinder's top cap are both at y 1.
            scene.World.AddStatic(shape, shape is SphereShape ? Pose.Identity : Pose.At(new Vector3(0, -1, 0)));
            Query query = scene.Query(scene.World, Lowest(0, 1 - Radius - 0.01, 0));
            Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
            Assert.Empty(query.Elements);
        }
    }

    [Fact]
    public void CurvedMembershipMatchesBruteForceOracle()
    {
        var random = new Random(1331);
        int poses = 0;
        for (int solid = 0; solid < 50; solid++)
        {
            using var scene = new Scene();
            Quaternion rotation = Quaternion.Normalize(new Quaternion(Signed(random), Signed(random), Signed(random),
                Signed(random)));
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            // Radii exceed the probe's, so no member's closest point sits on a degenerate axis.
            float radius = Between(random, 0.25, 0.8), length = Between(random, 0.2, 1.5);
            PhysicsShape shape = (solid % 3) switch
            {
                0 => new SphereShape(radius),
                1 => new CapsuleShape(radius, length),
                _ => new CylinderShape(radius, length),
            };
            // Every other solid is a compound leaf under its own rotated local pose.
            if (solid % 2 == 1)
                shape = new CompoundShape([new(shape, new Pose(new Vector3(Signed(random), Signed(random),
                    Signed(random)) * 0.5f, Quaternion.Normalize(new Quaternion(Signed(random), Signed(random),
                    Signed(random), Signed(random)))))]);
            StaticHandle handle = scene.World.AddStatic(shape, new Pose(position, rotation));
            List<OracleTangent> tangents = SupportNeighborhoodOracle.InstalledCurved(scene.World, handle);
            for (int sample = 0; sample < 10; sample++, poses++)
            {
                OracleTangent element = tangents[random.Next(tangents.Count)];
                (OracleVector target, OracleVector normal) = SurfacePoint(random, element);
                // Outward from the chosen surface, tilted at random, at a separation inside the band.
                OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.7;
                OracleVector outward = (normal + tilt).Unit;
                if (OracleVector.Dot(outward, normal) < 0.2) outward = normal;
                OracleVector lowest = target + outward * (Radius + Between(random, -5e-5, 5e-5));
                Pose probe = Lowest(lowest.X, lowest.Y, lowest.Z);
                Query query = scene.Query(scene.World, probe);
                Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
                Assert.Empty(query.Joins);
                (OracleVector lower, OracleVector upper) = Segment(probe);
                SupportNeighborhoodOracle.AssertTangentsMatch(query.Elements, tangents, lower, upper, Radius, Band);
                Assert.True(SupportNeighborhoodOracle.IndexOf(query.Elements, element) >= 0,
                    $"Pose {poses}: the element the probe was placed against is a member.");
            }
        }
        Assert.Equal(500, poses);
    }

    // A point of the element and the outward normal there. A quarter of side and cap points lie on the rim, and
    // a capsule point lies on an end cap or on the barrel.
    static (OracleVector Point, OracleVector Normal) SurfacePoint(Random random, OracleTangent element)
    {
        OracleVector axis = element.Axis, across = Across(random, axis);
        double radius = element.Radius, half = element.HalfLength;
        switch (element.Surface)
        {
            case OracleSurface.Round when half == 0 || random.Next(5) < 2:
                {
                    OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                    double end = random.Next(2) == 0 ? -1 : 1;
                    if (OracleVector.Dot(direction, axis) * end < 0) direction -= axis * (2 * OracleVector.Dot(direction, axis));
                    return (element.Centre + axis * (end * half) + direction * radius, direction);
                }
            case OracleSurface.Round:
                return (element.Centre + axis * ((random.NextDouble() * 2 - 1) * half) + across * radius, across);
            case OracleSurface.CylinderSide:
                double along = random.Next(4) == 0 ? (random.Next(2) == 0 ? -half : half) : (random.NextDouble() * 2 - 1) * half;
                return (element.Centre + axis * along + across * radius, across);
            default:
                double side = element.Surface == OracleSurface.CylinderTop ? 1 : -1;
                double reach = random.Next(4) == 0 ? radius : radius * Math.Sqrt(random.NextDouble());
                return (element.Centre + axis * (side * half) + across * reach, axis * side);
        }
    }

    // A random unit vector perpendicular to the unit axis.
    static OracleVector Across(Random random, OracleVector axis)
    {
        while (true)
        {
            var candidate = new OracleVector(Signed(random), Signed(random), Signed(random));
            OracleVector across = candidate - axis * OracleVector.Dot(candidate, axis);
            if (across.Length > 0.1) return across.Unit;
        }
    }

    // The element's witness and normal agree with the installed geometry within their published errors, and both
    // errors are small.
    static void AssertTight(SupportElement element, OracleVector witness, OracleVector normal)
    {
        Assert.Equal(SupportElementKind.Tangent, element.Kind);
        Assert.True(Distance(element.Witness, witness) <= element.PositionErrorMetres + SupportNeighborhoodOracle.Geometry,
            $"Witness {element.Witness} is within its error {element.PositionErrorMetres} of the closest point.");
        Assert.True(Distance(element.Normal, normal) <= element.NormalError + SupportNeighborhoodOracle.Geometry,
            $"Normal {element.Normal} is within its error {element.NormalError} of the surface normal.");
        Assert.InRange(element.PositionErrorMetres, 0, Tight);
        Assert.InRange(element.NormalError, 0, Tight);
        Assert.True(element.SeparationLower <= Band);
    }

    static double Distance(Vector3 published, OracleVector expected) => (OracleVector.From(published) - expected).Length;
}
