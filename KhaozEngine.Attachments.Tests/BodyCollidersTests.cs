using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class BodyCollidersTests
{
    const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Source = "body.colliders.json";
    const string Capsule = """{"joint":"upper","start":[0,1,0],"end":[0,2,0],"radius":0.125}""";
    const string File = $$"""{"formatVersion":1,"rig":"{{Rig}}","colliders":[{{Capsule}}]}""";
    static readonly ColliderDefinition Upper = new("upper", Vector3.UnitY, 2 * Vector3.UnitY, 0.125f);

    [Fact]
    public void A_body_collider_file_round_trips_to_canonical_bytes_and_hash()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(File);
        BodyColliderSet set = BodyCollidersJson.Parse(bytes, Source);

        Assert.Equal(Rig, set.RigIdentity);
        Assert.Equal(Upper, Assert.Single(set.Colliders));
        Assert.Equal(bytes, BodyCollidersJson.Write(set));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), set.Sha256);
    }

    [Fact]
    public void Collider_order_uses_ordinal_joint_then_each_coordinate_and_radius()
    {
        string[] ordered =
        [
            """{"joint":"Upper","start":[2,2,2],"end":[2,2,2],"radius":2}""",
            """{"joint":"upper","start":[0,0,0],"end":[0,0,0],"radius":1}""",
            """{"joint":"upper","start":[0,0,0],"end":[0,0,0],"radius":2}""",
            """{"joint":"upper","start":[0,0,0],"end":[0,0,1],"radius":1}""",
            """{"joint":"upper","start":[0,0,0],"end":[0,1,0],"radius":1}""",
            """{"joint":"upper","start":[0,0,0],"end":[1,0,0],"radius":1}""",
            """{"joint":"upper","start":[0,0,1],"end":[0,0,0],"radius":1}""",
            """{"joint":"upper","start":[0,1,0],"end":[0,0,0],"radius":1}""",
            """{"joint":"upper","start":[1,0,0],"end":[0,0,0],"radius":1}""",
        ];
        string canonical = Replace(File, Capsule, string.Join(",", ordered));
        string reversed = Replace(File, Capsule, string.Join(",", ordered.Reverse()));
        reversed = Replace(reversed, "[0,0,0]", "[-0,0,-0.0]");
        BodyColliderSet first = Parse(canonical);
        BodyColliderSet second = Parse(reversed);

        Assert.Equal(ordered.Length, second.Colliders.Count);
        Assert.Equal("Upper", second.Colliders[0].Joint);
        Assert.Equal(second.Colliders[0].Start, second.Colliders[0].End);
        Assert.Equal(first.Colliders.ToArray(), second.Colliders.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(canonical), BodyCollidersJson.Write(second));
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public void An_empty_file_is_admitted()
    {
        string empty = Replace(File, Capsule, "");
        BodyColliderSet set = Parse(empty);

        Assert.Empty(set.Colliders);
        Assert.Equal(Encoding.UTF8.GetBytes(empty), BodyCollidersJson.Write(set));
    }

    [Fact]
    public void The_set_owns_its_colliders_after_construction()
    {
        ColliderDefinition[] source = [Upper with { Joint = "z" }, Upper];
        var set = new BodyColliderSet(Rig, source);
        source[1] = default;

        Assert.Equal(new[] { Upper, Upper with { Joint = "z" } }, set.Colliders.ToArray());
    }

    [Theory]
    [InlineData("version", BodyCollidersRefusal.FormatVersionUnsupported, "$.formatVersion")]
    [InlineData("unknown", BodyCollidersRefusal.JsonPropertyUnknown, "$.colliders[0]", 0, "extra")]
    [InlineData("duplicate", BodyCollidersRefusal.JsonPropertyDuplicate, "$.colliders[0]", 0, "joint")]
    [InlineData("missing", BodyCollidersRefusal.JsonPropertyMissing, "$.colliders[0]", 0, "end")]
    [InlineData("point shape", BodyCollidersRefusal.JsonValueShapeInvalid, "$.colliders[0].start", 0)]
    [InlineData("point component shape", BodyCollidersRefusal.JsonValueShapeInvalid, "$.colliders[0].start[1]", 0)]
    [InlineData("truncated", BodyCollidersRefusal.JsonMalformed, null)]
    [InlineData("rig", BodyCollidersRefusal.RigIdentityInvalid, "$.rig")]
    [InlineData("blank joint", BodyCollidersRefusal.JointNameEmpty, "$.colliders[0].joint", 0, null, " ")]
    [InlineData("malformed joint", BodyCollidersRefusal.JointNameMalformedUtf16, "$.colliders[0].joint", 0)]
    [InlineData("infinite start", BodyCollidersRefusal.PointInvalid, "$.colliders[0].start", 0, null, "upper")]
    [InlineData("infinite end", BodyCollidersRefusal.PointInvalid, "$.colliders[0].end", 0, null, "upper")]
    [InlineData("zero radius", BodyCollidersRefusal.RadiusInvalid, "$.colliders[0].radius", 0, null, "upper")]
    [InlineData("infinite radius", BodyCollidersRefusal.RadiusInvalid, "$.colliders[0].radius", 0, null, "upper")]
    [InlineData("too many", BodyCollidersRefusal.TooManyColliders, "$.colliders")]
    public void A_body_collider_file_reports_the_rule_it_breaks(
        string fault, BodyCollidersRefusal reason, string? path, int? index = null, string? property = null, string? joint = null)
    {
        (string control, string broken, int count) = JsonCase(fault);
        Assert.Equal(count, Parse(control).Colliders.Count);

        var error = Assert.Throws<BodyCollidersDataException>(() => Parse(broken));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(Source, error.SourceName);
        Assert.Equal((path, index, property, joint), (error.JsonPath, error.ColliderIndex, error.PropertyName, error.JointName));
        Assert.Equal(reason == BodyCollidersRefusal.FormatVersionUnsupported ? 2 : (int?)null, error.FormatVersion);
        Assert.Equal(reason == BodyCollidersRefusal.RigIdentityInvalid ? Replace(Rig, "abcdef", "ABCDEF") : null, error.RigIdentity);
        Assert.Equal(reason == BodyCollidersRefusal.TooManyColliders ? 33 : (int?)null, error.ColliderCount);
    }

    [Fact]
    public void Invalid_utf8_is_a_malformed_file()
    {
        byte[] broken = Encoding.UTF8.GetBytes(File);
        Assert.Single(BodyCollidersJson.Parse(broken, Source).Colliders);
        broken[File.IndexOf("upper", StringComparison.Ordinal)] = 0xff;

        var error = Assert.Throws<BodyCollidersDataException>(() => BodyCollidersJson.Parse(broken, Source));

        Assert.Equal(BodyCollidersRefusal.JsonMalformed, error.Reason);
        Assert.Equal(Source, error.SourceName);
    }

    [Theory]
    [InlineData("rig", BodyCollidersRefusal.RigIdentityInvalid, "$.rig")]
    [InlineData("blank joint", BodyCollidersRefusal.JointNameEmpty, "$.colliders[1].joint", 1)]
    [InlineData("malformed joint", BodyCollidersRefusal.JointNameMalformedUtf16, "$.colliders[1].joint", 1)]
    [InlineData("invalid start", BodyCollidersRefusal.PointInvalid, "$.colliders[1].start", 1)]
    [InlineData("invalid end", BodyCollidersRefusal.PointInvalid, "$.colliders[1].end", 1)]
    [InlineData("negative radius", BodyCollidersRefusal.RadiusInvalid, "$.colliders[1].radius", 1)]
    [InlineData("nonfinite radius", BodyCollidersRefusal.RadiusInvalid, "$.colliders[1].radius", 1)]
    [InlineData("too many", BodyCollidersRefusal.TooManyColliders, "$.colliders")]
    public void The_constructor_reports_the_content_rule_it_breaks(
        string fault, BodyCollidersRefusal reason, string path, int? index = null)
    {
        int count = fault == "too many" ? 32 : 2;
        Assert.Equal(count, new BodyColliderSet(Rig, Enumerable.Repeat(Upper, count)).Colliders.Count);
        string rig = fault == "rig" ? Replace(Rig, "rig-v1:", "rig-v2:") : Rig;
        ColliderDefinition last = fault switch
        {
            "blank joint" => Upper with { Joint = "" },
            "malformed joint" => Upper with { Joint = "upper\udc00" },
            "invalid start" => Upper with { Start = new(float.NaN, 0, 0) },
            "invalid end" => Upper with { End = new(0, 0, float.PositiveInfinity) },
            "negative radius" => Upper with { Radius = -1 },
            "nonfinite radius" => Upper with { Radius = float.NaN },
            _ => Upper,
        };
        int prefixCount = fault == "too many" ? count : count - 1;

        var error = Assert.Throws<BodyCollidersDataException>(() =>
            new BodyColliderSet(rig, Enumerable.Repeat(Upper, prefixCount).Append(last)));

        Assert.Equal(reason, error.Reason);
        Assert.Null(error.SourceName);
        Assert.Equal((path, index), (error.JsonPath, error.ColliderIndex));
        Assert.Equal(index.HasValue ? last.Joint : null, error.JointName);
        Assert.Equal(reason == BodyCollidersRefusal.RigIdentityInvalid ? rig : null, error.RigIdentity);
        Assert.Equal(reason == BodyCollidersRefusal.TooManyColliders ? 33 : (int?)null, error.ColliderCount);
    }

    [Fact]
    public void The_constructor_reads_an_uncounted_sequence_only_one_past_the_cap()
    {
        Assert.Equal(BodyColliderSet.MaxColliders,
            new BodyColliderSet(Rig, Uncounted(BodyColliderSet.MaxColliders)).Colliders.Count);

        var error = Assert.Throws<BodyCollidersDataException>(() => new BodyColliderSet(Rig, Uncounted(int.MaxValue)));

        Assert.Equal(BodyCollidersRefusal.TooManyColliders, error.Reason);
        Assert.Equal(BodyColliderSet.MaxColliders + 1, error.ColliderCount);
    }

    // An iterator cannot report its size. Reading more than one item past the cap fails the test.
    static IEnumerable<ColliderDefinition> Uncounted(int count)
    {
        for (int read = 0; read < count; read++)
        {
            Assert.True(read <= BodyColliderSet.MaxColliders);
            yield return Upper;
        }
    }

    static (string Control, string Broken, int Count) JsonCase(string fault) => fault switch
    {
        "version" => (File, Replace(File, "\"formatVersion\":1", "\"formatVersion\":2"), 1),
        "unknown" => (File, Replace(File, "\"joint\":", "\"extra\":0,\"joint\":"), 1),
        "duplicate" => (File, Replace(File, "\"joint\":", "\"joint\":\"upper\",\"joint\":"), 1),
        "missing" => (File, Replace(File, "\"end\":[0,2,0],", ""), 1),
        "point shape" => (File, Replace(File, "[0,1,0]", "[0,1]"), 1),
        "point component shape" => (File, Replace(File, "[0,1,0]", """[0,"1",0]"""), 1),
        "truncated" => (File, File[..^1], 1),
        "rig" => (File, Replace(File, "abcdef", "ABCDEF"), 1),
        "blank joint" => (File, Replace(File, "\"upper\"", "\" \""), 1),
        "malformed joint" => (Replace(File, "upper", """upper\ud83d\ude00"""), Replace(File, "upper", """upper\ud800"""), 1),
        "infinite start" => (File, Replace(File, "[0,1,0]", "[0,1e100,0]"), 1),
        "infinite end" => (File, Replace(File, "[0,2,0]", "[0,-2e100,0]"), 1),
        "zero radius" => (File, Replace(File, "0.125", "0"), 1),
        "infinite radius" => (File, Replace(File, "0.125", "1e100"), 1),
        "too many" => (RepeatCapsule(32), RepeatCapsule(33), 32),
        _ => throw new ArgumentException(fault),
    };

    static string RepeatCapsule(int count) => Replace(File, Capsule, string.Join(",", Enumerable.Repeat(Capsule, count)));
    static string Replace(string text, string from, string to) => text.Replace(from, to, StringComparison.Ordinal);
    static BodyColliderSet Parse(string json) => BodyCollidersJson.Parse(Encoding.UTF8.GetBytes(json), Source);
}
