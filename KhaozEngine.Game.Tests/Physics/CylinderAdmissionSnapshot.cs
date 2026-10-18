using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Captured before the admission shortcut. Floating point payloads include NaNs and signed zeros.
internal static class CylinderAdmissionSnapshot
{
    internal static string Before(bool capsule, uint bits)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "cylinder-admission-before.jsonl");
        Assert.Equal("8175D7755A57577628F11F09A6ADAF29F9A0285887852C121EF91A9D84996980",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        using var row = JsonDocument.Parse(File.ReadLines(path).Single(line =>
        {
            using var candidate = JsonDocument.Parse(line);
            return candidate.RootElement.GetProperty("Key").GetString() == $"{capsule}-{bits:x8}";
        }));
        Assert.Equal("a251a3bf449baedf23529046075d7b8f1ffb5034", row.RootElement.GetProperty("ProductionCommit").GetString());
        return row.RootElement.GetProperty("Bits").GetRawText();
    }

    internal static string Read(bool capsule, float x)
    {
        var (scene, _, _) = ContactFollowupScenes.Log(capsule, 0.3f, false);
        using (scene)
        using (var lease = scene.World.AcquireQueryReadLease())
        {
            var query = new FootSupportQuery(new(x, 0), 0.6f, 0.2f, 0.4f, 0.4f,
                ContactFollowupTraversalTests.CosMaxSlope);
            var request = FootSupport.DescribeFootprint(query);
            var sample = SupportFootprintEquivalence.AssertMatchesReference(() =>
                FootSupport.Find(null, scene.World, lease, query));
            var result = SupportFootprintEquivalence.AssertMatchesReference(scene.World, lease, request);
            var members = new SupportFootprintMember[256];
            var joins = new ulong[1024];
            result = scene.World.QuerySupportFootprint(lease, request, members, joins);
            Assert.Same(scene.World, result.QueryWorld);
            Assert.Same(lease.SourceWorld, result.SourceWorld);
            Assert.Same(lease, result.Lease);
            long warmBytes = AllocationMeasurement.Measure(nameof(CylinderAdmissionSnapshot), () =>
                FootSupport.Find(null, scene.World, lease, query));
            Assert.Equal(0, warmBytes);
            var options = new JsonSerializerOptions { IncludeFields = true };
            options.Converters.Add(new FloatBits());
            options.Converters.Add(new DoubleBits());
            return JsonSerializer.Serialize(new
            {
                query.Axis,
                Result = new
                {
                    result.Status, result.Members, result.Witnesses, result.RequiredElements,
                    result.JoinWordsPerRow, result.Origin, result.GeometryGeneration,
                    result.AxisVerdict, result.FootVerdict,
                },
                Members = members.Take(result.Members),
                Joins = joins.Take(result.Members * result.JoinWordsPerRow),
                Sample = new
                {
                    sample.Status, sample.Height, sample.HeightError, sample.Normal,
                    Static = sample.Static?.Value, sample.FeatureId, sample.Witness,
                },
            }, options);
        }
    }

    sealed class FloatBits : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
            writer.WriteStringValue(unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("x8"));
    }

    sealed class DoubleBits : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteStringValue(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16"));
    }
}
