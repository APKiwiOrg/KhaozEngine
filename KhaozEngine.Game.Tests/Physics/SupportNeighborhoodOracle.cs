using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuCompound = BepuPhysics.Collidables.Compound;
using BepuHull = BepuPhysics.Collidables.ConvexHull;
using BepuSim = BepuPhysics.Simulation;

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

/// <summary>A face's separation from the probe, the closest probe axis point's height above the face plane, and
/// the face's closest point.</summary>
internal readonly record struct OracleContact(double Separation, double Height, OracleVector Witness);

/// <summary>Brute force segment and polygon distances in binary64 over the installed float geometry. Independent of
/// the backend's bounded arithmetic: it reads only installed shapes, poses and vertices.</summary>
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
        for (int i = 1; i < elements.Length; i++)
            Assert.True(elements[i - 1].Static.Value < elements[i].Static.Value ||
                (elements[i - 1].Static == elements[i].Static && elements[i - 1].ElementId < elements[i].ElementId),
                "Elements are ordered by static handle, then element id.");
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
