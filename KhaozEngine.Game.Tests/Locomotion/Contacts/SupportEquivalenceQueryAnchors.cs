using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

internal sealed record SupportEquivalenceQueryAnchor(string Row, Pose Pose, float Radius, float Length, float Band,
    Vector2 Axis, float Slope, float AlternateSlope, int? TargetId, string SceneDigest);

// Owns the historical questions independently of the support geometry oracle. The fixture has only integer
// float encodings. Capture still compares the original answer fixture and never enables digest replacement.
internal sealed class SupportEquivalenceQueryAnchors(bool verifyOnly = false)
{
    const string CaptureVariable = "KHAOZENGINE_SUPPORT_ANCHOR_CAPTURE";
    const string FixtureName = "support-equivalence-queries.json";
    readonly List<Bits> _observed = [];
    readonly Dictionary<IPhysicsWorld, string> _scenes = [];
    static readonly Lazy<Dictionary<string, Bits>> Frozen = new(() =>
        JsonSerializer.Deserialize<Bits[]>(File.ReadAllText(FixturePath))!.ToDictionary(row => row.Row));
    internal bool VerifyOnly { get; } = verifyOnly;
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts", "Fixtures", FixtureName);
    internal static SupportEquivalenceQueryAnchor Read(string row) => Frozen.Value[row].Decode();

    internal StaticHandle AddStatic(BepuPhysicsWorld world, PhysicsShape shape, Pose pose)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WritePose(writer, pose);
        WriteShape(writer, shape);
        writer.Flush();
        _scenes.Add(world, Convert.ToHexString(SHA256.HashData(stream.ToArray())));
        return world.AddStatic(shape, pose);
    }

    internal SupportEquivalenceQueryAnchor Resolve(string row, IPhysicsWorld world, CapsuleShape probe,
        Pose pose, int? targetId = null)
    {
        var generated = new SupportEquivalenceQueryAnchor(row, pose, probe.Radius, probe.Length,
            SupportCertification.ContactBand, new(pose.Position.X, pose.Position.Z), 0.70710677f, 0.5f,
            targetId, _scenes[world]);
        Bits bits = Bits.Encode(generated);
        _observed.Add(bits);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CaptureVariable))) return generated;
        Bits frozen = Frozen.Value[row];
        Assert.Equal(frozen.SceneDigest, bits.SceneDigest);
        if (VerifyOnly) Assert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(bits));
        return frozen.Decode();
    }

    internal void Complete()
    {
        Assert.Equal(1952, _observed.Count);
        Assert.Equal(_observed.Count, _observed.Select(row => row.Row).Distinct().Count());
        string? capture = Environment.GetEnvironmentVariable(CaptureVariable);
        if (!string.IsNullOrEmpty(capture))
            File.WriteAllText(CapturePath(capture), JsonSerializer.Serialize(_observed, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        else Assert.Equal(Frozen.Value.Keys.Order(), _observed.Select(row => row.Row).Order());
    }

    internal static string CapturePath(string path) => Path.GetFullPath(path, Path.GetFullPath("../../../../", AppContext.BaseDirectory));

    const float Band = SupportCertification.ContactBand;
    static readonly CapsuleShape[] Probes = [new(0.01f, 0.01f), new(0.2f, 0.01f)];

    internal void Generate(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe)
    {
        Polyhedra(observe, this);
        Meshes(observe, this);
        Curved(observe, this);
        Fans(observe, this);
        Features(observe, this);
    }

    void Visit(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, string row,
        IPhysicsWorld world, IPhysicsQueryLease lease, CapsuleShape probe, OracleVector lowest, int? target = null)
    {
        SupportEquivalenceQueryAnchor query = Resolve(row, world, probe,
            Pose.At(new Vector3((float)lowest.X, (float)(lowest.Y + 0.01f * 0.5), (float)lowest.Z)), target);
        if (!VerifyOnly) observe(query, world, lease);
    }

    void Visit(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, string row,
        IPhysicsWorld world, IPhysicsQueryLease lease, StaticHandle target, CapsuleShape probe, OracleVector lowest) =>
        Visit(observe, row, world, lease, probe, lowest, target.Value);

    // This is deliberately the historical real-quaternion expression and operation order from 28273267c.
    // It belongs only to query generation, never to the represented support expected-geometry function.
    static OracleVector HistoricalRotate(Quaternion q, OracleVector v)
    {
        var axis = new OracleVector(q.X, q.Y, q.Z);
        OracleVector t = OracleVector.Cross(axis, v) * 2;
        return v + t * q.W + OracleVector.Cross(axis, t);
    }
    static OracleVector HistoricalPoint(Pose pose, Vector3 local) =>
        HistoricalRotate(pose.Orientation, OracleVector.From(local)) + OracleVector.From(pose.Position);
    static OracleVector HistoricalHull(Pose root, Pose child, Vector3 local) =>
        HistoricalRotate(root.Orientation, OracleVector.From(child.Position) + OracleVector.From(local)) + OracleVector.From(root.Position);
    static (OracleVector, OracleVector) HistoricalCurved(Pose root, Pose? child)
    {
        OracleVector position = OracleVector.From(root.Position);
        if (child is not { } part) return (position, HistoricalRotate(root.Orientation, new(0, 1, 0)).Unit);
        return (HistoricalRotate(root.Orientation, OracleVector.From(part.Position)) + position,
            HistoricalRotate(root.Orientation, HistoricalRotate(part.Orientation, new(0, 1, 0))).Unit);
    }

    // Random boxes and hulls under random rotations, each probe placed against a random face point, both radii.
    static void Polyhedra(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, SupportEquivalenceQueryAnchors anchors)
    {
        var random = new Random(6438);
        for (int solid = 0; solid < 40; solid++)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            Quaternion rotation = RandomRotation(random);
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            List<OracleFace> faces;
            if (solid % 2 == 0)
            {
                var half = new Vector3(Between(random, 0.15, 0.8), Between(random, 0.15, 0.8), Between(random, 0.15, 0.8));
                var pose = new Pose(position, rotation);
                faces = SupportNeighborhoodOracle.Box(anchors.AddStatic(world, new BoxShape(half), pose), half, pose, HistoricalPoint);
            }
            else
            {
                var points = new Vector3[10];
                float scale = Between(random, 0.3, 0.8);
                for (int i = 0; i < points.Length; i++)
                {
                    OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                    points[i] = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z) * scale;
                }
                StaticHandle hull = anchors.AddStatic(world, new ConvexHullShape(points), new Pose(position, rotation));
                faces = SupportNeighborhoodOracle.InstalledHull(world, hull, HistoricalHull);
            }
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 10; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                OracleVector target = HistoricalSurfacePoint(random, face);
                OracleVector anchor = Outward(random, target, face.Normal);
                foreach (CapsuleShape probe in Probes)
                    anchors.Visit(observe, $"poly/{solid}/{sample}/{probe.Radius}", world, lease, probe,
                        anchor + face.Normal * probe.Radius);
            }
        }
    }

    // Random heightfields, level or under any rotation, each probe placed against a random triangle point.
    static void Meshes(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, SupportEquivalenceQueryAnchors anchors)
    {
        var random = new Random(7438);
        for (int field = 0; field < 20; field++)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            Vector3[] vertices = Heightfield(random, 4, Between(random, 0.2, 0.5), Between(random, 0.05, 0.3));
            Quaternion rotation = field % 2 == 0 ? Quaternion.Identity : RandomRotation(random);
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            StaticHandle mesh = anchors.AddStatic(world, new TriangleMeshShape(vertices,
                [.. Enumerable.Range(0, vertices.Length)]), new Pose(position, rotation));
            List<OracleFace> faces = SupportNeighborhoodOracle.InstalledMesh(world, mesh, HistoricalPoint);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 10; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                OracleVector target = HistoricalSurfacePoint(random, face);
                OracleVector anchor = Outward(random, target, face.Normal);
                foreach (CapsuleShape probe in Probes)
                {
                    // The axis end nearer the face's plane takes the point, so the whole axis lies in front of it.
                    OracleVector lowest = anchor + face.Normal * probe.Radius;
                    if (face.Normal.Y < 0) lowest -= new OracleVector(0, probe.Length, 0);
                    anchors.Visit(observe, $"mesh/{field}/{sample}/{probe.Radius}", world, lease, probe, lowest);
                }
            }
        }
    }

    // Spheres, capsules and cylinders, every other one a compound leaf under its own rotated pose.
    static void Curved(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, SupportEquivalenceQueryAnchors anchors)
    {
        var random = new Random(8331);
        for (int solid = 0; solid < 30; solid++)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            Quaternion rotation = RandomRotation(random);
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            float radius = Between(random, 0.25, 0.8), length = Between(random, 0.2, 1.5);
            PhysicsShape shape = (solid % 3) switch
            {
                0 => new SphereShape(radius),
                1 => new CapsuleShape(radius, length),
                _ => new CylinderShape(radius, length),
            };
            if (solid % 2 == 1)
                shape = new CompoundShape([new(shape, new Pose(new Vector3(Signed(random), Signed(random),
                    Signed(random)) * 0.5f, RandomRotation(random)))]);
            StaticHandle handle = anchors.AddStatic(world, shape, new Pose(position, rotation));
            List<OracleTangent> tangents = SupportNeighborhoodOracle.InstalledCurved(world, handle, HistoricalCurved);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 10; sample++)
            {
                OracleTangent element = tangents[random.Next(tangents.Count)];
                // A point on the element's surface along a random direction from its core, and the outward normal.
                OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                double along = element.HalfLength * Signed(random);
                OracleVector inside = element.Centre + element.Axis * along;
                OracleTangentPoint surface = element.Closest(inside + direction * (2 * element.Radius + 1));
                OracleVector anchor = Outward(random, surface.Witness, surface.Normal);
                foreach (CapsuleShape probe in Probes)
                    anchors.Visit(observe, $"curved/{solid}/{sample}/{probe.Radius}", world, lease, probe,
                        anchor + surface.Normal * probe.Radius);
            }
        }
    }

    // Flat fans of up-facing triangles meeting at the origin, with a square rim of half size 1. A probe at the apex
    // meets every triangle, which is the join pass's worst case below capacity and a capacity refusal above it.
    static void Fans(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, SupportEquivalenceQueryAnchors anchors)
    {
        foreach (int triangles in new[] { 96, 300 })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            anchors.AddStatic(world, new TriangleMeshShape(Fan(triangles), [.. Enumerable.Range(0, 3 * triangles)]),
                Pose.Identity);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            foreach (CapsuleShape probe in Probes)
            {
                anchors.Visit(observe, $"fan/{triangles}/apex/{probe.Radius}", world, lease, probe,
                    new OracleVector(0, 0, 0));
                anchors.Visit(observe, $"fan/{triangles}/side/{probe.Radius}", world, lease, probe,
                    new OracleVector(0.5, 0, 0.25));
            }
        }
    }

    // The rim walks the square from +X towards +Z in steps of 2 / (triangles / 4), so every coordinate is a correctly
    // rounded quotient. Triangle (origin, rim i, rim i + 1) has the front normal Cross(C - A, B - A) = +Y.
    static Vector3[] Fan(int triangles)
    {
        int side = triangles / 4;
        Vector3 Rim(int i)
        {
            i %= triangles;
            float u = (float)(-1 + 2.0 * (i % side) / side);
            return (i / side) switch
            {
                0 => new Vector3(1, 0, u),
                1 => new Vector3(-u, 0, 1),
                2 => new Vector3(-1, 0, -u),
                _ => new Vector3(u, 0, -1),
            };
        }
        var vertices = new Vector3[3 * triangles];
        for (int i = 0; i < triangles; i++)
        {
            vertices[3 * i] = Vector3.Zero;
            vertices[3 * i + 1] = Rim(i);
            vertices[3 * i + 2] = Rim(i + 1);
        }
        return vertices;
    }

    // The closest-feature query over random boxes, hulls and heightfields, unrotated or under random rotations, each
    // probe placed within the band of a random face point.
    static void Features(Action<SupportEquivalenceQueryAnchor, IPhysicsWorld, IPhysicsQueryLease> observe, SupportEquivalenceQueryAnchors anchors)
    {
        var random = new Random(9438);
        for (int solid = 0; solid < 18; solid++)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            // Every other triple is unrotated, where most closest features are decided exactly.
            Quaternion rotation = solid / 3 % 2 == 0 ? Quaternion.Identity : RandomRotation(random);
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            var pose = new Pose(position, rotation);
            StaticHandle target;
            List<OracleFace> faces;
            switch (solid % 3)
            {
                case 0:
                    var half = new Vector3(Between(random, 0.15, 0.8), Between(random, 0.15, 0.8),
                        Between(random, 0.15, 0.8));
                    target = anchors.AddStatic(world, new BoxShape(half), pose);
                    faces = SupportNeighborhoodOracle.Box(target, half, pose, HistoricalPoint);
                    break;
                case 1:
                    var points = new Vector3[10];
                    float scale = Between(random, 0.3, 0.8);
                    for (int i = 0; i < points.Length; i++)
                    {
                        OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                        points[i] = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z) * scale;
                    }
                    target = anchors.AddStatic(world, new ConvexHullShape(points), pose);
                    faces = SupportNeighborhoodOracle.InstalledHull(world, target, HistoricalHull);
                    break;
                default:
                    Vector3[] vertices = Heightfield(random, 4, Between(random, 0.2, 0.5), Between(random, 0.05, 0.3));
                    target = anchors.AddStatic(world, new TriangleMeshShape(vertices, [.. Enumerable.Range(0, vertices.Length)]),
                        pose);
                    faces = SupportNeighborhoodOracle.InstalledMesh(world, target, HistoricalPoint);
                    break;
            }
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 4; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                // Within the band, where the closest feature is decided rather than absent.
                OracleVector anchor = Outward(random, HistoricalSurfacePoint(random, face), face.Normal,
                    0, Band);
                foreach (CapsuleShape probe in Probes)
                {
                    OracleVector lowest = anchor + face.Normal * probe.Radius;
                    if (face.Normal.Y < 0) lowest -= new OracleVector(0, probe.Length, 0);
                    anchors.Visit(observe, $"feature/{solid}/{sample}/{probe.Radius}", world, lease, target, probe, lowest);
                }
            }
        }
    }

    static OracleVector HistoricalSurfacePoint(Random random, OracleFace face)
    {
        OracleVector[] vertices = face.Vertices;
        switch (random.Next(3))
        {
            case 0: return vertices[random.Next(vertices.Length)];
            case 1:
                int edge = random.Next(vertices.Length);
                return vertices[edge] + (vertices[(edge + 1) % vertices.Length] - vertices[edge]) * random.NextDouble();
            default:
                OracleVector sum = default;
                double total = 0;
                foreach (OracleVector vertex in vertices)
                {
                    double weight = random.NextDouble() + 0.01;
                    sum += vertex * weight;
                    total += weight;
                }
                return sum * (1 / total);
        }
    }

    // Offset from a surface point along its outward normal, tilted at random, by a gap from one band inside to
    // three bands out by default, so both edges of the band are crossed. The caller adds the probe radius along the
    // normal.
    static OracleVector Outward(Random random, OracleVector target, OracleVector normal, double low = -Band,
        double high = 3 * Band)
    {
        OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.2;
        double gap = Between(random, low, high);
        return target + tilt * Band + normal * gap;
    }

    static Quaternion RandomRotation(Random random) => Quaternion.Normalize(new Quaternion(Signed(random),
        Signed(random), Signed(random), Signed(random)));

    static float Signed(Random random) => (float)(random.NextDouble() * 2 - 1);
    static float Between(Random random, double low, double high) => (float)(low + random.NextDouble() * (high - low));

    // An up-facing cells by cells grid of quads centred on the origin, random heights within the amplitude, with
    // a level 2 by 2 cell block of exactly coplanar neighbours.
    static Vector3[] Heightfield(Random random, int cells, float spacing, float amplitude)
    {
        var heights = new float[cells + 1, cells + 1];
        for (int i = 0; i <= cells; i++)
            for (int j = 0; j <= cells; j++) heights[i, j] = amplitude * Signed(random);
        int i0 = random.Next(cells - 1), j0 = random.Next(cells - 1);
        for (int i = i0; i <= i0 + 2; i++)
            for (int j = j0; j <= j0 + 2; j++) heights[i, j] = heights[i0, j0];
        Vector3 Corner(int i, int j) => new((i - cells / 2f) * spacing, heights[i, j], (j - cells / 2f) * spacing);
        var vertices = new List<Vector3>();
        for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
                vertices.AddRange(FootSupportScene.Quad(Corner(i, j), Corner(i + 1, j), Corner(i, j + 1),
                    Corner(i + 1, j + 1)));
        return [.. vertices];
    }

    static int F(float value) => BitConverter.SingleToInt32Bits(value);
    static float S(int bits) => BitConverter.Int32BitsToSingle(bits);
    static int[] V(Vector3 value) => [F(value.X), F(value.Y), F(value.Z)];
    static Vector3 V(int[] bits) => new(S(bits[0]), S(bits[1]), S(bits[2]));

    internal sealed record Bits(string Row, int[] Position, int[] Orientation, int Radius, int Length, int Band,
        int[] Axis, int Slope, int AlternateSlope, int? TargetId, string SceneDigest)
    {
        internal static Bits Encode(SupportEquivalenceQueryAnchor a) => new(a.Row, V(a.Pose.Position),
            [F(a.Pose.Orientation.X), F(a.Pose.Orientation.Y), F(a.Pose.Orientation.Z), F(a.Pose.Orientation.W)],
            F(a.Radius), F(a.Length), F(a.Band), [F(a.Axis.X), F(a.Axis.Y)], F(a.Slope), F(a.AlternateSlope),
            a.TargetId, a.SceneDigest);
        internal SupportEquivalenceQueryAnchor Decode() => new(Row,
            new(V(Position), new(S(Orientation[0]), S(Orientation[1]), S(Orientation[2]), S(Orientation[3]))),
            S(Radius), S(Length), S(Band), new(S(Axis[0]), S(Axis[1])), S(Slope), S(AlternateSlope), TargetId, SceneDigest);
    }

    static void WritePose(BinaryWriter writer, Pose pose)
    {
        Write(writer, pose.Position);
        writer.Write(F(pose.Orientation.X)); writer.Write(F(pose.Orientation.Y));
        writer.Write(F(pose.Orientation.Z)); writer.Write(F(pose.Orientation.W));
    }

    static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(F(value.X)); writer.Write(F(value.Y)); writer.Write(F(value.Z));
    }

    static void WriteShape(BinaryWriter writer, PhysicsShape shape)
    {
        writer.Write(shape.GetType().Name);
        switch (shape)
        {
            case BoxShape box: Write(writer, box.HalfExtents); break;
            case SphereShape sphere: writer.Write(F(sphere.Radius)); break;
            case CapsuleShape capsule: writer.Write(F(capsule.Radius)); writer.Write(F(capsule.Length)); break;
            case CylinderShape cylinder: writer.Write(F(cylinder.Radius)); writer.Write(F(cylinder.Length)); break;
            case ConvexHullShape hull:
                writer.Write(hull.Points.Length);
                foreach (Vector3 point in hull.Points) Write(writer, point);
                break;
            case TriangleMeshShape mesh:
                writer.Write(mesh.Vertices.Length);
                foreach (Vector3 point in mesh.Vertices) Write(writer, point);
                writer.Write(mesh.Indices.Length);
                foreach (int index in mesh.Indices) writer.Write(index);
                break;
            case CompoundShape compound:
                writer.Write(compound.Children.Length);
                foreach (CompoundChild child in compound.Children)
                {
                    WritePose(writer, child.Local);
                    WriteShape(writer, child.Shape);
                }
                break;
            default: throw new InvalidOperationException("Unknown equivalence shape.");
        }
    }
}
