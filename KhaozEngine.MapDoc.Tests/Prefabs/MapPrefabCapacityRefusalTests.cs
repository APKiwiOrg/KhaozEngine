using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabCapacityRefusalTests
{
    static readonly MapPrefabExpansionLimits SmallLimits = new(MaxInstances: 8, MaxChildren: 8,
        MaxReferenceEdges: 8, MaxPatches: 8, MaxBindings: 8, MaxOverrides: 8, MaxTransfers: 8,
        MaxGeometryElements: 32);

    public static TheoryData<string> Collections => new()
    {
        "instances", "children", "references", "patches", "entries", "overrides", "transfers",
        "attachments", "materializedAttachments", "other",
    };

    [Theory]
    [InlineData("Light")]
    [InlineData("Prefab")]
    [InlineData("Value")]
    public void PayloadBytes_RetainTheMeasuredCountAndInclusiveLimit(string kind)
    {
        var asset = PrefabFixtures.Create("roomless").Assets.GetPrefab("hut");
        byte[] payload = kind == "Light" ? MapLightFile.Write(PrefabFixtures.GlowLight())
            : kind == "Prefab" ? MapPrefabFile.Write(asset) : Encoding.UTF8.GetBytes("\"value\"");
        long limit = payload.Length + 1;
        string? resourceId = kind == "Light" ? PrefabFixtures.GlowLight().Id : kind == "Prefab" ? asset.Id : null;
        void Read(int padding)
        {
            byte[] bytes = kind == "Value" ? Encoding.UTF8.GetBytes("\"value" + new string('x', padding) + "\"")
                : [.. payload, .. Enumerable.Repeat((byte)' ', padding)];
            if (kind == "Light") MapLightFile.ReadWithPayloadByteLimitForTests(bytes, resourceId!, limit);
            else if (kind == "Prefab") MapPrefabFile.ReadWithPayloadByteLimitForTests(bytes, resourceId!, limit);
            else
            {
                using var json = JsonDocument.Parse(bytes);
                MapPrefabJson.ReadWithPayloadByteLimitForTests<JsonElement>(json.RootElement, limit);
            }
        }
        var error = Assert.Throws<MapDocumentException>(() => Read(2));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("PayloadBytes", error.Budget);
        Assert.Equal(limit + 1, error.ActualByteCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("ByteLength", error.FieldPath);
        Assert.Equal(resourceId, error.ResourceId);
        Read(1);
        Read(0);
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void CollectionCounts_AcceptTheExactNamedBudget(string name)
    {
        int limit = name == "other" ? SmallLimits.MaxGeometryElements : SmallLimits.MaxInstances;
        bool accumulated = name is not ("references" or "patches" or "other");
        void Preflight(int count)
        {
            using var json = JsonDocument.Parse("{\"" + name + "\":[" + string.Join(',', Enumerable.Repeat("0", count)) + "]}");
            MapPrefabJson.PreflightWithCapacityLimitsForTests(json.RootElement, SmallLimits);
        }
        var error = Assert.Throws<MapDocumentException>(() => Preflight(limit + 1));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(accumulated ? "Aggregate." + name : name, error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("$." + name, error.FieldPath);
        Preflight(limit);
        Preflight(limit - 1);
    }

    [Theory]
    [InlineData("instances")]
    [InlineData("children")]
    [InlineData("entries")]
    [InlineData("overrides")]
    [InlineData("transfers")]
    [InlineData("attachments")]
    [InlineData("materializedAttachments")]
    public void AggregateCounts_DistinguishTheAccumulatedBudget(string name)
    {
        int limit = SmallLimits.MaxInstances;
        void Preflight(int count)
        {
            string first = string.Join(',', Enumerable.Repeat("0", limit / 2));
            string second = string.Join(',', Enumerable.Repeat("0", count - limit / 2));
            using var json = JsonDocument.Parse("{\"first\":{\"" + name + "\":[" + first + "]},\"nested\":{\"" + name + "\":[" + second + "]}}");
            MapPrefabJson.PreflightWithCapacityLimitsForTests(json.RootElement, SmallLimits);
        }
        var error = Assert.Throws<MapDocumentException>(() => Preflight(limit + 1));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("Aggregate." + name, error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("$.nested." + name, error.FieldPath);
        Preflight(limit);
        Preflight(limit - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeometryPreflight_ChargesPatchCellsCornersAndArrayMembershipAtTheExactCap(bool aggregate)
    {
        long limit = MapPrefabJson.Limits.MaxGeometryElements;
        // A 64 by 64 patch contributes 65*65 + 64*64 = 8321, plus its array membership.
        // A 1 by 1 patch contributes 4 + 1, plus membership. Scalar members contribute membership alone.
        int large = (int)(limit / 8322), small = (int)(limit % 8322 / 6), scalars = (int)(limit % 8322 % 6);
        string body = string.Join(',', Enumerable.Repeat("{\"width\":64,\"depth\":64}", large)
            .Concat(Enumerable.Repeat("{\"width\":1,\"depth\":1}", small)).Concat(Enumerable.Repeat("0", scalars)));
        using var refused = JsonDocument.Parse(aggregate ? "{\"a\":[" + body + "],\"b\":[0]}" : "[" + body + ",0]");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Preflight(refused.RootElement));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(aggregate ? "Aggregate.GeometryElements" : "GeometryElements", error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        using var control = JsonDocument.Parse("[" + body + "]");
        MapPrefabJson.Preflight(control.RootElement);
        using var below = JsonDocument.Parse("[" + body[..body.LastIndexOf(',')] + "]");
        MapPrefabJson.Preflight(below.RootElement);
    }

    [Theory]
    [InlineData("references", "ReferenceEdges")]
    [InlineData("patches", "Patches")]
    [InlineData("records", "Records")]
    public void CrossArrayBudgets_KeepTheActualAggregateThatFailed(string name, string budget)
    {
        int limit = name switch
        {
            "references" => MapPrefabJson.Limits.MaxReferenceEdges,
            "patches" => MapPrefabJson.Limits.MaxPatches,
            _ => MapPrefabJson.Limits.MaxRecords,
        };
        string part = string.Join(',', Enumerable.Repeat("0", limit / 2));
        using var refused = JsonDocument.Parse("{\"a\":{\"" + name + "\":[" + part + "]},\"b\":{\"" + name + "\":[" + part + ",0]}}");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Preflight(refused.RootElement));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("Aggregate." + budget, error.Budget);
        Assert.Equal((long)limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("$.b." + name, error.FieldPath);
        using var control = JsonDocument.Parse("{\"a\":{\"" + name + "\":[" + part + "]},\"b\":{\"" + name + "\":[" + part + "]}}");
        MapPrefabJson.Preflight(control.RootElement);
        using var below = JsonDocument.Parse("{\"a\":{\"" + name + "\":[" + part + "]},\"b\":{\"" + name + "\":[" + part[2..] + "]}}");
        MapPrefabJson.Preflight(below.RootElement);
    }

    [Fact]
    public void AggregatePreflight_RetainsTheArrayThatCrossedTheTotal()
    {
        int limit = MapPrefabJson.Limits.MaxInstances;
        string part = string.Join(',', Enumerable.Repeat("0", limit / 2));
        using var refused = JsonDocument.Parse("{\"a\":{\"instances\":[" + part + "]},\"b\":{\"instances\":[" + part + ",0]}}");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabJson.Preflight(refused.RootElement));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("Aggregate.instances", error.Budget);
        Assert.Equal("$.b.instances", error.FieldPath);
        Assert.Equal((long)limit + 1, error.ActualCount);
        Assert.Equal((long)limit, error.Limit);
        using var control = JsonDocument.Parse("{\"a\":{\"instances\":[" + part + "]},\"b\":{\"instances\":[" + part + "]}}");
        MapPrefabJson.Preflight(control.RootElement);
        using var below = JsonDocument.Parse("{\"a\":{\"instances\":[" + part + "]},\"b\":{\"instances\":[" + part[2..] + "]}}");
        MapPrefabJson.Preflight(below.RootElement);
    }
}
