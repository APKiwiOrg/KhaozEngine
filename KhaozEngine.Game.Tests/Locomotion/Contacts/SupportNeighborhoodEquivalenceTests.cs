// The neighborhood cost task's equivalence proof. The fixture was recorded from the code before the support
// neighborhood was made allocation free and its join pass pruned. It holds one digest per question: the
// neighborhood status, every element field bit for bit, the whole join matrix and the certified contributions, or
// for a FootSupport row the whole SupportSample and both probes' neighborhoods. A changed bit anywhere changes the
// digest. Poses follow the brute force oracle rows: random boxes, hulls, meshes and curved solids under random
// rotations with the probe placed against a surface point, then FootSupport over the scene catalogue.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class SupportNeighborhoodEquivalenceTests(ITestOutputHelper output)
{
    const float Band = SupportCertification.ContactBand;
    const int Capacity = SupportNeighborhoodResult.MaximumElements;
    // Names a file to write the recorded digests to instead of comparing. Used once, on the code before the change.
    const string CaptureVariable = "KHAOZENGINE_SUPPORT_EQUIVALENCE_CAPTURE";
    static readonly CapsuleShape[] Probes = [new(0.01f, 0.01f), new(0.2f, 0.01f)];
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly float CosMaxSlope = MathF.Cos(Tuning.MaxSlopeRadians);

    [Fact]
    public void NeighborhoodsAndSupportMatchTheRecordedFixture()
    {
        var lines = new List<string>();
        Polyhedra(lines);
        Meshes(lines);
        Curved(lines);
        Support(lines);
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

    // FootSupport over the scene catalogue at a grid of axes and feet heights. Each row digests the sample and both
    // probes' neighborhoods and contributions, recomputed exactly as FootSupport proposes them.
    static void Support(List<string> lines)
    {
        foreach ((string name, Func<FootSupportScene> build, (float X, float Z, float Feet)[] poses) in SupportScenes())
        {
            using FootSupportScene scene = build();
            foreach ((float x, float z, float feet) in poses)
            {
                float footRadius = new GroundCoreSettings().FootRadiusFraction * Tuning.CapsuleRadius;
                var query = new FootSupportQuery(new Vector2(x, z), feet, footRadius, Tuning.StepHeight,
                    Tuning.StepHeight, CosMaxSlope);
                SupportSample sample = FootSupport.Find(null, null, scene.World, scene.Lease, query);
                var digest = new Digest();
                digest.Sample(sample);
                foreach (float radius in new[] { FootSupport.AxisProbeRadius, footRadius })
                    Probe(scene.World, scene.Lease, query, radius, digest);
                lines.Add($"foot/{name}/{x}/{z}/{feet} {sample.Status} {digest.Hex()}");
            }
        }
    }

    static IEnumerable<(string, Func<FootSupportScene>, (float, float, float)[])> SupportScenes()
    {
        (float, float, float)[] Grid(float low, float high, params float[] feet)
        {
            var poses = new List<(float, float, float)>();
            for (float x = low; x <= high + 1e-4f; x += 0.15f)
                foreach (float z in new[] { -0.25f, 0.1f })
                    foreach (float f in feet) poses.Add((x, z, f));
            return [.. poses];
        }
        foreach (SceneVariant v in new[] { SceneVariant.Box, SceneVariant.Mesh })
        {
            yield return ($"floor-{v}", () => Floor(v), Grid(-0.3f, 0.6f, 0, 0.2f));
            yield return ($"slope30-{v}", () => Slope(v, 30), Grid(-0.6f, 0.6f, -0.3f, 0, 0.3f));
            yield return ($"slope50-{v}", () => Slope(v, 50), Grid(-0.6f, 0.6f, -0.3f, 0, 0.3f));
            yield return ($"lip-{v}", () => Lip(v), Grid(-0.45f, 0.3f, 0, LipTop));
            yield return ($"crate-{v}", () => Crate(v), Grid(-0.45f, 0.3f, 0, CrateTop));
            yield return ($"treads-{v}", () => Treads(v), Grid(-0.3f, 0.9f, 0.25f, 0.5f));
            yield return ($"ramp-{v}", () => Ramp(v, 0.5f), Grid(-0.45f, 0.45f, 0, 0.1f));
            yield return ($"ridge-{v}", () => Ridge(v, 30), Grid(-0.45f, 0.45f, 0.3f, 0.5f));
            yield return ($"tworidge-{v}", () => TwoStaticRidge(v, 30), Grid(-0.45f, 0.45f, 0.3f, 0.5f));
            yield return ($"bank-{v}", () => Bank(v), Grid(0.6f, 1.2f, 0, BankTop));
            yield return ($"stairs-{v}", () => Stairs(v, 0.35f, 0.25f, 6), Grid(-0.3f, 1.2f, 0.25f, 0.5f, 0.75f));
            yield return ($"sphere-{v}", () => Sphere(v, new Vector3(0, -0.25f, 0), 0.25f), Grid(-0.3f, 0.3f, -0.1f, 0));
            yield return ($"plateau-{v}", () => PlateauBesideSteepFace(v), Grid(-0.45f, 0.45f, -0.3f, 0));
            yield return ($"overlap-{v}", () => OverlappingCoplanarFloors(v), Grid(-1.2f, 1.2f, 0));
        }
        yield return ("incline", () => Incline(20), Grid(-0.6f, 0.6f, -0.2f, 0, 0.2f));
        yield return ("cylinder", () => UprightCylinder(Vector3.Zero, 0.3f, 0.5f), Grid(-0.45f, 0.45f, 0.5f));
        yield return ("leaning", () => UprightCylinder(Vector3.Zero, 0.3f, 0.5f, 20), Grid(-0.45f, 0.45f, 0.4f, 0.5f));
        yield return ("log", () => LyingLog(new Vector3(0, 0.3f, 0), 0.3f, 2), Grid(-0.45f, 0.45f, 0.5f, 0.6f));
        yield return ("terrain1", () => PartitionedTerrain(1), Grid(-0.9f, 0.9f, 0, 0.2f));
        yield return ("terrain16", () => PartitionedTerrain(16), Grid(-0.9f, 0.9f, 0, 0.2f));
        yield return ("overfan", () => OverCapacityFan(), [(0, 0, 0), (0.5f, 0.1f, 0)]);
        yield return ("compound", Compound, Grid(-0.6f, 0.6f, 0.3f, 0.5f));
        yield return ("fan96", () => Fan(96), [(0, 0, 0)]);
        yield return ("grid20000", () => Grid20000(), [(0.123f, -0.234f, 0), (0.05f, 0.05f, 0)]);
    }

    // A box with a cylinder standing on it, one compound static over a box floor.
    static FootSupportScene Compound() => Floor(SceneVariant.Box).Add("compound", new CompoundShape([
            new(new BoxShape(new Vector3(0.4f, 0.15f, 0.4f)), Pose.At(new Vector3(0, 0.15f, 0))),
            new(new CylinderShape(0.2f, 0.3f), Pose.At(new Vector3(0.2f, 0.3f, 0)))]),
        Pose.At(new Vector3(0.1f, 0, 0)));

    // A flat fan of up-facing triangles meeting at the origin, with a rim of radius 1.
    static FootSupportScene Fan(int triangles)
    {
        Vector3 Rim(int i) => new(MathF.Cos(2 * MathF.PI * (i % triangles) / triangles), 0,
            MathF.Sin(2 * MathF.PI * (i % triangles) / triangles));
        var vertices = new Vector3[3 * triangles];
        for (int i = 0; i < triangles; i++)
            FootSupportScene.Facing(Vector3.Zero, Rim(i), Rim(i + 1), Vector3.UnitY).CopyTo(vertices, 3 * i);
        return new FootSupportScene(SceneVariant.Mesh).Mesh("fan", vertices);
    }

    // One flat mesh of 100 by 100 quads of 0.1 centred on the origin at Y 0.
    static FootSupportScene Grid20000()
    {
        const int cells = 100;
        float Coordinate(int k) => 0.1f * (k - cells / 2);
        var vertices = new List<Vector3>(6 * cells * cells);
        for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
                vertices.AddRange(FootSupportScene.Quad(new(Coordinate(i), 0, Coordinate(j)),
                    new(Coordinate(i + 1), 0, Coordinate(j)), new(Coordinate(i), 0, Coordinate(j + 1)),
                    new(Coordinate(i + 1), 0, Coordinate(j + 1))));
        return new FootSupportScene(SceneVariant.Mesh).Mesh("grid", [.. vertices]);
    }

    // FootSupport's probe: the same sweep, contact pose, neighborhood and certification.
    static void Probe(IPhysicsWorld world, IPhysicsQueryLease lease, in FootSupportQuery query, float radius,
        Digest digest)
    {
        const float probeLength = 0.01f, bandMargin = 0.001f;
        var capsule = new CapsuleShape(radius, probeLength);
        float lowest = query.FeetY + query.ReachUp + bandMargin;
        float centreY = lowest + radius + probeLength / 2;
        Pose start = Pose.At(new Vector3(query.Axis.X, centreY, query.Axis.Y));
        bool hit = world.SweepCapsule(capsule, start, -Vector3.UnitY, query.ReachUp + query.ReachDown + 2 * bandMargin,
            out SweepHit sweep, QueryFilter.StaticsOnly with { CullBackFaces = true });
        digest.Int(hit ? 1 : 0);
        if (!hit || sweep.Body is not StaticHandle) return;
        digest.Float(sweep.Distance);
        Pose contact = Pose.At(new Vector3(query.Axis.X, centreY - sweep.Distance, query.Axis.Y));
        Query(world, lease, capsule, contact, query.Axis, query.CosMaxSlope, digest);
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
    // three bands out, so both edges of the band are crossed. The caller adds the probe radius along the normal.
    static OracleVector Outward(Random random, OracleVector target, OracleVector normal)
    {
        OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.2;
        double gap = Between(random, -Band, 3 * Band);
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

        internal void Sample(in SupportSample sample)
        {
            Int((int)sample.Status);
            Float(sample.Height);
            Float(sample.HeightError);
            Vector(sample.Normal);
            Int(sample.Static?.Value ?? -1);
            Int(sample.FeatureId);
            Vector(sample.Witness);
        }

        internal string Hex()
        {
            _writer.Flush();
            return Convert.ToHexString(SHA256.HashData(_stream.ToArray()))[..32];
        }
    }
}
