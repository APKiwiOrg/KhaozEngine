using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

// This serializer uses only result types shared with 9e63657a7. The same source captures the old
// answers and reads HEAD answers. It never supplies an expected represented geometric decision.
internal sealed class SupportEquivalenceFields
{
    internal sealed record Field(string Name, string Class, int Width, string Bits);
    internal sealed record Row(string Name, string Scene, string Question, string Summary, Field[] Fields)
    {
        internal string Hash => Rehash(Fields);
        internal string Line => Summary + " " + Hash;
    }

    readonly List<Field> fields = [];
    string prefix = "";
    void I(string name, string kind, int value) => fields.Add(new(prefix + name, kind, 4, unchecked((uint)value).ToString("X8")));
    void L(string name, string kind, long value) => fields.Add(new(prefix + name, kind, 8, unchecked((ulong)value).ToString("X16")));
    void F(string name, string kind, float value) => I(name, kind, BitConverter.SingleToInt32Bits(value));
    void D(string name, string kind, double value) => L(name, kind, BitConverter.DoubleToInt64Bits(value));
    void V(string name, string kind, Vector3 v) { F(name + ".x", kind, v.X); F(name + ".y", kind, v.Y); F(name + ".z", kind, v.Z); }

    internal static string Rehash(IEnumerable<Field> fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (Field field in fields)
            if (field.Width == 4) writer.Write(Convert.ToUInt32(field.Bits, 16));
            else { Assert.Equal(8, field.Width); writer.Write(Convert.ToUInt64(field.Bits, 16)); }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))[..32];
    }

    internal static Row Read(SupportEquivalenceQueryAnchor q, IPhysicsWorld world, IPhysicsQueryLease lease)
    {
        var capture = new SupportEquivalenceFields();
        string summary;
        if (q.TargetId.HasValue)
        {
            var faces = new CapsuleIncidentFace[256];
            var r = ((IPhysicsCapsuleFeatures)world).QueryCapsuleFeature(lease, new(q.TargetId.Value),
                new CapsuleShape(q.Radius, q.Length), q.Pose, q.Band, faces, QueryFilter.StaticsOnly);
            capture.Feature(r);
            for (int i = 0; i < r.Written; i++)
            {
                capture.prefix = $"face/{i}/";
                capture.I("id", "identity", faces[i].FaceId);
                capture.V("normal", "normal", faces[i].Normal);
                capture.F("normalError", "enclosure", faces[i].NormalError);
                capture.I("incidence", "eligibility", (int)faces[i].Incidence);
            }
            summary = $"{q.Row} {r.Status} {r.Kind} {r.Written}";
        }
        else
        {
            var (r, joined) = capture.Neighborhood(q, world, lease, q.Slope, "primary/");
            capture.Neighborhood(q, world, lease, q.AlternateSlope, "alternate/");
            summary = $"{q.Row} {r.Status} {r.Elements} {joined}";
        }
        return new(q.Row, q.SceneDigest, JsonSerializer.Serialize(SupportEquivalenceQueryAnchors.Bits.Encode(q)),
            summary, [.. capture.fields]);
    }

    (SupportNeighborhoodResult, int) Neighborhood(SupportEquivalenceQueryAnchor q, IPhysicsWorld world,
        IPhysicsQueryLease lease, float slope, string section)
    {
        var elements = new SupportElement[256];
        var joins = new ulong[1024];
        var contributions = new SupportContribution[256];
        var capability = (IPhysicsSupportNeighborhood)world;
        var r = capability.QuerySupportNeighborhood(lease, new CapsuleShape(q.Radius, q.Length), q.Pose, q.Band,
            elements, joins, QueryFilter.StaticsOnly);
        prefix = section;
        I("status", "eligibility", (int)r.Status);
        I("elements", "eligibility", r.Elements);
        I("required", "eligibility", r.RequiredElements);
        I("stride", "joins", r.JoinWordsPerRow);
        for (int i = 0; i < r.Elements; i++)
        {
            prefix = section + $"element/{i}/";
            var e = elements[i];
            I("static", "identity", e.Static.Value);
            I("kind", "identity", (int)e.Kind);
            I("id", "identity", e.ElementId);
            V("normal", "normal", e.Normal);
            F("normalError", "enclosure", e.NormalError);
            V("witness", "witness", e.Witness);
            F("positionError", "enclosure", e.PositionErrorMetres);
            D("separationLower", "enclosure", e.SeparationLower);
            D("separationUpper", "enclosure", e.SeparationUpper);
        }
        prefix = section;
        int joined = 0;
        for (int i = 0; i < r.Elements * r.JoinWordsPerRow; i++)
        {
            L($"join/{i}", "joins", (long)joins[i]);
            joined += BitOperations.PopCount(joins[i]);
        }
        int count = SupportCertification.CertifyNeighborhood(capability, lease, r, elements, joins, q.Axis, slope, contributions);
        I("contributions", "eligibility", count);
        for (int i = 0; i < count; i++)
        {
            prefix = section + $"contribution/{i}/";
            var c = contributions[i];
            I("kind", "eligibility", (int)c.Kind);
            D("lower", "height", c.Lower);
            D("upper", "height", c.Upper);
            V("normal", "normal", c.Normal);
            V("witness", "witness", c.Witness);
            I("id", "identity", c.FeatureId);
        }
        return (r, joined / 2);
    }

    void Feature(CapsuleFeatureResult r)
    {
        I("status", "eligibility", (int)r.Status);
        I("written", "eligibility", r.Written);
        I("required", "eligibility", r.RequiredCapacity);
        I("target", "identity", r.Target.Value);
        I("leaf", "identity", r.LeafId);
        I("id", "identity", r.FeatureId);
        I("kind", "identity", (int)r.Kind);
        V("axis", "witness", r.AxisPoint);
        V("geometry", "witness", r.GeometryPoint);
        V("normal", "normal", r.SeparationNormal);
        D("separationLower", "enclosure", r.SeparationLower);
        D("separationUpper", "enclosure", r.SeparationUpper);
        F("positionError", "enclosure", r.PositionErrorMetres);
        F("normalError", "enclosure", r.NormalError);
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts", "Fixtures", name);
    internal const string BaselineName = "support-equivalence-fields.jsonl";
    internal static Row[] Baseline() => File.ReadLines(Fixture(BaselineName)).Select(line => JsonSerializer.Deserialize<Row>(line)!).ToArray();

    // The source and scene generator are byte-identical in the detached baseline checkout and lane C.
    // Capture checks every old digest before writing anything, so a different question cannot bless a baseline.
    internal static void Freeze()
    {
        string path = Environment.GetEnvironmentVariable("KHAOZENGINE_SUPPORT_FIELD_BASELINE_CAPTURE")!;
        Assert.False(string.IsNullOrEmpty(path));
        string[] expected = File.ReadAllLines(Fixture("support-equivalence.txt"));
        var rows = new List<Row>();
        var anchors = new SupportEquivalenceQueryAnchors();
        anchors.Generate((q, world, lease) => rows.Add(Read(q, world, lease)));
        anchors.Complete();
        Assert.Equal(expected, rows.Select(r => r.Line));
        File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)));
    }
}

public sealed class SupportEquivalenceFieldBaselineTests
{
    [Fact]
    public void FrozenEquivalenceFieldsRehashTheOriginalDigests()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KHAOZENGINE_SUPPORT_FIELD_BASELINE_CAPTURE")))
        {
            SupportEquivalenceFields.Freeze();
            return;
        }
        var rows = SupportEquivalenceFields.Baseline();
        Assert.Equal(1952, rows.Length);
        foreach (var row in rows)
        {
            var q = SupportEquivalenceQueryAnchors.Read(row.Name);
            Assert.Equal(row.Scene, q.SceneDigest);
            Assert.Equal(row.Question, JsonSerializer.Serialize(SupportEquivalenceQueryAnchors.Bits.Encode(q)));
            Assert.Equal(row.Hash, row.Line.Split(' ').Last());
        }
    }
}
