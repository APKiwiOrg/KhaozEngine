using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuCapsule = BepuPhysics.Collidables.Capsule;
using BepuCompound = BepuPhysics.Collidables.Compound;
using BepuCylinder = BepuPhysics.Collidables.Cylinder;
using BepuHull = BepuPhysics.Collidables.ConvexHull;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using BepuSim = BepuPhysics.Simulation;
using BepuSphere = BepuPhysics.Collidables.Sphere;
using BepuStaticDescription = BepuPhysics.StaticDescription;
using BepuStaticHandle = BepuPhysics.StaticHandle;
using BepuTypedIndex = BepuPhysics.Collidables.TypedIndex;

namespace KhaozEngine.Tests.Physics;

/// <summary>A binary64 vector. Float inputs convert exactly.</summary>
internal readonly record struct OracleVector(double X, double Y, double Z)
{
    internal static OracleVector From(Vector3 v) => new(v.X, v.Y, v.Z);
    public static OracleVector operator +(OracleVector a, OracleVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static OracleVector operator -(OracleVector a, OracleVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static OracleVector operator *(OracleVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    internal static double Dot(OracleVector a, OracleVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static OracleVector Cross(OracleVector a, OracleVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal double Length => Math.Sqrt(Dot(this, this));
    internal OracleVector Unit => this * (1 / Length);

    // v + 2w (q x v) + 2 q x (q x v) for the float quaternion as installed.
    internal static OracleVector Rotate(Quaternion q, OracleVector v)
    {
        var axis = new OracleVector(q.X, q.Y, q.Z);
        OracleVector t = Cross(axis, v) * 2;
        return v + t * q.W + Cross(axis, t);
    }
}

/// <summary>One installed face, its vertices in the backend's order and its outward unit normal.</summary>
internal sealed record OracleFace(StaticHandle Static, int ElementId, OracleVector[] Vertices)
{
    internal OracleVector Normal { get; } =
        OracleVector.Cross(Vertices[1] - Vertices[0], Vertices[2] - Vertices[0]).Unit;
}

/// <summary>Which surface of a curved leaf an oracle tangent describes. A round surface lies at the radius from a
/// core segment of the given half length along the axis, so a sphere is a round surface of half length zero.
/// Cylinder parts are side 0, top cap 1 and bottom cap 2.</summary>
internal enum OracleSurface { Round, CylinderSide, CylinderTop, CylinderBottom }

/// <summary>A tangent element's closest point to one point, its distance, the outward normal there and the point's
/// signed height above the tangent plane at the closest point.</summary>
internal readonly record struct OracleTangentPoint(double Distance, OracleVector Witness, OracleVector Normal,
    double Height);

/// <summary>One curved surface element of an installed leaf in binary64: the leaf's world centre, the unit direction
/// of its local Y axis, its radius and its half length.</summary>
internal sealed record OracleTangent(StaticHandle Static, int ElementId, OracleSurface Surface, OracleVector Centre,
    OracleVector Axis, double Radius, double HalfLength)
{
    /// <summary>The closed form closest point of this element to <paramref name="point"/>.</summary>
    internal OracleTangentPoint Closest(OracleVector point)
    {
        OracleVector offset = point - Centre;
        double along = OracleVector.Dot(offset, Axis);
        if (Surface == OracleSurface.Round)
        {
            OracleVector core = Centre + Axis * Math.Clamp(along, -HalfLength, HalfLength);
            OracleVector radial = point - core;
            double length = radial.Length;
            OracleVector normal = radial * (1 / length);
            return new(Math.Abs(length - Radius), core + normal * Radius, normal, length - Radius);
        }
        OracleVector across = offset - Axis * along;
        double distance = across.Length;
        OracleVector outward = across * (1 / distance);
        if (Surface == OracleSurface.CylinderSide)
        {
            double beyond = Math.Max(Math.Abs(along) - HalfLength, 0), height = distance - Radius;
            return new(Math.Sqrt(height * height + beyond * beyond),
                Centre + Axis * Math.Clamp(along, -HalfLength, HalfLength) + outward * Radius, outward, height);
        }
        double side = Surface == OracleSurface.CylinderTop ? 1 : -1;
        double above = side * along - HalfLength, outside = Math.Max(distance - Radius, 0);
        OracleVector rim = distance > Radius ? outward * Radius : across;
        return new(Math.Sqrt(above * above + outside * outside), Centre + Axis * (side * HalfLength) + rim,
            Axis * side, above);
    }
}

/// <summary>A face's separation from the probe, the closest probe axis point's height above the face plane, and
/// the face's closest point.</summary>
internal readonly record struct OracleContact(double Separation, double Height, OracleVector Witness);

/// <summary>Brute force segment distances to polygons and curved surfaces in binary64 over the installed float
/// geometry. Independent of the backend's bounded arithmetic: it reads only installed shapes, poses and vertices.</summary>
internal static class SupportNeighborhoodOracle
{
    /// <summary>Tolerance for binary64 evaluation of the installed float geometry against the backend enclosures.</summary>
    internal const double Geometry = 1e-6;

    // Box corner i has +X when bit 0 is set, +Y for bit 1 and +Z for bit 2. Faces are -X, +X, -Y, +Y, -Z, +Z.
    static readonly int[][] BoxFaces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];

    internal const int BoxTop = 3;
    internal const int BoxMinusX = 0;
    internal const int BoxMinusZ = 4;

    internal static List<OracleFace> Box(StaticHandle owner, Vector3 half, Pose pose)
    {
        var corners = new OracleVector[8];
        for (int i = 0; i < corners.Length; i++)
        {
            var local = new OracleVector((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
            corners[i] = OracleVector.Rotate(pose.Orientation, local) + OracleVector.From(pose.Position);
        }
        var faces = new List<OracleFace>();
        for (int face = 0; face < BoxFaces.Length; face++)
            faces.Add(new OracleFace(owner, face, Array.ConvertAll(BoxFaces[face], i => corners[i])));
        return faces;
    }

    /// <summary>The faces of the only static in <paramref name="world"/>, a convex hull in its centroid wrapper,
    /// read from the installed shape.</summary>
    internal static List<OracleFace> InstalledHull(BepuPhysicsWorld world, StaticHandle owner)
    {
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var body = ref simulation.Statics[0];
        Assert.Equal(default(BepuCompound).TypeId, body.Shape.Type);
        ref BepuCompound compound = ref simulation.Shapes.GetShape<BepuCompound>(body.Shape.Index);
        Assert.Equal(1, compound.Children.Length);
        ref var child = ref compound.Children[0];
        Assert.Equal(default(BepuHull).TypeId, child.ShapeIndex.Type);
        Assert.Equal(Quaternion.Identity, child.LocalPose.Orientation);
        ref BepuHull hull = ref simulation.Shapes.GetShape<BepuHull>(child.ShapeIndex.Index);
        var faces = new List<OracleFace>();
        for (int face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
        {
            hull.GetVertexIndicesForFace(face, out var indices);
            var vertices = new OracleVector[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                hull.GetPoint(indices[i], out Vector3 point);
                OracleVector local = OracleVector.From(child.LocalPose.Position) + OracleVector.From(point);
                vertices[i] = OracleVector.Rotate(body.Pose.Orientation, local) + OracleVector.From(body.Pose.Position);
            }
            faces.Add(new OracleFace(owner, face, vertices));
        }
        return faces;
    }

    /// <summary>The triangles of mesh static <paramref name="owner"/>, read from the installed shape and pose. Element
    /// id is the triangle index. Vertices run A, C, B so the normal is the backend's front, Cross(C - A, B - A).</summary>
    internal static List<OracleFace> InstalledMesh(BepuPhysicsWorld world, StaticHandle owner)
    {
        BepuSim simulation = Installed(world, owner, out BepuStaticDescription description);
        Assert.Equal(default(BepuMesh).TypeId, description.Shape.Type);
        ref BepuMesh mesh = ref simulation.Shapes.GetShape<BepuMesh>(description.Shape.Index);
        Assert.Equal(Vector3.One, mesh.Scale);
        OracleVector World(Vector3 local) =>
            OracleVector.Rotate(description.Pose.Orientation, OracleVector.From(local)) +
            OracleVector.From(description.Pose.Position);
        var faces = new List<OracleFace>();
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            ref var triangle = ref mesh.Triangles[i];
            faces.Add(new OracleFace(owner, i, [World(triangle.A), World(triangle.C), World(triangle.B)]));
        }
        return faces;
    }

    /// <summary>The curved surface elements of static <paramref name="owner"/>, read from the installed shapes and
    /// poses: a sphere or capsule static, or every sphere, capsule and cylinder leaf of a compound. A cylinder's leaf
    /// pose carries the base alignment lift.</summary>
    internal static List<OracleTangent> InstalledCurved(BepuPhysicsWorld world, StaticHandle owner)
    {
        BepuSim simulation = Installed(world, owner, out BepuStaticDescription description);
        var tangents = new List<OracleTangent>();
        Quaternion orientation = description.Pose.Orientation;
        OracleVector position = OracleVector.From(description.Pose.Position);
        if (description.Shape.Type != default(BepuCompound).TypeId)
        {
            AddCurved(simulation, description.Shape, owner, 0, position,
                OracleVector.Rotate(orientation, new OracleVector(0, 1, 0)).Unit, tangents);
            return tangents;
        }
        ref BepuCompound compound = ref simulation.Shapes.GetShape<BepuCompound>(description.Shape.Index);
        for (int leaf = 0; leaf < compound.Children.Length; leaf++)
        {
            var child = compound.Children[leaf];
            OracleVector centre = OracleVector.Rotate(orientation, OracleVector.From(child.LocalPose.Position)) + position;
            OracleVector axis = OracleVector.Rotate(orientation,
                OracleVector.Rotate(child.LocalPose.Orientation, new OracleVector(0, 1, 0))).Unit;
            AddCurved(simulation, child.ShapeIndex, owner, leaf, centre, axis, tangents);
        }
        return tangents;
    }

    static void AddCurved(BepuSim simulation, BepuTypedIndex shape, StaticHandle owner, int leaf, OracleVector centre,
        OracleVector axis, List<OracleTangent> tangents)
    {
        int id = leaf * TangentLeafStride;
        if (shape.Type == default(BepuSphere).TypeId)
        {
            float radius = simulation.Shapes.GetShape<BepuSphere>(shape.Index).Radius;
            tangents.Add(new(owner, id, OracleSurface.Round, centre, axis, radius, 0));
        }
        else if (shape.Type == default(BepuCapsule).TypeId)
        {
            BepuCapsule capsule = simulation.Shapes.GetShape<BepuCapsule>(shape.Index);
            tangents.Add(new(owner, id, OracleSurface.Round, centre, axis, capsule.Radius, capsule.HalfLength));
        }
        else if (shape.Type == default(BepuCylinder).TypeId)
        {
            BepuCylinder cylinder = simulation.Shapes.GetShape<BepuCylinder>(shape.Index);
            foreach (OracleSurface surface in (ReadOnlySpan<OracleSurface>)[OracleSurface.CylinderSide,
                         OracleSurface.CylinderTop, OracleSurface.CylinderBottom])
                tangents.Add(new(owner, id + (int)surface - 1, surface, centre, axis, cylinder.Radius,
                    cylinder.HalfLength));
        }
    }

    /// <summary>Tangent element ids are leaf * 256 + part, the polyhedron face stride.</summary>
    internal const int TangentLeafStride = 256;

    static BepuSim Installed(BepuPhysicsWorld world, StaticHandle owner, out BepuStaticDescription description)
    {
        FieldInfo? simulationField = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? handlesField = typeof(BepuPhysicsWorld).GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(simulationField);
        Assert.NotNull(handlesField);
        BepuSim simulation = Assert.IsType<BepuSim>(simulationField.GetValue(world));
        var handles = Assert.IsType<Dictionary<int, (BepuStaticHandle Handle, BepuTypedIndex Shape)>>(
            handlesField.GetValue(world));
        simulation.Statics.GetDescription(handles[owner.Value].Handle, out description);
        return simulation;
    }

    /// <summary>The closest point of a tangent element to the probe segment from <paramref name="lower"/> to
    /// <paramref name="upper"/>. Each point distance is closed form. The segment minimum is a dense scan followed by a
    /// golden section search around the best sample.</summary>
    internal static OracleTangentPoint TangentContact(OracleTangent element, OracleVector lower, OracleVector upper)
    {
        const int Samples = 1024;
        OracleVector At(double t) => lower + (upper - lower) * t;
        double Distance(double t) => element.Closest(At(t)).Distance;
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i <= Samples; i++)
        {
            double distance = Distance((double)i / Samples);
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }
        double a = Math.Max(0, (best - 1.0) / Samples), b = Math.Min(1, (best + 1.0) / Samples);
        double ratio = (Math.Sqrt(5) - 1) / 2;
        for (int i = 0; i < 200 && b - a > 1e-15; i++)
        {
            double c = b - (b - a) * ratio, d = a + (b - a) * ratio;
            if (Distance(c) <= Distance(d)) b = d;
            else a = c;
        }
        double t = (a + b) / 2;
        return element.Closest(At(Distance(t) <= bestDistance ? t : (double)best / Samples));
    }

    /// <summary>Every tangent whose oracle separation is inside the band by more than the geometry tolerance, and
    /// whose closest axis point is in front within the band, is a member. Every member agrees with the oracle within
    /// its published error bounds.</summary>
    internal static int AssertTangentsMatch(ReadOnlySpan<SupportElement> elements,
        IReadOnlyList<OracleTangent> tangents, OracleVector lower, OracleVector upper, double radius, double band)
    {
        AssertOrdered(elements);
        int required = 0;
        foreach (OracleTangent tangent in tangents)
        {
            OracleTangentPoint contact = TangentContact(tangent, lower, upper);
            double separation = contact.Distance - radius;
            if (separation > band - Geometry || contact.Height < -band + Geometry) continue;
            required++;
            Assert.True(IndexOf(elements, tangent) >= 0,
                $"Tangent {tangent.ElementId} of static {tangent.Static.Value} at separation {separation} is missing.");
        }
        foreach (SupportElement element in elements)
        {
            OracleTangent? tangent = null;
            foreach (OracleTangent candidate in tangents)
                if (candidate.Static == element.Static && candidate.ElementId == element.ElementId) tangent = candidate;
            Assert.NotNull(tangent);
            OracleTangentPoint contact = TangentContact(tangent, lower, upper);
            double separation = contact.Distance - radius;
            Assert.Equal(SupportElementKind.Tangent, element.Kind);
            Assert.True(element.SeparationLower <= band, "A member's separation lower bound reaches the band.");
            Assert.True(element.SeparationLower <= element.SeparationUpper);
            Assert.True(separation <= band + element.PositionErrorMetres + Geometry,
                $"Member {element.ElementId} lies at oracle separation {separation}.");
            Assert.InRange(separation, element.SeparationLower - Geometry, element.SeparationUpper + Geometry);
            Assert.True((OracleVector.From(element.Normal) - contact.Normal).Length <= element.NormalError + Geometry,
                "The published normal error encloses the installed surface normal.");
            Assert.True((OracleVector.From(element.Witness) - contact.Witness).Length <=
                element.PositionErrorMetres + Geometry, "The published witness error encloses the closest point.");
        }
        return required;
    }

    internal static int IndexOf(ReadOnlySpan<SupportElement> elements, OracleTangent tangent)
    {
        for (int i = 0; i < elements.Length; i++)
            if (elements[i].Static == tangent.Static && elements[i].ElementId == tangent.ElementId) return i;
        return -1;
    }

    static void AssertOrdered(ReadOnlySpan<SupportElement> elements)
    {
        for (int i = 1; i < elements.Length; i++)
            Assert.True(elements[i - 1].Static.Value < elements[i].Static.Value ||
                (elements[i - 1].Static == elements[i].Static && elements[i - 1].ElementId < elements[i].ElementId),
                "Elements are ordered by static handle, then element id.");
    }

    internal static OracleContact Contact(OracleFace face, OracleVector lower, OracleVector upper, double radius)
    {
        (double distance, OracleVector axis, OracleVector point) = Closest(lower, upper, face);
        return new(distance - radius, OracleVector.Dot(face.Normal, axis - face.Vertices[0]), point);
    }

    /// <summary>Every face whose oracle separation is inside the band by more than the geometry tolerance, and
    /// whose closest axis point is in front within the band, is a member. Every member agrees with the oracle
    /// within its published error bounds.</summary>
    internal static int AssertMatches(ReadOnlySpan<SupportElement> elements, IReadOnlyList<OracleFace> faces,
        OracleVector lower, OracleVector upper, double radius, double band)
    {
        AssertOrdered(elements);
        int required = 0;
        foreach (OracleFace face in faces)
        {
            OracleContact contact = Contact(face, lower, upper, radius);
            if (contact.Separation > band - Geometry || contact.Height < -band + Geometry) continue;
            required++;
            Assert.True(IndexOf(elements, face) >= 0,
                $"Face {face.ElementId} of static {face.Static.Value} at separation {contact.Separation} is missing.");
        }
        foreach (SupportElement element in elements)
        {
            OracleFace? face = null;
            foreach (OracleFace candidate in faces)
                if (candidate.Static == element.Static && candidate.ElementId == element.ElementId) face = candidate;
            Assert.NotNull(face);
            OracleContact contact = Contact(face, lower, upper, radius);
            Assert.Equal(SupportElementKind.Polygon, element.Kind);
            Assert.True(element.SeparationLower <= band, "A member's separation lower bound reaches the band.");
            Assert.True(element.SeparationLower <= element.SeparationUpper);
            Assert.True(contact.Separation <= band + element.PositionErrorMetres + Geometry,
                $"Member {element.ElementId} lies at oracle separation {contact.Separation}.");
            Assert.InRange(contact.Separation, element.SeparationLower - Geometry, element.SeparationUpper + Geometry);
            Assert.True((OracleVector.From(element.Normal) - face.Normal).Length <= element.NormalError + Geometry,
                "The published normal error encloses the installed face normal.");
            Assert.True((OracleVector.From(element.Witness) - contact.Witness).Length <=
                element.PositionErrorMetres + Geometry, "The published witness error encloses the closest point.");
        }
        return required;
    }

    internal static int IndexOf(ReadOnlySpan<SupportElement> elements, OracleFace face)
    {
        for (int i = 0; i < elements.Length; i++)
            if (elements[i].Static == face.Static && elements[i].ElementId == face.ElementId) return i;
        return -1;
    }

    static (double Distance, OracleVector Axis, OracleVector Point) Closest(OracleVector p, OracleVector q,
        OracleFace face)
    {
        OracleVector origin = face.Vertices[0], n = face.Normal;
        double dp = OracleVector.Dot(n, p - origin), dq = OracleVector.Dot(n, q - origin);
        if (dp != dq && ((dp <= 0 && dq >= 0) || (dp >= 0 && dq <= 0)))
        {
            OracleVector crossing = p + (q - p) * (dp / (dp - dq));
            if (Inside(face, crossing)) return (0, crossing, crossing);
        }
        (double Distance, OracleVector Axis, OracleVector Point) best = (double.PositiveInfinity, p, origin);
        foreach (OracleVector end in (ReadOnlySpan<OracleVector>)[p, q])
        {
            OracleVector projected = end - n * OracleVector.Dot(n, end - origin);
            double distance = (end - projected).Length;
            if (Inside(face, projected) && distance < best.Distance) best = (distance, end, projected);
        }
        for (int i = 0; i < face.Vertices.Length; i++)
        {
            (OracleVector axis, OracleVector point) = SegmentSegment(p, q, face.Vertices[i],
                face.Vertices[(i + 1) % face.Vertices.Length]);
            double distance = (axis - point).Length;
            if (distance < best.Distance) best = (distance, axis, point);
        }
        return best;
    }

    static bool Inside(OracleFace face, OracleVector point)
    {
        for (int i = 0; i < face.Vertices.Length; i++)
        {
            OracleVector a = face.Vertices[i], b = face.Vertices[(i + 1) % face.Vertices.Length];
            if (OracleVector.Dot(OracleVector.Cross(b - a, point - a), face.Normal) < 0) return false;
        }
        return true;
    }

    // The closest points of two segments, after Ericson, Real-Time Collision Detection 5.1.9.
    static (OracleVector First, OracleVector Second) SegmentSegment(OracleVector p1, OracleVector q1,
        OracleVector p2, OracleVector q2)
    {
        OracleVector d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        double a = OracleVector.Dot(d1, d1), e = OracleVector.Dot(d2, d2), f = OracleVector.Dot(d2, r);
        double s, t;
        if (a == 0 && e == 0) { s = 0; t = 0; }
        else if (a == 0) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = OracleVector.Dot(d1, r);
            if (e == 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = OracleVector.Dot(d1, d2), denominator = a * e - b * b;
                s = denominator != 0 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s, p2 + d2 * t);
    }
}
