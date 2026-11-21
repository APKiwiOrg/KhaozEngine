using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class BodyRegionMapTests
{
    const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Source = "body.regions.json";
    const string Map = $$"""{"formatVersion":1,"rig":"{{Rig}}","regions":["a","b"],"parts":[{"triangles":4,"runs":[[0,3],[1,1]]}]}""";
    const string TwoParts = $$"""{"formatVersion":1,"rig":"{{Rig}}","regions":["a","b"],"parts":[{"triangles":4,"runs":[[0,3],[1,1]]},{"triangles":2,"runs":[[1,2]]}]}""";

    [Fact]
    public void A_map_round_trips_and_names_every_triangle_once()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Map);
        BodyRegionMap map = BodyRegionMapJson.Parse(bytes, Source);

        Assert.Equal(Rig, map.RigIdentity);
        Assert.Equal(new[] { "a", "b" }, map.Regions);
        Assert.Equal(new[] { new RegionRun(0, 3), new RegionRun(1, 1) }, Assert.Single(map.Parts));
        Assert.True(map.TryRegion("b", out int b));
        Assert.Equal(1, b);
        Assert.False(map.TryRegion("B", out _));

        byte[] perTriangle = new byte[4];
        map.WriteTriangleRegions(0, perTriangle);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, perTriangle);
        Assert.Throws<ArgumentException>(() => map.WriteTriangleRegions(0, new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WriteTriangleRegions(1, perTriangle));

        Assert.Equal(bytes, BodyRegionMapJson.Write(map));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), map.Sha256);
    }

    [Fact]
    public void Each_part_of_a_two_part_map_names_its_own_triangles()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(TwoParts);
        BodyRegionMap map = BodyRegionMapJson.Parse(bytes, Source);

        Assert.Equal((4, 2), (map.TriangleCount(0), map.TriangleCount(1)));
        byte[] first = new byte[4];
        byte[] second = new byte[2];
        map.WriteTriangleRegions(0, first);
        map.WriteTriangleRegions(1, second);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, first);
        Assert.Equal(new byte[] { 1, 1 }, second);
        Assert.Throws<ArgumentException>(() => map.WriteTriangleRegions(1, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.TriangleCount(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.TriangleCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WriteTriangleRegions(2, second));
        Assert.Equal(bytes, BodyRegionMapJson.Write(map));
    }

    [Fact]
    public void A_part_whose_runs_pass_int32_is_refused_and_one_at_int32_max_is_kept()
    {
        var first = new[] { new RegionRun(0, 3), new RegionRun(1, 1) };
        var atMax = new BodyRegionMap(Rig, new[] { "a", "b" }, new[] { first, new[] { new RegionRun(0, int.MaxValue - 1), new RegionRun(1, 1) } });
        Assert.Equal(int.MaxValue, atMax.TriangleCount(1));

        var error = Assert.Throws<BodyRegionMapDataException>(() =>
            new BodyRegionMap(Rig, new[] { "a", "b" }, new[] { first, new[] { new RegionRun(0, int.MaxValue), new RegionRun(1, 1) } }));
        Assert.Equal(BodyRegionMapRefusal.TriangleCountOverflow, error.Reason);
        Assert.Equal((1, 1, int.MaxValue + 1L), (error.PartIndex, error.RunIndex, error.ActualTriangleCount));
    }

    [Fact]
    public void Adjacent_runs_of_one_region_merge_so_equal_maps_share_an_identity()
    {
        BodyRegionMap split = BodyRegionMapJson.Parse(Encoding.UTF8.GetBytes(Map.Replace("[[0,3],", "[[0,1],[0,2],", StringComparison.Ordinal)), Source);

        Assert.Equal(new[] { new RegionRun(0, 3), new RegionRun(1, 1) }, split.Parts[0]);
        Assert.Equal(Encoding.UTF8.GetBytes(Map), BodyRegionMapJson.Write(split));
        Assert.Equal(BodyRegionMapJson.Parse(Encoding.UTF8.GetBytes(Map), Source).Sha256, split.Sha256);
    }

    [Theory]
    [InlineData("runs sum to 3 for 4 triangles", BodyRegionMapRefusal.TriangleCountMismatch)]
    [InlineData("the second part's runs sum to 1 for 2 triangles", BodyRegionMapRefusal.TriangleCountMismatch)]
    [InlineData("run names region 2 of 2", BodyRegionMapRefusal.RegionIndexOutOfRange)]
    [InlineData("two regions named a", BodyRegionMapRefusal.RegionNameDuplicate)]
    [InlineData("129 regions", BodyRegionMapRefusal.TooManyRegions)]
    [InlineData("empty region name", BodyRegionMapRefusal.RegionNameEmpty)]
    [InlineData("a run of zero triangles", BodyRegionMapRefusal.RunEmpty)]
    [InlineData("rig is not a rig-v1 identity", BodyRegionMapRefusal.RigIdentityInvalid)]
    [InlineData("formatVersion 2", BodyRegionMapRefusal.FormatVersionUnsupported)]
    [InlineData("an unknown property", BodyRegionMapRefusal.JsonPropertyUnknown)]
    [InlineData("a repeated property", BodyRegionMapRefusal.JsonPropertyDuplicate)]
    [InlineData("parts is missing", BodyRegionMapRefusal.JsonPropertyMissing)]
    [InlineData("a run of three numbers", BodyRegionMapRefusal.JsonValueShapeInvalid)]
    [InlineData("truncated text", BodyRegionMapRefusal.JsonMalformed)]
    public void A_bad_map_is_refused_with_its_reason(string fault, BodyRegionMapRefusal reason)
    {
        (string control, string broken) = Case(fault);
        BodyRegionMapJson.Parse(Encoding.UTF8.GetBytes(control), Source);

        var error = Assert.Throws<BodyRegionMapDataException>(() => BodyRegionMapJson.Parse(Encoding.UTF8.GetBytes(broken), Source));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(Source, error.SourceName);
        switch (fault)
        {
            case "runs sum to 3 for 4 triangles":
                Assert.Equal(("$.parts[0]", 0), (error.JsonPath, error.PartIndex));
                Assert.Equal(4, error.DeclaredTriangleCount);
                Assert.Equal(3L, error.ActualTriangleCount);
                break;
            case "the second part's runs sum to 1 for 2 triangles":
                Assert.Equal(("$.parts[1]", 1, 2, 1L), (error.JsonPath, error.PartIndex, error.DeclaredTriangleCount, error.ActualTriangleCount));
                break;
            case "run names region 2 of 2":
                Assert.Equal((0, 1, 2, 2), (error.PartIndex, error.RunIndex, error.RegionIndex, error.RegionCount));
                break;
            case "two regions named a":
                Assert.Equal(("a", 1), (error.RegionName, error.RegionIndex));
                break;
            case "129 regions":
                Assert.Equal(129, error.RegionCount);
                break;
            case "empty region name":
                Assert.Equal(1, error.RegionIndex);
                break;
            case "a run of zero triangles":
                Assert.Equal((0, 1), (error.PartIndex, error.RunIndex));
                break;
            case "rig is not a rig-v1 identity":
                Assert.Equal(Rig.Replace("rig-v1", "rig-v2", StringComparison.Ordinal), error.RigIdentity);
                break;
            case "formatVersion 2":
                Assert.Equal(("$.formatVersion", 2), (error.JsonPath, error.FormatVersion));
                break;
            case "an unknown property":
                Assert.Equal(("$", "extra"), (error.JsonPath, error.PropertyName));
                break;
            case "a repeated property":
                Assert.Equal(("$", "rig"), (error.JsonPath, error.PropertyName));
                break;
            case "parts is missing":
                Assert.Equal(("$", "parts"), (error.JsonPath, error.PropertyName));
                break;
            case "a run of three numbers":
                Assert.Equal("$.parts[0].runs[1]", error.JsonPath);
                break;
        }
    }

    [Fact]
    public void A_region_name_with_a_lone_surrogate_is_refused_because_canonical_json_cannot_write_it()
    {
        var parts = new[] { new[] { new RegionRun(0, 3), new RegionRun(1, 1) } };
        Assert.Equal(Map, Encoding.UTF8.GetString(BodyRegionMapJson.Write(new BodyRegionMap(Rig, new[] { "a", "b" }, parts))));

        var error = Assert.Throws<BodyRegionMapDataException>(() => new BodyRegionMap(Rig, new[] { "a", "\ud800" }, parts));
        Assert.Equal(BodyRegionMapRefusal.RegionNameMalformedUtf16, error.Reason);
        Assert.Equal(1, error.RegionIndex);
    }

    static (string Control, string Broken) Case(string fault) => fault switch
    {
        "runs sum to 3 for 4 triangles" => (Map, Map.Replace("[[0,3],", "[[0,2],", StringComparison.Ordinal)),
        "the second part's runs sum to 1 for 2 triangles" => (TwoParts, TwoParts.Replace("[[1,2]]", "[[1,1]]", StringComparison.Ordinal)),
        "run names region 2 of 2" => (Map, Map.Replace("[1,1]]", "[2,1]]", StringComparison.Ordinal)),
        "two regions named a" => (Map, Map.Replace("[\"a\",\"b\"]", "[\"a\",\"a\"]", StringComparison.Ordinal)),
        "129 regions" => (Regions(RegionSet.MaxRegions), Regions(RegionSet.MaxRegions + 1)),
        "empty region name" => (Map, Map.Replace("[\"a\",\"b\"]", "[\"a\",\"\"]", StringComparison.Ordinal)),
        "a run of zero triangles" => (Map, Map.Replace("[[0,3],[1,1]]", "[[0,4],[1,0]]", StringComparison.Ordinal)),
        "rig is not a rig-v1 identity" => (Map, Map.Replace("rig-v1:", "rig-v2:", StringComparison.Ordinal)),
        "formatVersion 2" => (Map, Map.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal)),
        "an unknown property" => (Map, Map.Replace("\"parts\":", "\"extra\":0,\"parts\":", StringComparison.Ordinal)),
        "a repeated property" => (Map, Map.Replace("\"regions\":", $"\"rig\":\"{Rig}\",\"regions\":", StringComparison.Ordinal)),
        "parts is missing" => (Map, Map[..Map.IndexOf(",\"parts\"", StringComparison.Ordinal)] + "}"),
        "a run of three numbers" => (Map, Map.Replace("[1,1]]", "[1,1,1]]", StringComparison.Ordinal)),
        "truncated text" => (Map, Map[..^1]),
        _ => throw new ArgumentException(fault),
    };

    static string Regions(int count)
    {
        string names = string.Join(",", Enumerable.Range(0, count).Select(index => $"\"r{index}\""));
        return Map.Replace("[\"a\",\"b\"]", $"[{names}]", StringComparison.Ordinal);
    }
}
