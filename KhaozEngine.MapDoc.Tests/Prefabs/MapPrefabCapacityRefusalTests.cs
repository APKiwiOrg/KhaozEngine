using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabCapacityRefusalTests
{
    public static TheoryData<string, long> Collections => new()
    {
        { "instances", MapPrefabJson.Limits.MaxInstances },
        { "children", MapPrefabJson.Limits.MaxChildren },
        { "references", MapPrefabJson.Limits.MaxReferenceEdges },
        { "patches", MapPrefabJson.Limits.MaxPatches },
        { "entries", MapPrefabJson.Limits.MaxBindings },
        { "overrides", MapPrefabJson.Limits.MaxOverrides },
        { "transfers", MapPrefabJson.Limits.MaxTransfers },
        { "attachments", MapPrefabJson.Limits.MaxTransfers },
        { "materializedAttachments", MapPrefabJson.Limits.MaxTransfers },
        { "other", MapPrefabJson.Limits.MaxGeometryElements },
    };

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Light", true)]
    [InlineData("Prefab", false)]
    [InlineData("Prefab", true)]
    public void PayloadBytes_RetainTheMeasuredCountAndInclusiveLimit(string kind, bool resource)
    {
        long limit = MapPrefabJson.Limits.MaxPayloadBytes;
        string? resourceId = resource ? "payload-resource" : null;
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabCapacity.RequirePayloadBytes(limit + 1, kind, resourceId));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("PayloadBytes", error.Budget);
        Assert.Equal(limit + 1, error.ActualByteCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("ByteLength", error.FieldPath);
        Assert.Equal(resourceId, error.ResourceId);
        MapPrefabCapacity.RequirePayloadBytes(limit, kind, resourceId);
        // Exercise the public producers with real, small payloads. The same value-taking guard tests their large limit.
        if (kind == "Light")
        {
            var light = PrefabFixtures.GlowLight();
            Assert.Equal(light, MapLightFile.Read(MapLightFile.Write(light), light.Id));
        }
        else
        {
            var asset = PrefabFixtures.Create("roomless").Assets.GetPrefab("hut");
            Assert.Equal(asset.Id, MapPrefabFile.Read(MapPrefabFile.Write(asset), asset.Id).Id);
        }
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public void CollectionCounts_AcceptTheExactNamedBudget(string name, long limit)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabCapacity.RequireCollection(limit + 1, name, "$." + name, false));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(name, error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("$." + name, error.FieldPath);
        MapPrefabCapacity.RequireCollection(limit, name, "$." + name, false);
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
        long limit = Collections.Single(row => (string)row[0] == name)[1] is long value ? value : 0;
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabCapacity.RequireCollection(limit + 1, name, "$.nested." + name, true));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("Aggregate." + name, error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.Equal("$.nested." + name, error.FieldPath);
        MapPrefabCapacity.RequireCollection(limit, name, "$.nested." + name, true);
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
    }
}
