using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Mass, woven stiffnesses, bending modulus, damping time, thickness and dry friction.</summary>
/// <remarks>Density is in kg/m², extension and shear in N/m, bending in N m, damping in seconds
/// and half-thickness in metres. Friction coefficients are dimensionless. Optional still-air drag is in kg/(m² s).</remarks>
public readonly record struct ClothMaterial(float Density, float WarpStiffness, float WeftStiffness,
    float ShearStiffness, float BendModulus, float DampingSeconds, float HalfThickness,
    float StaticFriction, float KineticFriction, float NormalAirDrag = 0);

/// <summary>Three node indices in material winding order.</summary>
public readonly record struct ClothTriangle(int A, int B, int C)
{
    internal Vector3 Blend(ReadOnlySpan<Vector3> points, Vector3 weights) =>
        weights.X * points[A] + weights.Y * points[B] + weights.Z * points[C];
}

/// <summary>An optional natural angle in radians about an interior edge, ordered by increasing node index.</summary>
public readonly record struct ClothRestHinge(int First, int Second, float NaturalAngle);

public enum ClothRigRefusal
{
    Empty,
    InvalidNode,
    InvalidTriangle,
    InvalidWinding,
    NonManifoldEdge,
    UnusedNode,
    InvalidPin,
    UnpinnedComponent,
    InvalidHinge,
    NonManifoldVertex,
}

/// <summary>A malformed cloth pattern with the index of the affected input element.</summary>
public sealed class ClothRigException : ArgumentException
{
    internal ClothRigException(ClothRigRefusal reason, int elementIndex, string message) : base(message) =>
        (Reason, ElementIndex) = (reason, elementIndex);

    public ClothRigRefusal Reason { get; }
    public int ElementIndex { get; }
}

/// <summary>Owned material topology, mass and natural curvature shared by clamped or free cloth instances.</summary>
public sealed class ClothRig
{
    private readonly Vector3[] _rest;
    private readonly Vector2[] _coordinates;
    private readonly ClothTriangle[] _triangles;
    private readonly int[] _pins;

    private ClothRig(ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector2> coordinates,
        ReadOnlySpan<ClothTriangle> triangles, ReadOnlySpan<int> pins, ClothMaterial material,
        ClothMembrane[] membranes, ClothHinge[] hinges, float[] inverseMasses)
    {
        _rest = rest.ToArray();
        _coordinates = coordinates.ToArray();
        _triangles = triangles.ToArray();
        _pins = pins.ToArray();
        Material = material;
        Membranes = membranes;
        Hinges = hinges;
        InverseMasses = inverseMasses;
    }

    public ReadOnlySpan<Vector3> RestPositions => _rest;
    public ReadOnlySpan<Vector2> MaterialCoordinates => _coordinates;
    public ReadOnlySpan<ClothTriangle> Triangles => _triangles;
    public ReadOnlySpan<int> Pins => _pins;
    public ClothMaterial Material { get; }
    internal ClothMembrane[] Membranes { get; }
    internal ClothHinge[] Hinges { get; }
    internal float[] InverseMasses { get; }

    /// <summary>Copies and validates a material pattern in metres. Coordinates run weft, then warp.
    /// An empty pin span describes free cloth. An omitted interior hinge uses its rest geometry.</summary>
    public static ClothRig Create(ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector2> coordinates,
        ReadOnlySpan<ClothTriangle> triangles, ReadOnlySpan<int> pins, ReadOnlySpan<ClothRestHinge> restHinges,
        ClothMaterial material)
    {
        ValidateMaterial(material);
        if (rest.Length == 0 || triangles.Length == 0)
            throw new ClothRigException(ClothRigRefusal.Empty, -1, "Cloth requires nodes and triangles.");
        if (coordinates.Length != rest.Length)
            throw new ArgumentException("Each rest node requires a material coordinate.", nameof(coordinates));

        var topology = new ClothRigTopology(rest, coordinates, triangles, pins, restHinges, material);
        return new ClothRig(rest, coordinates, triangles, pins, material, topology.Membranes,
            topology.Hinges, topology.InverseMasses);
    }

    private static void ValidateMaterial(ClothMaterial material)
    {
        Positive(material.Density, nameof(material.Density));
        Positive(material.WarpStiffness, nameof(material.WarpStiffness));
        Positive(material.WeftStiffness, nameof(material.WeftStiffness));
        Positive(material.ShearStiffness, nameof(material.ShearStiffness));
        Positive(material.BendModulus, nameof(material.BendModulus));
        Positive(material.HalfThickness, nameof(material.HalfThickness));
        ArgumentOutOfRangeException.ThrowIfLessThan(material.DampingSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(material.NormalAirDrag, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(material.KineticFriction, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(material.StaticFriction, material.KineticFriction);
        if (!float.IsFinite(material.DampingSeconds) || !float.IsFinite(material.StaticFriction) ||
            !float.IsFinite(material.KineticFriction) || !float.IsFinite(material.NormalAirDrag))
            throw new ArgumentOutOfRangeException(nameof(material));

        static void Positive(float value, string name)
        {
            if (!float.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(name, value, "A positive finite value is required.");
        }
    }
}

internal sealed class ClothRigTopology
{
    private readonly record struct Edge(int First, int Second);
    private record struct Neighbours(int Left, int Right)
    {
        public Neighbours() : this(-1, -1) { }
    }

    internal ClothRigTopology(ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector2> coordinates,
        ReadOnlySpan<ClothTriangle> triangles, ReadOnlySpan<int> pins, ReadOnlySpan<ClothRestHinge> restHinges,
        ClothMaterial material)
    {
        var edges = new Dictionary<Edge, Neighbours>();
        var fans = new List<(int First, int Second)>?[rest.Length];
        float[] mass = new float[rest.Length];
        int[] components = new int[rest.Length];
        Membranes = new ClothMembrane[triangles.Length];
        for (int i = 0; i < rest.Length; i++)
        {
            components[i] = i;
            if (!ChainMath.IsFinite(rest[i]) || !float.IsFinite(coordinates[i].X) || !float.IsFinite(coordinates[i].Y))
                throw new ClothRigException(ClothRigRefusal.InvalidNode, i, "The rest node or material coordinate is nonfinite.");
        }

        for (int i = 0; i < triangles.Length; i++)
        {
            ClothTriangle face = triangles[i];
            if ((uint)face.A >= (uint)rest.Length || (uint)face.B >= (uint)rest.Length ||
                (uint)face.C >= (uint)rest.Length || !ClothMembrane.TryCreate(face, rest, coordinates, material,
                    out ClothMembrane membrane, out float area))
                throw new ClothRigException(ClothRigRefusal.InvalidTriangle, i, "The material triangle has invalid nodes or a degenerate frame.");

            Membranes[i] = membrane;
            float tributaryMass = material.Density * area / 3;
            mass[face.A] += tributaryMass;
            mass[face.B] += tributaryMass;
            mass[face.C] += tributaryMass;
            Connect(face.A, face.B, face.C, i);
            Connect(face.B, face.C, face.A, i);
            Connect(face.C, face.A, face.B, i);
            (fans[face.A] ??= []).Add((face.B, face.C));
            (fans[face.B] ??= []).Add((face.C, face.A));
            (fans[face.C] ??= []).Add((face.A, face.B));
            components[Find(components, face.B)] = Find(components, face.A);
            components[Find(components, face.C)] = Find(components, face.A);
        }

        ValidateVertexLinks(fans);
        InverseMasses = MakeInverseMasses(mass, pins, components);
        Hinges = MakeHinges(rest, restHinges, material, edges);

        void Connect(int a, int b, int opposite, int faceIndex)
        {
            var edge = new Edge(Math.Min(a, b), Math.Max(a, b));
            if (!edges.TryGetValue(edge, out Neighbours neighbours))
                neighbours = new Neighbours();
            if (neighbours.Left >= 0 && neighbours.Right >= 0)
                throw new ClothRigException(ClothRigRefusal.NonManifoldEdge, faceIndex, "An edge has more than two incident triangles.");

            if (neighbours.Left == opposite || neighbours.Right == opposite)
                throw new ClothRigException(ClothRigRefusal.InvalidTriangle, faceIndex, "A material triangle cannot be repeated.");

            bool left = a == edge.First;
            if (left ? neighbours.Left >= 0 : neighbours.Right >= 0)
                throw new ClothRigException(ClothRigRefusal.InvalidWinding, faceIndex, "Adjacent triangles need opposite winding along their shared edge.");

            if (left) neighbours.Left = opposite;
            else neighbours.Right = opposite;
            edges[edge] = neighbours;
        }
    }

    internal ClothMembrane[] Membranes { get; }
    internal ClothHinge[] Hinges { get; }
    internal float[] InverseMasses { get; }

    private static void ValidateVertexLinks(List<(int First, int Second)>?[] fans)
    {
        // Edge incidence already bounds each link's degree to two. A manifold
        // vertex therefore needs one connected path or cycle in its link.
        var parents = new Dictionary<int, int>();
        for (int node = 0; node < fans.Length; node++)
        {
            if (fans[node] is not { Count: > 1 } fan) continue;

            parents.Clear();
            int components = 0;
            foreach ((int first, int second) in fan)
            {
                if (parents.TryAdd(first, first)) components++;
                if (parents.TryAdd(second, second)) components++;
                int rootFirst = Root(parents, first), rootSecond = Root(parents, second);
                if (rootFirst == rootSecond) continue;

                parents[rootSecond] = rootFirst;
                components--;
            }
            if (components != 1)
                throw new ClothRigException(ClothRigRefusal.NonManifoldVertex, node, "A material node needs one connected face fan.");
        }

        static int Root(Dictionary<int, int> link, int node)
        {
            while (link[node] != node)
            {
                link[node] = link[link[node]];
                node = link[node];
            }
            return node;
        }
    }

    private static int Find(int[] components, int node)
    {
        while (components[node] != node)
        {
            components[node] = components[components[node]];
            node = components[node];
        }
        return node;
    }

    private static float[] MakeInverseMasses(float[] mass, ReadOnlySpan<int> pins, int[] components)
    {
        var pinned = new bool[mass.Length];
        var fixedComponents = new bool[mass.Length];
        for (int i = 0; i < pins.Length; i++)
        {
            int node = pins[i];
            if ((uint)node >= (uint)mass.Length || pinned[node])
                throw new ClothRigException(ClothRigRefusal.InvalidPin, i, "Pins must name distinct existing nodes.");

            pinned[node] = true;
            fixedComponents[Find(components, node)] = true;
        }

        var inverse = new float[mass.Length];
        for (int i = 0; i < inverse.Length; i++)
        {
            if (mass[i] <= 0 || !float.IsFinite(mass[i]) || !float.IsFinite(1 / mass[i]))
                throw new ClothRigException(ClothRigRefusal.UnusedNode, i, "Each node requires finite, positive area mass.");
            if (pins.Length != 0 && !fixedComponents[Find(components, i)])
                throw new ClothRigException(ClothRigRefusal.UnpinnedComponent, i, "An anchored rig requires a pin in every material component.");

            inverse[i] = pinned[i] ? 0 : 1 / mass[i];
        }
        return inverse;
    }

    private static ClothHinge[] MakeHinges(ReadOnlySpan<Vector3> rest, ReadOnlySpan<ClothRestHinge> restHinges,
        ClothMaterial material, Dictionary<Edge, Neighbours> edges)
    {
        var overrides = new Dictionary<Edge, float>();
        for (int i = 0; i < restHinges.Length; i++)
        {
            ClothRestHinge hinge = restHinges[i];
            var edge = new Edge(hinge.First, hinge.Second);
            if (!edges.TryGetValue(edge, out Neighbours neighbours) || neighbours.Left < 0 || neighbours.Right < 0 ||
                !float.IsFinite(hinge.NaturalAngle) || MathF.Abs(hinge.NaturalAngle) > MathF.PI ||
                !overrides.TryAdd(edge, hinge.NaturalAngle))
                throw new ClothRigException(ClothRigRefusal.InvalidHinge, i, "An override needs a distinct ordered interior edge and angle in [-π, π].");
        }

        var hinges = new List<ClothHinge>();
        foreach ((Edge edge, Neighbours neighbours) in edges)
        {
            if (neighbours.Left < 0 || neighbours.Right < 0) continue;
            float? angle = overrides.TryGetValue(edge, out float value) ? value : null;
            hinges.Add(ClothHinge.Create(edge.First, edge.Second, neighbours.Left, neighbours.Right, rest,
                material.BendModulus, angle));
        }
        return hinges.ToArray();
    }
}
