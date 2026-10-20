using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepuUtilities;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;
using RigidPose = BepuPhysics.RigidPose;
using BepuCompound = BepuPhysics.Collidables.Compound;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class InstalledSupportPoseTests
{
    [Fact]
    public void ExactInstallControlsMatchBaseline()
    {
        var lines = new List<string>();
        Quaternion[] rotations = [Quaternion.Identity, new(0, 1, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f)];
        foreach (Quaternion rotation in rotations)
            foreach (float zero in new[] { 0f, BitConverter.Int32BitsToSingle(int.MinValue) })
                foreach (PhysicsShape shape in ExactShapes())
                {
                    using var world = new BepuPhysicsWorld(Vector3.Zero);
                    world.AddStatic(shape, new(new(zero, -1, zero), rotation));
                    using var lease = world.AcquireQueryReadLease();
                    var members = new SupportFootprintMember[256];
                    var joins = new ulong[1024];
                    SupportFootprintRequest request = SupportFootprintTests.Request(x: zero, z: zero);
                    SupportFootprintResult footprint = ((IPhysicsSupportFootprint)world)
                        .QuerySupportFootprint(lease, request, members, joins);
                    Assert.Equal(CapsuleFeatureStatus.Complete, footprint.Status);
                    Assert.True(footprint.Members > 0);
                    string footprintBits = Raw(footprint, world, lease) + " " +
                        string.Join(" ", members.Take(footprint.Members).Select(m => Raw(m, world, lease))) + " " +
                        string.Join(" ", joins.Take(footprint.Members * footprint.JoinWordsPerRow).Select(j => j.ToString("X16")));
                    using (SupportReferenceEvaluation.Begin())
                    {
                        SupportFootprintResult reference = ((IPhysicsSupportFootprint)world)
                            .QuerySupportFootprint(lease, request, members, joins);
                        Assert.Equal(footprintBits, Raw(reference, world, lease) + " " +
                            string.Join(" ", members.Take(reference.Members).Select(m => Raw(m, world, lease))) + " " +
                            string.Join(" ", joins.Take(reference.Members * reference.JoinWordsPerRow).Select(j => j.ToString("X16"))));
                    }
                    var elements = new SupportElement[256];
                    SupportNeighborhoodResult neighborhood = ((IPhysicsSupportNeighborhood)world).QuerySupportNeighborhood(
                        lease, new CapsuleShape(0.2f, 0.01f), Pose.At(new(zero, 0.205f, zero)), 0.0001f, elements, joins);
                    Assert.Equal(CapsuleFeatureStatus.Complete, neighborhood.Status);
                    Assert.True(neighborhood.Elements > 0);
                    lines.Add($"{shape.GetType().Name} {Raw(rotation, world, lease)} {BitConverter.SingleToInt32Bits(zero):X8} " +
                        footprintBits + " " + Raw(neighborhood, world, lease) + " " +
                        string.Join(" ", elements.Take(neighborhood.Elements).Select(e => Raw(e, world, lease))) + " " +
                        string.Join(" ", joins.Take(neighborhood.Elements * neighborhood.JoinWordsPerRow).Select(j => j.ToString("X16"))));
                }
        string? capture = Environment.GetEnvironmentVariable("KHAOZENGINE_SUPPORT_CONTROL_CAPTURE");
        if (!string.IsNullOrEmpty(capture)) File.WriteAllLines(KhaozEngine.Tests.Locomotion.Contacts.SupportEquivalenceQueryAnchors.CapturePath(capture), lines);
        else
        {
            string[] baselineControlBits = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts",
                "Fixtures", "support-exact-install-controls.txt"));
            string[] currentControlBits = [.. lines];
            Assert.Equal(baselineControlBits, currentControlBits);
        }
    }

    [Fact]
    public void SupportRepresentedFactoryMatchesEveryScalarAndWideLane()
    {
        foreach (Quaternion q in Rotations())
        {
            var pose = new RigidPose(new(0.125f, -0.25f, 0.5f), q);
            Assert.True(InstalledPoseOperator.TryCreateSupport(pose, out var map));
            int[] expectedCoefficientBits = CoefficientBits(q);
            Matrix3x3.CreateFromQuaternion(q, out var scalar);
            int[] scalarCoefficientBits = Bits(scalar.X, scalar.Y, scalar.Z);
            Assert.Equal(expectedCoefficientBits, scalarCoefficientBits);
            var wideQ = new QuaternionWide { X = new(q.X), Y = new(q.Y), Z = new(q.Z), W = new(q.W) };
            Matrix3x3Wide.CreateFromQuaternion(wideQ, out var wide);
            for (int lane = 0; lane < Vector<float>.Count; lane++)
            {
                int[] wideLaneCoefficientBits = Bits(new(wide.X.X[lane], wide.X.Y[lane], wide.X.Z[lane]),
                    new(wide.Y.X[lane], wide.Y.Y[lane], wide.Y.Z[lane]), new(wide.Z.X[lane], wide.Z.Y[lane], wide.Z.Z[lane]));
                Assert.Equal(expectedCoefficientBits, wideLaneCoefficientBits);
            }
            Assert.Equal(expectedCoefficientBits, Bits(Point(map.X), Point(map.Y), Point(map.Z)));
            Assert.Equal(expectedCoefficientBits, Bits(Point(map.RepresentedX), Point(map.RepresentedY), Point(map.RepresentedZ)));
            M exact = Represented(q);
            Encloses(map.Determinant.Bounds, exact.Determinant);
            Assert.Equal(GeometrySign.Positive, map.Determinant.Sign);
            Encloses(map.GramDiagonalDeparture, new(V.Dot(exact.X, exact.X) - R.One,
                V.Dot(exact.Y, exact.Y) - R.One, V.Dot(exact.Z, exact.Z) - R.One));
            Encloses(map.GramOffDiagonal, new(V.Dot(exact.X, exact.Y), V.Dot(exact.X, exact.Z), V.Dot(exact.Y, exact.Z)));
            Encloses(map.InverseTransposeX.Bounds, V.Cross(exact.Y, exact.Z) / exact.Determinant);
            Encloses(map.InverseTransposeY.Bounds, V.Cross(exact.Z, exact.X) / exact.Determinant);
            Encloses(map.InverseTransposeZ.Bounds, V.Cross(exact.X, exact.Y) / exact.Determinant);
        }
    }

    [Fact]
    public void SupportRepresentedFactoryPreservesSignedZerosWithoutNewCorrespondenceRefusal()
    {
        float negative = BitConverter.Int32BitsToSingle(int.MinValue);
        foreach (float x in new[] { 0f, negative })
            foreach (float y in new[] { 0f, negative })
                foreach (float z in new[] { 0f, negative })
                    foreach (float w in new[] { 1f, -1f })
                    {
                        var pose = new RigidPose(new(negative, 0, negative), new(x, y, z, w));
                        Assert.True(InstalledPoseOperator.TryCreateSupport(pose, out var map));
                        Assert.Equal(CoefficientBits(pose.Orientation), Bits(Point(map.X), Point(map.Y), Point(map.Z)));
                        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits((float)map.Translation.X.Value));
                        Assert.True(InstalledSweepBounds.OperationalCoefficientsMatch(map, pose.Orientation));
                    }
    }

    [Fact]
    public void SupportRepresentedFactoryKeepsPoseAndCoordinateDomains()
    {
        foreach (Quaternion q in new[] { default(Quaternion), new Quaternion(0, 0, 0, 2),
                     new Quaternion(float.NaN, 0, 0, 1), new Quaternion(0, float.PositiveInfinity, 0, 1) })
            Assert.False(InstalledPoseOperator.TryCreateSupport(new RigidPose(Vector3.Zero, q), out _));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, MathF.BitIncrement(2048) })
            Assert.False(InstalledPoseOperator.TryCreateSupport(new RigidPose(new(value, 0, 0), Quaternion.Identity), out _));
        Quaternion[] endpoints = [new(0, 0, 1, 1f / 1024), new(1023f / 1024, 43f / 1024, 14f / 1024, 1f / 1024)];
        for (int i = 0; i < endpoints.Length; i++)
            foreach (int step in new[] { -1, 0, 1 })
            {
                Quaternion q = endpoints[i];
                q.W = step < 0 ? MathF.BitDecrement(q.W) : step > 0 ? MathF.BitIncrement(q.W) : q.W;
                Assert.Equal(InBand(q), InstalledPoseOperator.TryCreateSupport(new RigidPose(Vector3.Zero, q), out _));
            }
        Assert.True(InstalledPoseOperator.TryCreateSupport(new RigidPose(new(2048, 0, 0), Quaternion.Identity), out var boundary));
        Assert.Equal(CapsuleFeatureStatus.Complete, CapsuleFeatureGeometry.TransformVertex(Vector3.Zero, boundary, out _));
        Assert.Equal(CapsuleFeatureStatus.Unsupported, CapsuleFeatureGeometry.TransformVertex(Vector3.UnitX, boundary, out _));
        Assert.Equal(CapsuleFeatureStatus.Unsupported, CapsuleFeatureGeometry.TransformVertex(new(MathF.BitIncrement(2048), 0, 0), boundary, out _));
    }

    [Fact]
    public void SupportCompositionUsesInstalledLeafBitsIncludingHullWrappersAndChildOrder()
    {
        Pose root = new(new(16, 8, -16), Quaternion.CreateFromYawPitchRoll(0.37f, 0.1f, -0.05f));
        foreach (bool reverse in new[] { false, true })
        {
            PhysicsShape hull = new ConvexHullShape([new(1, 0, 0), new(2, 0, 0), new(1, 1, 0), new(1, 0, 1)]);
            KhaozEngine.Physics.CompoundChild[] children = [new(hull, new(new(0.125f, 0.25f, -0.5f), new(0.5f, 0.5f, 0.5f, 0.5f))),
                new(new BoxShape(Vector3.One), Pose.At(new(2, 0, 0)))];
            if (reverse) Array.Reverse(children);
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            world.AddStatic(new CompoundShape(children), root);
            using var lease = world.AcquireQueryReadLease();
            var sim = (BepuPhysics.Simulation)typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
            ref var installed = ref sim.Statics[0];
            ref var compound = ref sim.Shapes.GetShape<BepuCompound>(installed.Shape.Index);
            var scratch = new CapsuleFeatureCaptureScratch();
            var leaves = new List<CapsuleFeaturePolyhedron>();
            Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodPolyhedra.Capture(sim, installed.Shape, installed.Pose, scratch, leaves));
            Assert.Equal(compound.Children.Length, leaves.Count);
            for (int i = 0; i < compound.Children.Length; i++)
            {
                var child = compound.Children[i];
                Pose local = new(child.LocalPose.Position, child.LocalPose.Orientation);
                Pose expected = Composed(local, root);
                BepuCompound.GetWorldPose(child.LocalPose, installed.Pose, out var actual);
                Assert.Equal(PoseBits(expected), PoseBits(new(actual.Position, actual.Orientation)));
                Assert.True(InstalledPoseOperations.ProveSupportComposition(child.LocalPose, installed.Pose, actual));
                Assert.Equal(i, leaves[i].LeafId);
                Assert.Equal(Bits(Represented(expected.Orientation).X.AsSingle(), Represented(expected.Orientation).Y.AsSingle(),
                    Represented(expected.Orientation).Z.AsSingle()), Bits(Point(leaves[i].SourceMap.X), Point(leaves[i].SourceMap.Y), Point(leaves[i].SourceMap.Z)));
                foreach (Vector3 vertex in leaves[i].LocalVertices)
                    Encloses(leaves[i].SourceMap.Point(vertex).Bounds, Represented(expected.Orientation).Apply(V.From(vertex)) + V.From(expected.Position));
                actual.Position.X = MathF.BitIncrement(actual.Position.X);
                Assert.False(InstalledPoseOperations.ProveSupportComposition(child.LocalPose, installed.Pose, actual));
            }
        }
    }

    [Fact]
    public void PlanarCompoundSupportQueriesAdmitOnlyThroughTheSupportDoor()
    {
        Quaternion yaw = new(0, 0.70710677f, 0, 0.70710677f);
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++) corners[i] = new((i & 1) == 0 ? 0 : 2, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
        Vector3[] mirrored = [.. corners.Select(c => new Vector3(-c.X, c.Y, c.Z))];
        Vector3[] offCentre = [.. corners.Select(c => c - new Vector3(0.5f, 0, 0))];
        (string Name, PhysicsShape Shape)[] scenes =
        [
            ("boxes", new CompoundShape([new(new BoxShape(Vector3.One), Pose.At(new(-1, 0, 0))),
                new(new BoxShape(Vector3.One), Pose.At(new(1, 0, 0)))])),
            ("hulls", new CompoundShape([new(new ConvexHullShape(corners), Pose.Identity),
                new(new ConvexHullShape(mirrored), Pose.Identity)])),
            ("hull centroid wrapper", new ConvexHullShape(offCentre)),
        ];
        foreach ((string name, PhysicsShape shape) in scenes)
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            world.AddStatic(shape, new(new(0, -1, 0), yaw));
            var sim = (BepuPhysics.Simulation)typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
            // Every scene installs as a Bepu compound, so the shared compound root gate runs.
            Assert.Equal(default(BepuCompound).TypeId, sim.Statics[0].Shape.Type);
            using var lease = world.AcquireQueryReadLease();
            var members = new SupportFootprintMember[256];
            var elements = new SupportElement[256];
            var joins = new ulong[1024];
            var dualAdmissions = new StrongBox<int>();
            StrongBox<int>? prior = InstalledPoseOperator.ObserveDualAdmissions(dualAdmissions);
            SupportNeighborhoodResult neighborhood;
            SupportFootprintResult footprint;
            try
            {
                neighborhood = ((IPhysicsSupportNeighborhood)world).QuerySupportNeighborhood(
                    lease, new CapsuleShape(0.2f, 0.01f), Pose.At(new(0, 0.205f, 0)), 0.0001f, elements, joins);
                footprint = ((IPhysicsSupportFootprint)world).QuerySupportFootprint(
                    lease, SupportFootprintTests.Request(), members, joins);
            }
            finally { InstalledPoseOperator.ObserveDualAdmissions(prior); }
            Assert.True(CapsuleFeatureStatus.Complete == neighborhood.Status, $"{name} neighborhood {neighborhood.Status}");
            Assert.True(neighborhood.Elements > 0, name);
            Assert.True(CapsuleFeatureStatus.Complete == footprint.Status, $"{name} footprint {footprint.Status}");
            Assert.True(footprint.Members > 0, name);
            Assert.True(dualAdmissions.Value == 0, $"{name} reached {dualAdmissions.Value} dual admissions");
        }
    }

    static IEnumerable<Quaternion> Rotations() => [Quaternion.Identity, new(-0f, 0, -0f, 1), new(0, 1, 0, 0),
        new(0.5f, 0.5f, 0.5f, 0.5f), new(0, 0.70710677f, 0, 0.70710677f), Quaternion.CreateFromYawPitchRoll(0.37f, 0.1f, -0.05f)];
    internal static Vector3 Point(FeaturePoint p) { Assert.True(p.IsExact); return new((float)p.X.Value, (float)p.Y.Value, (float)p.Z.Value); }
    static int[] Bits(params Vector3[] vectors) => vectors.SelectMany(v => new[] { BitConverter.SingleToInt32Bits(v.X),
        BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z) }).ToArray();
    static int[] PoseBits(Pose p) => [.. Bits(p.Position), BitConverter.SingleToInt32Bits(p.Orientation.X),
        BitConverter.SingleToInt32Bits(p.Orientation.Y), BitConverter.SingleToInt32Bits(p.Orientation.Z), BitConverter.SingleToInt32Bits(p.Orientation.W)];
    internal static void Encloses(GeometryInterval bounds, R exact)
    {
        Assert.True(bounds.IsResolved);
        Assert.True(R.From(bounds.Lower) <= exact && exact <= R.From(bounds.Upper), $"[{bounds.Lower:R}, {bounds.Upper:R}] misses {exact}");
    }
    internal static void Encloses(GeometryVector bounds, V exact) { Encloses(bounds.X, exact.X); Encloses(bounds.Y, exact.Y); Encloses(bounds.Z, exact.Z); }

    // Signed zeros accompany the existing independent rational rounding graph. No host float multiply/add
    // computes an expected coefficient. Exact cancellation is positive zero except the IEEE all-negative sum.
    readonly record struct SingleValue(R Value, bool Negative)
    {
        internal static SingleValue From(float f) => new(R.From(f), BitConverter.SingleToInt32Bits(f) < 0);
        internal int Bits => Value == R.Zero ? Negative ? int.MinValue : 0 : BitConverter.SingleToInt32Bits(Value.AsSingle());
        static SingleValue Round(R value, bool negativeZero) => new(Single(value), value < R.Zero || (value == R.Zero && negativeZero));
        public static SingleValue operator +(SingleValue a, SingleValue b) => Round(a.Value + b.Value, a.Negative && b.Negative);
        public static SingleValue operator -(SingleValue a, SingleValue b) => a + new SingleValue(-b.Value, !b.Negative);
        public static SingleValue operator *(SingleValue a, SingleValue b) => Round(a.Value * b.Value, a.Negative != b.Negative);
    }
    internal static int[] CoefficientBits(Quaternion q)
    {
        SingleValue x = SingleValue.From(q.X), y = SingleValue.From(q.Y), z = SingleValue.From(q.Z), w = SingleValue.From(q.W);
        SingleValue x2 = x + x, y2 = y + y, z2 = z + z, one = SingleValue.From(1);
        SingleValue xx = x2 * x, yy = y2 * y, zz = z2 * z, xy = x2 * y, xz = x2 * z, xw = x2 * w,
            yz = y2 * z, yw = y2 * w, zw = z2 * w;
        return [(one - yy - zz).Bits, (xy + zw).Bits, (xz - yw).Bits, (xy - zw).Bits, (one - xx - zz).Bits,
            (yz + xw).Bits, (xz + yw).Bits, (yz - xw).Bits, (one - xx - yy).Bits];
    }

    static IEnumerable<PhysicsShape> ExactShapes()
    {
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++) corners[i] = new((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
        yield return new BoxShape(Vector3.One);
        yield return new ConvexHullShape(corners);
        yield return new CompoundShape([new(new BoxShape(Vector3.One), Pose.Identity),
            new(new BoxShape(Vector3.One), Pose.Identity)]);
        // Front is Cross(C-A,B-A), the opposite of the polyhedron winding.
        int[][] faces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
        yield return new TriangleMeshShape(corners, faces.SelectMany(f => new[] { f[0], f[2], f[1], f[0], f[3], f[2] }).ToArray());
    }

    // Serialize every public value field/property, including both floating zero signs. Reference metadata is
    // authenticated against this query's actual world and lease before it is encoded as a stable identity token.
    internal static string Raw(object value, IPhysicsWorld world, IPhysicsQueryLease lease)
    {
        if (value is float f) return BitConverter.SingleToInt32Bits(f).ToString("X8");
        if (value is double d) return BitConverter.DoubleToInt64Bits(d).ToString("X16");
        if (value is IPhysicsWorld w) { Assert.Same(world, w); return "world"; }
        if (value is IPhysicsQueryLease l) { Assert.Same(lease, l); return "lease"; }
        Type type = value.GetType();
        if (type.IsEnum || type.IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name)
            .Select(p => p.Name + "=" + Raw(p.GetValue(value)!, world, lease));
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name)
            .Select(p => p.Name + "=" + (p.GetValue(value) is { } item ? Raw(item, world, lease) : "null"));
        return "{" + string.Join(",", fields.Concat(properties)) + "}";
    }
}
