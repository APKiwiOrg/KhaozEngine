using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
