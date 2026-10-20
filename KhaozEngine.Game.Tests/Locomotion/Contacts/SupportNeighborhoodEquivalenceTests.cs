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

public partial class SupportNeighborhoodEquivalenceTests(ITestOutputHelper output)
{
    const int Capacity = SupportNeighborhoodResult.MaximumElements;
    // Names a file to write the recorded digests to instead of comparing. Used once, on the code before the change.
    const string CaptureVariable = "KHAOZENGINE_SUPPORT_EQUIVALENCE_CAPTURE";

    [Fact]
    public void NeighborhoodsAndSupportMatchTheRecordedFixture()
    {
        var lines = new List<string>();
        var anchors = new SupportEquivalenceQueryAnchors();
        Generate(lines, anchors);
        anchors.Complete();
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
        var protectedMismatches = new List<string>();
        for (int i = 0; i < Math.Max(expected.Length, lines.Count); i++)
        {
            string? want = i < expected.Length ? expected[i] : null, got = i < lines.Count ? lines[i] : null;
            if (want != got)
            {
                string mismatch = $"row {i}: expected '{want}', got '{got}'";
                mismatches.Add(mismatch);
                if (want is not null && (want.StartsWith("feature/", StringComparison.Ordinal) ||
                    want.StartsWith("fan/", StringComparison.Ordinal) ||
                    want.StartsWith("mesh/", StringComparison.Ordinal) && int.Parse(want.Split('/')[1]) % 2 == 0))
                    protectedMismatches.Add(mismatch);
            }
        }
        output.WriteLine($"{lines.Count} rows compared, {mismatches.Count} differ.");
        output.WriteLine($"{protectedMismatches.Count} identity mesh, fan or legacy feature rows differ.");
        Assert.True(protectedMismatches.Count == 0, string.Join("\n", protectedMismatches));
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    [Fact]
    public void SupportEquivalenceAnchorsKeepEveryOriginalQueryBit()
    {
        var anchors = new SupportEquivalenceQueryAnchors(verifyOnly: true);
        Generate([], anchors);
        anchors.Complete();
    }

    static void Generate(List<string> lines, SupportEquivalenceQueryAnchors anchors) =>
        anchors.Generate((query, world, lease) => lines.Add(query.TargetId.HasValue
            ? Feature(query, world, lease) : Neighborhood(query, world, lease)));

    static string Feature(SupportEquivalenceQueryAnchor query, IPhysicsWorld world, IPhysicsQueryLease lease)
    {
        Pose pose = query.Pose;
        var probe = new CapsuleShape(query.Radius, query.Length);
        var target = new StaticHandle(query.TargetId!.Value);
        var faces = new CapsuleIncidentFace[Capacity];
        CapsuleFeatureResult result = ((IPhysicsCapsuleFeatures)world).QueryCapsuleFeature(lease, target, probe, pose,
            query.Band, faces, QueryFilter.StaticsOnly);
        var digest = new Digest();
        digest.Feature(result);
        for (int i = 0; i < result.Written; i++) digest.Face(faces[i]);
        return $"{query.Row} {result.Status} {result.Kind} {result.Written} {digest.Hex()}";
    }

    static string Neighborhood(SupportEquivalenceQueryAnchor query, IPhysicsWorld world, IPhysicsQueryLease lease)
    {
        Pose pose = query.Pose;
        var probe = new CapsuleShape(query.Radius, query.Length);
        var digest = new Digest();
        (SupportNeighborhoodResult result, int joined) = Query(world, lease, probe, pose,
            query.Axis, query.Slope, query.Band, digest);
        // A second slope limit makes steep and walkable members trade places.
        Query(world, lease, probe, pose, query.Axis, query.AlternateSlope, query.Band, digest);
        return $"{query.Row} {result.Status} {result.Elements} {joined} {digest.Hex()}";
    }

    static (SupportNeighborhoodResult, int) Query(IPhysicsWorld world, IPhysicsQueryLease lease, CapsuleShape probe,
        Pose pose, Vector2 axis, float cosMaxSlope, float band, Digest digest)
    {
        var features = (IPhysicsSupportNeighborhood)world;
        var elements = new SupportElement[Capacity];
        var joins = new ulong[Capacity * SupportNeighborhoodResult.JoinWordsFor(Capacity)];
        var contributions = new SupportContribution[Capacity];
        SupportNeighborhoodResult result = features.QuerySupportNeighborhood(lease, probe, pose, band, elements, joins,
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
