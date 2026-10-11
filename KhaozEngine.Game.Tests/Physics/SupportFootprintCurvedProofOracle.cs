using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;
using Bounds = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using Point = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

/// <summary>Closed-form proof strata over exact installed sources. Root bounds use the independent integer
/// square root owner. Production capture, extrema, intervals and source predicates are never called.</summary>
internal static class SupportFootprintCurvedProofOracle
{
    internal readonly record struct Reading(Point Witness, Point Normal, Bounds Contribution, Bounds PerPoint,
        SupportStatus Status, bool? HasAxis);
    internal readonly record struct InstalledPart(OracleTangent Tangent, Point Centre, Point Axis, R Radius, R Half);
    internal readonly record struct Boundary(Point Witness, Point Normal, Bounds Contribution, Bounds Derivative,
        Bounds AxialCoordinate);

    static Bounds Exact(double value) => R.From(value);
    static Bounds Min(Bounds a, Bounds b) => new(a.Lower < b.Lower ? a.Lower : b.Lower,
        a.Upper < b.Upper ? a.Upper : b.Upper);
    static Bounds Root(Bounds value) => new(Bounds.Root(value.Lower).Lower, Bounds.Root(value.Upper).Upper);
    static Point Add(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static Point Scale(Point a, Bounds b) => new(a.X * b, a.Y * b, a.Z * b);
    static Point Up => Point.Exact(V.UnitY);
    static Bounds Tangent(Point p, Point n, R x, R z) => p.Y +
        (n.X * (p.X - (Bounds)x) + n.Z * (p.Z - (Bounds)z)) / n.Y;

    internal static Reading Sphere(R centreY, R radius, R axisX, R discRadius, R cosineSquared)
    {
        R nearest = axisX > discRadius ? axisX - discRadius : R.Zero;
        Bounds y = Bounds.Root(radius.Square() - nearest.Square());
        var point = new Point(nearest, (Bounds)centreY + y, R.Zero);
        var normal = new Point(nearest / radius, y / (Bounds)radius, R.Zero);
        Bounds contribution = Min(point.Y, Tangent(point, normal, axisX, R.Zero));
        return new(point, normal, contribution, contribution,
            radius.Square() - nearest.Square() >= radius.Square() * cosineSquared
                ? SupportStatus.Walkable : SupportStatus.Steep, axisX <= radius);
    }

    internal static R AnisotropicContribution(R x, R z)
    {
        R height = -(x - R.One).Square() - new R(100, 1) * z.Square();
        R tangent = x.Square() + new R(100, 1) * z.Square() - R.One;
        return height < tangent ? height : tangent;
    }

    internal static Bounds AnisotropicSupremum() =>
        (Bounds.Root(new R(1585, 1)) - (Bounds)R.One) / (Bounds)new R(198, 1) - (Bounds)R.One;

    internal static Boundary WalkableBoundary(InstalledPart part, in SupportFootprintRequest request)
    {
        Point axis = part.Axis;
        Bounds k = axis.Y, u = Root(axis.X * axis.X + axis.Z * axis.Z);
        var e = new Point(axis.X / u, R.Zero, axis.Z / u);
        var f = new Point(-e.Z, R.Zero, e.X);
        var w = new Point(-k * e.X, u, -k * e.Z);
        Bounds dx = part.Centre.X - Exact(request.Axis.X), dz = part.Centre.Z - Exact(request.Axis.Y);
        Bounds p = dx * e.X + dz * e.Z, q = dx * f.X + dz * f.Z;
        Bounds v = Exact(request.CosMaxSlope) / u;
        Bounds z = -Root((Bounds)R.One - v * v);
        Bounds lateral = q + (Bounds)part.Radius * z;
        Bounds extent = Root(Exact(request.Radius) * Exact(request.Radius) - lateral * lateral);
        Bounds t = ((Bounds)part.Radius * k * v - p + extent) / u;
        Point normal = Add(Scale(w, v), Scale(f, z));
        normal = normal with { Y = Exact(request.CosMaxSlope) };
        Point witness = Add(Add(part.Centre, Scale(axis, t)), Scale(normal, part.Radius));
        Bounds contribution = Min(witness.Y, Tangent(witness, normal, R.From(request.Axis.X), R.From(request.Axis.Y)));
        Bounds derivative = -((Bounds)part.Radius * z / v + k * (Bounds)part.Radius * lateral / extent) / u;
        return new(witness, normal, contribution, derivative, t);
    }

    internal static Point CapPoint(InstalledPart part, R x, R z)
    {
        Point normal = Scale(part.Axis, new R(part.Tangent.Surface == OracleSurface.CylinderTop ? 1 : -1, 1));
        Point centre = Add(part.Centre, Scale(normal, part.Half));
        return new(x, Tangent(centre, normal, x, z), z);
    }

    internal static (double Lower, double Upper)? CapReading(OracleTangent part, in SupportFootprintRequest request)
    {
        if (part.ExactAxis is not { } source) return null;
        Point normal = Scale(source, new R(part.Surface == OracleSurface.CylinderTop ? 1 : -1, 1));
        if (normal.Y.Lower <= R.Zero) return null;
        Point centre = Add(Point.Exact(new(R.From(part.Centre.X), R.From(part.Centre.Y), R.From(part.Centre.Z))),
            Scale(normal, R.From(part.HalfLength)));
        Bounds plane = Tangent(centre, normal, R.From(request.Axis.X), R.From(request.Axis.Y));
        Bounds radius = Exact(part.Radius), disc = Exact(request.Radius);
        Bounds ax = Exact(request.Axis.X), az = Exact(request.Axis.Y);
        Bounds horizontal = Root(normal.X * normal.X + normal.Z * normal.Z);
        if (horizontal.Upper == R.Zero) return Publish(plane);
        Point crest = Add(centre, Scale(Add(Up, Scale(normal, -normal.Y)), radius / horizontal));
        Bounds DiscSquare(Point p) => (p.X - ax) * (p.X - ax) + (p.Z - az) * (p.Z - az);
        if (DiscSquare(crest).Upper <= (disc * disc).Lower) return Publish(Min(plane, crest.Y));
        bool InCap(Point p)
        {
            Bounds dx = p.X - centre.X, dy = p.Y - centre.Y, dz = p.Z - centre.Z;
            return (dx * dx + dy * dy + dz * dz).Upper <= (radius * radius).Lower;
        }
        var axisPoint = new Point(Exact(request.Axis.X), plane, Exact(request.Axis.Y));
        if (InCap(axisPoint)) return Publish(plane);
        // A certified feasible point above the axis plane attains the cap's constant tangent upper bound.
        Bounds x = (centre.X + Exact(request.Axis.X)) / (Bounds)new R(2, 1);
        Bounds z = (centre.Z + Exact(request.Axis.Y)) / (Bounds)new R(2, 1);
        Bounds y = centre.Y + (normal.X * (centre.X - x) + normal.Z * (centre.Z - z)) / normal.Y;
        var middle = new Point(x, y, z);
        return InCap(middle) && DiscSquare(middle).Upper <= (disc * disc).Lower && y.Lower >= plane.Upper
            ? Publish(plane) : null;
    }

    internal static double RoundSampleLower(OracleTangent part, double x, double z,
        in SupportFootprintRequest request)
    {
        double lower = double.NegativeInfinity;
        Point axis = part.ExactAxis ?? Point.Exact(new(R.From(part.Axis.X), R.From(part.Axis.Y), R.From(part.Axis.Z)));
        Point centre = Point.Exact(new(R.From(part.Centre.X), R.From(part.Centre.Y), R.From(part.Centre.Z)));
        R radius = R.From(part.Radius);
        foreach (double end in part.HalfLength == 0 ? new[] { 0.0 } : new[] { -part.HalfLength, part.HalfLength })
        {
            Point c = Add(centre, Scale(axis, R.From(end)));
            Bounds dx = Exact(x) - c.X, dz = Exact(z) - c.Z;
            Bounds square = (Bounds)radius.Square() - dx * dx - dz * dz;
            if (square.Lower <= R.Zero) continue;
            Bounds dy = Root(square);
            var normal = new Point(dx / (Bounds)radius, dy / (Bounds)radius, dz / (Bounds)radius);
            Bounds along = normal.X * axis.X + normal.Y * axis.Y + normal.Z * axis.Z;
            if (part.HalfLength != 0 && (end > 0 ? along.Lower < R.Zero : along.Upper > R.Zero)) continue;
            var point = new Point(Exact(x), c.Y + dy, Exact(z));
            Bounds contribution = Min(point.Y, Tangent(point, normal, R.From(request.Axis.X), R.From(request.Axis.Y)));
            lower = Math.Max(lower, Publish(contribution).Lower);
        }
        return lower;
    }

    internal static IReadOnlyList<InstalledPart> Installed(BepuPhysicsWorld world, StaticHandle owner)
    {
        var sim = (BepuPhysics.Simulation)typeof(BepuPhysicsWorld)
            .GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        var handles = (Dictionary<int, (BepuPhysics.StaticHandle Handle, TypedIndex Shape)>)typeof(BepuPhysicsWorld)
            .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        sim.Statics.GetDescription(handles[owner.Value].Handle, out StaticDescription description);
        var result = new List<InstalledPart>();
        Pose parent = new(description.Pose.Position, description.Pose.Orientation);
        void Add(TypedIndex index, Pose pose, int leaf)
        {
            Point centre = Point.Exact(V.From(pose.Position));
            V source = InstalledPoseOracle.Real(pose.Orientation).Y;
            Bounds length = Bounds.Root(source.LengthSquared);
            var axis = new Point((Bounds)source.X / length, (Bounds)source.Y / length, (Bounds)source.Z / length);
            int id = leaf * SupportNeighborhoodOracle.TangentLeafStride;
            void Part(OracleSurface surface, float radius, float half, int part) => result.Add(new(
                new OracleTangent(owner, id + part, surface, centre.Middle, axis.Middle, radius, half)
                { ExactAxis = axis }, centre, axis,
                R.From(radius), R.From(half)));
            if (index.Type == default(Sphere).TypeId)
                Part(OracleSurface.Round, sim.Shapes.GetShape<Sphere>(index.Index).Radius, 0, 0);
            else if (index.Type == default(Capsule).TypeId)
            {
                ref Capsule capsule = ref sim.Shapes.GetShape<Capsule>(index.Index);
                Part(OracleSurface.Round, capsule.Radius, capsule.HalfLength, 0);
            }
            else if (index.Type == default(Cylinder).TypeId)
            {
                ref Cylinder cylinder = ref sim.Shapes.GetShape<Cylinder>(index.Index);
                Part(OracleSurface.CylinderSide, cylinder.Radius, cylinder.HalfLength, 0);
                Part(OracleSurface.CylinderTop, cylinder.Radius, cylinder.HalfLength, 1);
                Part(OracleSurface.CylinderBottom, cylinder.Radius, cylinder.HalfLength, 2);
            }
        }
        if (description.Shape.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref sim.Shapes.GetShape<Compound>(description.Shape.Index);
            for (int leaf = 0; leaf < compound.Children.Length; leaf++)
            {
                var child = compound.Children[leaf];
                Add(child.ShapeIndex, InstalledPoseOracle.Composed(new(child.LocalPose.Position,
                    child.LocalPose.Orientation), parent), leaf);
            }
        }
        else Add(description.Shape, parent, 0);
        return result;
    }

    // The row admits the whole crest. A global height bound independently certifies the per-point supremum
    // when its tangent is no lower. On a cap every tangent is its one constant plane at the query axis.
    internal static Reading Crest(InstalledPart part, in SupportFootprintRequest request)
    {
        Point axis = part.Axis.Y.Upper < R.Zero ? Scale(part.Axis, -R.One) : part.Axis;
        bool cap = part.Tangent.Surface is OracleSurface.CylinderTop or OracleSurface.CylinderBottom;
        if (cap) axis = Scale(part.Axis, new R(part.Tangent.Surface == OracleSurface.CylinderTop ? 1 : -1, 1));
        Point end = Add(part.Centre, Scale(axis, part.Half));
        Point normal;
        if (part.Tangent.Surface == OracleSurface.Round) normal = Up;
        else
        {
            Bounds horizontal = Root(axis.X * axis.X + axis.Z * axis.Z);
            Point radial = Add(Up, Scale(axis, -axis.Y));
            normal = horizontal.Upper == R.Zero ? Point.Exact(V.Zero) : Scale(radial, (Bounds)R.One / horizontal);
        }
        Point witness = Add(end, Scale(normal, part.Radius));
        if (cap) normal = axis;
        Bounds plane = Tangent(witness, normal, R.From(request.Axis.X), R.From(request.Axis.Y));
        Bounds contribution = Min(witness.Y, plane);
        if (!cap && plane.Lower < witness.Y.Upper && part.Tangent.Surface != OracleSurface.Round)
            throw new InvalidOperationException("The crest row must certify its global height as the alternate reading.");
        return new(witness, normal, contribution, cap ? Min(witness.Y, plane) : witness.Y,
            normal.Y.Lower >= R.From(request.CosMaxSlope) ? SupportStatus.Walkable : SupportStatus.Steep,
            null);
    }

    internal static (double Lower, double Upper) Publish(Bounds bounds)
    {
        double lower = Approximate(bounds.Lower);
        double upper = Approximate(bounds.Upper);
        while (R.From(lower) > bounds.Lower) lower = Math.BitDecrement(lower);
        while (R.From(upper) < bounds.Upper) upper = Math.BitIncrement(upper);
        return (lower, upper);
    }

    // Nested rational root bounds can have huge numerator and denominator while their ratio stays small.
    // Scale each integer before conversion, then certify the publication direction against the exact ratio.
    static double Approximate(R value)
    {
        BigInteger numerator = BigInteger.Abs(value.Numerator);
        int numeratorShift = Math.Max(0, checked((int)numerator.GetBitLength()) - 64);
        int denominatorShift = Math.Max(0, checked((int)value.Denominator.GetBitLength()) - 64);
        double ratio = (double)(numerator >> numeratorShift) / (double)(value.Denominator >> denominatorShift);
        return value.Numerator.Sign * Math.ScaleB(ratio, numeratorShift - denominatorShift);
    }
}
