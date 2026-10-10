// The neighborhood cost task's equivalence proof. The neighborhood rows were recorded from the code before the
// support neighborhood was made allocation free and its join pass pruned. Each row holds one digest per question:
// the neighborhood status, every element field bit for bit, the whole join matrix and the certified contributions, or
// for a feature row the whole closest-feature result and its incident faces. A changed bit anywhere changes the
// digest. Poses follow the brute force oracle rows: random boxes, hulls, meshes and curved solids under random
// rotations with the probe placed against a surface point, then dense fans and closest-feature queries.
// Every row is platform independent. No input passes through a Bepu sweep, whose contact distances differ in the last
// bits between x64 and arm64, or through MathF trigonometry, which differs between platform maths libraries. Scene
// coordinates come from exact arithmetic, and the slope limit is a literal.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class SupportNeighborhoodEquivalenceTests(ITestOutputHelper output)
{
    const float Band = SupportCertification.ContactBand;
    const int Capacity = SupportNeighborhoodResult.MaximumElements;
    // Names a file to write the recorded digests to instead of comparing. Used once, on the code before the change.
    const string CaptureVariable = "KHAOZENGINE_SUPPORT_EQUIVALENCE_CAPTURE";
    static readonly CapsuleShape[] Probes = [new(0.01f, 0.01f), new(0.2f, 0.01f)];
    // The float nearest cos(MoveTuning.Default.MaxSlopeRadians), the default 45 degree slope limit, bits 0x3F3504F3.
    const float CosMaxSlope = 0.70710677f;

    [Fact]
    public void NeighborhoodsAndSupportMatchTheRecordedFixture()
    {
        var lines = new List<string>();
        Polyhedra(lines);
        Meshes(lines);
        Curved(lines);
        Fans(lines);
        Features(lines);
        string? capture = Environment.GetEnvironmentVariable(CaptureVariable);
        if (!string.IsNullOrEmpty(capture))
        {
            File.WriteAllLines(capture, lines);
            output.WriteLine($"Recorded {lines.Count} rows to {capture}.");
            return;
        }
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts",
            "Fixtures", "support-equivalence.txt"));
        var mismatches = new List<string>();
        for (int i = 0; i < Math.Max(expected.Length, lines.Count); i++)
        {
            string? want = i < expected.Length ? expected[i] : null, got = i < lines.Count ? lines[i] : null;
            if (want != got) mismatches.Add($"row {i}: expected '{want}', got '{got}'");
        }
        output.WriteLine($"{lines.Count} rows compared, {mismatches.Count} differ.");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    // Random boxes and hulls under random rotations, each probe placed against a random face point, both radii.
    static void Polyhedra(List<string> lines)
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
                faces = SupportNeighborhoodOracle.Box(world.AddStatic(new BoxShape(half), pose), half, pose);
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
                StaticHandle hull = world.AddStatic(new ConvexHullShape(points), new Pose(position, rotation));
                faces = SupportNeighborhoodOracle.InstalledHull(world, hull);
            }
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 10; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                OracleVector target = SupportNeighborhoodTests.SurfacePoint(random, face);
                OracleVector anchor = Outward(random, target, face.Normal);
                foreach (CapsuleShape probe in Probes)
                    lines.Add(Neighborhood($"poly/{solid}/{sample}/{probe.Radius}", world, lease, probe,
                        anchor + face.Normal * probe.Radius));
            }
        }
    }

    // Random heightfields, level or under any rotation, each probe placed against a random triangle point.
    static void Meshes(List<string> lines)
    {
        var random = new Random(7438);
        for (int field = 0; field < 20; field++)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            Vector3[] vertices = Heightfield(random, 4, Between(random, 0.2, 0.5), Between(random, 0.05, 0.3));
            Quaternion rotation = field % 2 == 0 ? Quaternion.Identity : RandomRotation(random);
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            StaticHandle mesh = world.AddStatic(new TriangleMeshShape(vertices,
                [.. Enumerable.Range(0, vertices.Length)]), new Pose(position, rotation));
            List<OracleFace> faces = SupportNeighborhoodOracle.InstalledMesh(world, mesh);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 10; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                OracleVector target = SupportNeighborhoodTests.SurfacePoint(random, face);
                OracleVector anchor = Outward(random, target, face.Normal);
                foreach (CapsuleShape probe in Probes)
                {
                    // The axis end nearer the face's plane takes the point, so the whole axis lies in front of it.
                    OracleVector lowest = anchor + face.Normal * probe.Radius;
                    if (face.Normal.Y < 0) lowest -= new OracleVector(0, probe.Length, 0);
                    lines.Add(Neighborhood($"mesh/{field}/{sample}/{probe.Radius}", world, lease, probe, lowest));
                }
            }
        }
    }

    // Spheres, capsules and cylinders, every other one a compound leaf under its own rotated pose.
    static void Curved(List<string> lines)
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
            StaticHandle handle = world.AddStatic(shape, new Pose(position, rotation));
            List<OracleTangent> tangents = SupportNeighborhoodOracle.InstalledCurved(world, handle);
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
                    lines.Add(Neighborhood($"curved/{solid}/{sample}/{probe.Radius}", world, lease, probe,
                        anchor + surface.Normal * probe.Radius));
            }
        }
    }

    // Flat fans of up-facing triangles meeting at the origin, with a square rim of half size 1. A probe at the apex
    // meets every triangle, which is the join pass's worst case below capacity and a capacity refusal above it.
    static void Fans(List<string> lines)
    {
        foreach (int triangles in new[] { 96, 300 })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            world.AddStatic(new TriangleMeshShape(Fan(triangles), [.. Enumerable.Range(0, 3 * triangles)]),
                Pose.Identity);
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            foreach (CapsuleShape probe in Probes)
            {
                lines.Add(Neighborhood($"fan/{triangles}/apex/{probe.Radius}", world, lease, probe,
                    new OracleVector(0, 0, 0)));
                lines.Add(Neighborhood($"fan/{triangles}/side/{probe.Radius}", world, lease, probe,
                    new OracleVector(0.5, 0, 0.25)));
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
    static void Features(List<string> lines)
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
                    target = world.AddStatic(new BoxShape(half), pose);
                    faces = SupportNeighborhoodOracle.Box(target, half, pose);
                    break;
                case 1:
                    var points = new Vector3[10];
                    float scale = Between(random, 0.3, 0.8);
                    for (int i = 0; i < points.Length; i++)
                    {
                        OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                        points[i] = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z) * scale;
                    }
                    target = world.AddStatic(new ConvexHullShape(points), pose);
                    faces = SupportNeighborhoodOracle.InstalledHull(world, target);
                    break;
                default:
                    Vector3[] vertices = Heightfield(random, 4, Between(random, 0.2, 0.5), Between(random, 0.05, 0.3));
                    target = world.AddStatic(new TriangleMeshShape(vertices, [.. Enumerable.Range(0, vertices.Length)]),
                        pose);
                    faces = SupportNeighborhoodOracle.InstalledMesh(world, target);
                    break;
            }
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            for (int sample = 0; sample < 4; sample++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                // Within the band, where the closest feature is decided rather than absent.
                OracleVector anchor = Outward(random, SupportNeighborhoodTests.SurfacePoint(random, face), face.Normal,
                    0, Band);
                foreach (CapsuleShape probe in Probes)
                {
                    OracleVector lowest = anchor + face.Normal * probe.Radius;
                    if (face.Normal.Y < 0) lowest -= new OracleVector(0, probe.Length, 0);
                    lines.Add(Feature($"feature/{solid}/{sample}/{probe.Radius}", world, lease, target, probe, lowest));
                }
            }
        }
    }

    static string Feature(string label, IPhysicsWorld world, IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape probe, OracleVector lowest)
    {
        Pose pose = SupportNeighborhoodTests.Lowest(lowest.X, lowest.Y, lowest.Z);
        var faces = new CapsuleIncidentFace[Capacity];
        CapsuleFeatureResult result = ((IPhysicsCapsuleFeatures)world).QueryCapsuleFeature(lease, target, probe, pose,
            Band, faces, QueryFilter.StaticsOnly);
        var digest = new Digest();
        digest.Feature(result);
        for (int i = 0; i < result.Written; i++) digest.Face(faces[i]);
        return $"{label} {result.Status} {result.Kind} {result.Written} {digest.Hex()}";
    }

    static string Neighborhood(string label, IPhysicsWorld world, IPhysicsQueryLease lease, CapsuleShape probe,
        OracleVector lowest)
    {
        Pose pose = SupportNeighborhoodTests.Lowest(lowest.X, lowest.Y, lowest.Z);
        var digest = new Digest();
        (SupportNeighborhoodResult result, int joined) = Query(world, lease, probe, pose,
            new Vector2(pose.Position.X, pose.Position.Z), CosMaxSlope, digest);
        // A second slope limit makes steep and walkable members trade places.
        Query(world, lease, probe, pose, new Vector2(pose.Position.X, pose.Position.Z), 0.5f, digest);
        return $"{label} {result.Status} {result.Elements} {joined} {digest.Hex()}";
    }

    static (SupportNeighborhoodResult, int) Query(IPhysicsWorld world, IPhysicsQueryLease lease, CapsuleShape probe,
        Pose pose, Vector2 axis, float cosMaxSlope, Digest digest)
    {
        var features = (IPhysicsSupportNeighborhood)world;
        var elements = new SupportElement[Capacity];
        var joins = new ulong[Capacity * SupportNeighborhoodResult.JoinWordsFor(Capacity)];
        var contributions = new SupportContribution[Capacity];
        SupportNeighborhoodResult result = features.QuerySupportNeighborhood(lease, probe, pose, Band, elements, joins,
            QueryFilter.StaticsOnly);
        digest.Int((int)result.Status);
        digest.Int(result.Elements);
        digest.Int(result.RequiredElements);
        digest.Int(result.JoinWordsPerRow);
        int joined = 0;
        for (int i = 0; i < result.Elements; i++) digest.Element(elements[i]);
        for (int i = 0; i < result.Elements * result.JoinWordsPerRow; i++)
        {
            digest.Long((long)joins[i]);
            joined += System.Numerics.BitOperations.PopCount(joins[i]);
        }
        int count = SupportCertification.CertifyNeighborhood(features, lease, result, elements, joins, axis,
            cosMaxSlope, contributions);
        digest.Int(count);
        for (int i = 0; i < count; i++) digest.Contribution(contributions[i]);
        return (result, joined / 2);
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

    static float Signed(Random random) => SupportNeighborhoodTests.Signed(random);
    static float Between(Random random, double low, double high) => SupportNeighborhoodTests.Between(random, low, high);

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

    // Bit-exact SHA-256 over every recorded field.
    sealed class Digest
    {
        readonly MemoryStream _stream = new();
        readonly BinaryWriter _writer;

        internal Digest() => _writer = new BinaryWriter(_stream);

        internal void Int(int value) => _writer.Write(value);
        internal void Long(long value) => _writer.Write(value);
        internal void Float(float value) => _writer.Write(BitConverter.SingleToInt32Bits(value));
        internal void Double(double value) => _writer.Write(BitConverter.DoubleToInt64Bits(value));
        internal void Vector(Vector3 value)
        {
            Float(value.X);
            Float(value.Y);
            Float(value.Z);
        }

        internal void Element(in SupportElement element)
        {
            Int(element.Static.Value);
            Int((int)element.Kind);
            Int(element.ElementId);
            Vector(element.Normal);
            Float(element.NormalError);
            Vector(element.Witness);
            Float(element.PositionErrorMetres);
            Double(element.SeparationLower);
            Double(element.SeparationUpper);
        }

        internal void Contribution(in SupportContribution contribution)
        {
            Int((int)contribution.Kind);
            Double(contribution.Lower);
            Double(contribution.Upper);
            Vector(contribution.Normal);
            Vector(contribution.Witness);
            Int(contribution.FeatureId);
        }

        internal void Feature(in CapsuleFeatureResult result)
        {
            Int((int)result.Status);
            Int(result.Written);
            Int(result.RequiredCapacity);
            Int(result.Target.Value);
            Int(result.LeafId);
            Int(result.FeatureId);
            Int((int)result.Kind);
            Vector(result.AxisPoint);
            Vector(result.GeometryPoint);
            Vector(result.SeparationNormal);
            Double(result.SeparationLower);
            Double(result.SeparationUpper);
            Float(result.PositionErrorMetres);
            Float(result.NormalError);
        }

        internal void Face(in CapsuleIncidentFace face)
        {
            Int(face.FaceId);
            Vector(face.Normal);
            Float(face.NormalError);
            Int((int)face.Incidence);
        }

        internal string Hex()
        {
            _writer.Flush();
            return Convert.ToHexString(SHA256.HashData(_stream.ToArray()))[..32];
        }
    }
}
