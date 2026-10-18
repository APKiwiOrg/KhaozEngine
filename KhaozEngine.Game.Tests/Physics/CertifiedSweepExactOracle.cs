using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// Independent configuration-space oracle. The moving axis is absorbed into a finite convex hull.
// Hull facets are discovered by exhaustive rational sidedness, rather than the production regimes.
// All allocation and exact rational root isolation belong outside allocation measurement windows.
internal static class CertifiedSweepExactOracle
{
    internal static (double Lower, double Upper) BoxEntry(BepuPhysicsWorld world, StaticHandle handle,
        Vector3 centre, float radius, float halfCylinderLength, Vector3 delta)
    {
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var simulation = (BepuSim)typeof(BepuPhysicsWorld).GetField("_sim", flags)!.GetValue(world)!;
        var handles = (Dictionary<int, (BepuPhysics.StaticHandle Handle, TypedIndex Shape)>)
            typeof(BepuPhysicsWorld).GetField("_handles", flags)!.GetValue(world)!;
        var entry = handles[handle.Value];
        simulation.Statics.GetDescription(entry.Handle, out var installed);
        Assert.Equal(default(Box).TypeId, entry.Shape.Type);
        ref Box box = ref simulation.Shapes.GetShape<Box>(entry.Shape.Index);
        V translation = V.From(installed.Pose.Position);
        // Enclose the real installed polynomial and the independently evaluated represented map.
        // Neither is replaced by a normalized quaternion or its assumed inverse.
        var exact = Entry(Real(installed.Pose.Orientation), translation, box, centre, radius, halfCylinderLength, delta);
        var represented = Entry(Represented(installed.Pose.Orientation), translation, box, centre, radius, halfCylinderLength, delta);
        lease.AssertCurrent();
        return (Math.Min(exact.Lower, represented.Lower), Math.Max(exact.Upper, represented.Upper));
    }

    internal static (double Lower, double Upper) HullEntry(BepuPhysicsWorld world, StaticHandle handle,
        Vector3 centre, float radius, float halfCylinderLength, Vector3 delta)
    {
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var simulation = CertifiedGeneralSweepFixture.Sim(world);
        var entry = CertifiedGeneralSweepFixture.Static(world, handle);
        simulation.Statics.GetDescription(entry.Handle, out var installed);
        var pose = installed.Pose;
        TypedIndex index = entry.Shape;
        if (index.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref simulation.Shapes.GetShape<Compound>(index.Index);
            Assert.Equal(1, compound.Children.Length);
            var child = compound.Children[0];
            Compound.GetWorldPose(child.LocalPose, pose, out var worldPose);
            pose = worldPose;
            index = child.ShapeIndex;
        }
        Assert.Equal(default(ConvexHull).TypeId, index.Type);
        ref ConvexHull hull = ref simulation.Shapes.GetShape<ConvexHull>(index.Index);
        var points = new HashSet<V>();
        for (int face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
        {
            hull.GetVertexIndicesForFace(face, out var indices);
            for (int i = 0; i < indices.Length; i++)
            {
                hull.GetPoint(indices[i], out Vector3 point);
                points.Add(V.From(point));
            }
        }
        (double Lower, double Upper) Evaluate(M map)
        {
            var vertices = new V[points.Count * 2];
            int i = 0;
            foreach (V point in points)
            {
                V worldPoint = map.Apply(point) + V.From(pose.Position);
                V shift = V.UnitY * R.From(halfCylinderLength);
                vertices[i++] = worldPoint - shift;
                vertices[i++] = worldPoint + shift;
            }
            return Entry(vertices, centre, radius, delta);
        }
        var exact = Evaluate(Real(pose.Orientation));
        var represented = Evaluate(Represented(pose.Orientation));
        lease.AssertCurrent();
        return (Math.Min(exact.Lower, represented.Lower), Math.Max(exact.Upper, represented.Upper));
    }

    internal static (double Lower, double Upper) TriangleEntry(BepuPhysicsWorld world, StaticHandle handle,
        int triangleIndex, Vector3 centre, float radius, float halfCylinderLength, Vector3 delta)
    {
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var simulation = CertifiedGeneralSweepFixture.Sim(world);
        var entry = CertifiedGeneralSweepFixture.Static(world, handle);
        simulation.Statics.GetDescription(entry.Handle, out var installed);
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(entry.Shape.Index);
        mesh.GetLocalChild(triangleIndex, out Triangle triangle);
        (double Lower, double Upper) Evaluate(M map)
        {
            var vertices = new List<V>();
            foreach (Vector3 point in new[] { triangle.A, triangle.B, triangle.C })
            {
                V transformed = map.Apply(V.From(point)) + V.From(installed.Pose.Position);
                V shift = V.UnitY * R.From(halfCylinderLength);
                vertices.Add(transformed - shift); vertices.Add(transformed + shift);
            }
            return Entry(vertices.ToArray(), centre, radius, delta);
        }
        var exact = Evaluate(Real(installed.Pose.Orientation));
        var represented = Evaluate(Represented(installed.Pose.Orientation));
        lease.AssertCurrent();
        return (Math.Min(exact.Lower, represented.Lower), Math.Max(exact.Upper, represented.Upper));
    }

    static (double Lower, double Upper) Entry(M map, V translation, Box box, Vector3 centre,
        float radius, float halfCylinderLength, Vector3 delta)
    {
        var vertices = new V[16];
        for (int i = 0; i < vertices.Length; i++)
        {
            var local = V.From(new Vector3((i & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                (i & 2) == 0 ? -box.HalfHeight : box.HalfHeight,
                (i & 4) == 0 ? -box.HalfLength : box.HalfLength));
            V shift = V.UnitY * R.From((i & 8) == 0 ? -halfCylinderLength : halfCylinderLength);
            vertices[i] = map.Apply(local) + translation + shift;
        }
        return Entry(vertices, centre, radius, delta);
    }

    static (double Lower, double Upper) Entry(V[] vertices, Vector3 centre, float radius, Vector3 delta)
    {
        V start = V.From(centre), motion = V.From(delta);
        R radiusSquared = R.From(radius).Square();
        R? bestLower = null, bestUpper = null;
        var planes = new List<(V Normal, V Anchor)>();
        bool fullDimensional = false;
        for (int i = 0; i < vertices.Length; i++)
            for (int j = i + 1; j < vertices.Length; j++)
                for (int k = j + 1; k < vertices.Length; k++)
                {
                    V a = vertices[i], u = vertices[j] - a, v = vertices[k] - a;
                    V normal = V.Cross(u, v);
                    if (normal.LengthSquared == R.Zero) continue;
                    bool positive = false, negative = false;
                    foreach (V point in vertices)
                    {
                        R side = V.Dot(point - a, normal);
                        positive |= side > R.Zero;
                        negative |= side < R.Zero;
                    }
                    fullDimensional |= positive || negative;
                    if (positive && negative) continue;
                    planes.Add((positive ? normal * new R(-1, 1) : normal, a));
                    R uu = u.LengthSquared, uv = V.Dot(u, v), vv = v.LengthSquared;
                    R denominator = uu * vv - uv.Square();
                    V w = start - a;
                    R x0 = V.Dot(w, u) * vv - V.Dot(w, v) * uv;
                    R x1 = V.Dot(motion, u) * vv - V.Dot(motion, v) * uv;
                    R y0 = V.Dot(w, v) * uu - V.Dot(w, u) * uv;
                    R y1 = V.Dot(motion, v) * uu - V.Dot(motion, u) * uv;
                    R n0 = V.Dot(w, normal), n1 = V.Dot(motion, normal);
                    Offer(n1.Square(), new R(2, 1) * n0 * n1,
                        n0.Square() - radiusSquared * normal.LengthSquared,
                        [(x0, x1), (y0, y1), (denominator - x0 - y0, -x1 - y1)]);
                }
        // Every chord lies in the solid. Extra interior chords cannot create a false event.
        for (int i = 0; i < vertices.Length; i++)
        {
            V w = start - vertices[i];
            Offer(motion.LengthSquared, new R(2, 1) * V.Dot(w, motion), w.LengthSquared - radiusSquared, []);
            for (int j = i + 1; j < vertices.Length; j++)
            {
                V edge = vertices[j] - vertices[i];
                R denominator = edge.LengthSquared;
                if (denominator == R.Zero) continue;
                R p0 = V.Dot(w, edge), p1 = V.Dot(motion, edge);
                V r0 = w * denominator - edge * p0, r1 = motion * denominator - edge * p1;
                Offer(r1.LengthSquared, new R(2, 1) * V.Dot(r0, r1),
                    r0.LengthSquared - radiusSquared * denominator.Square(),
                    [(p0, p1), (denominator - p0, -p1)]);
            }
        }
        bool inside = fullDimensional;
        foreach (var plane in planes)
            inside &= V.Dot(start - plane.Anchor, plane.Normal) <= R.Zero;
        if (inside) bestLower = bestUpper = R.Zero;
        Assert.NotNull(bestLower);
        Assert.NotNull(bestUpper);
        I norm = Root(motion.LengthSquared);
        return (Down(bestLower.Value * norm.Lower), Up(bestUpper.Value * norm.Upper));

        void Offer(R a, R b, R c, (R Constant, R Slope)[] constraints)
        {
            R lo = R.Zero, hi = R.One;
            foreach (var condition in constraints)
            {
                if (condition.Slope == R.Zero)
                {
                    if (condition.Constant < R.Zero) return;
                    continue;
                }
                R boundary = -condition.Constant / condition.Slope;
                if (condition.Slope > R.Zero) { if (boundary > lo) lo = boundary; }
                else if (boundary < hi) hi = boundary;
            }
            if (lo > hi) return;
            R Evaluate(R t) => (a * t + b) * t + c;
            if (Evaluate(lo) > R.Zero)
            {
                R minimum = a == R.Zero ? hi : -b / (new R(2, 1) * a);
                if (minimum < lo) minimum = lo;
                if (minimum > hi) minimum = hi;
                if (Evaluate(minimum) > R.Zero) return;
                hi = minimum;
                // Exact convexity and the attained nonpositive minimum prove a root exists.
                for (int depth = 0; depth < 52; depth++)
                {
                    R middle = (lo + hi) / new R(2, 1);
                    if (Evaluate(middle) > R.Zero) lo = middle;
                    else hi = middle;
                }
                // A rational zero attained on the decreasing branch is the exact first
                // event. Do not turn that exact answer into a wider oracle-only bracket.
                if (Evaluate(hi) == R.Zero) lo = hi;
            }
            else hi = lo;
            if (bestLower is not R lower || lo < lower) bestLower = lo;
            if (bestUpper is not R upper || hi < upper) bestUpper = hi;
        }
    }

    static double Down(R value)
    {
        double rounded = (double)value.Numerator / (double)value.Denominator;
        while (R.From(rounded) > value) rounded = Math.BitDecrement(rounded);
        return rounded;
    }

    static double Up(R value)
    {
        double rounded = (double)value.Numerator / (double)value.Denominator;
        while (R.From(rounded) < value) rounded = Math.BitIncrement(rounded);
        return rounded;
    }
}
