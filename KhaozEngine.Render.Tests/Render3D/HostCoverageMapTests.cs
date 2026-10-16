using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Strict format 1 host coverage sidecar: canonical bytes, domain-separated semantic identity, refusals and
/// immutable snapshots. All data is synthetic.</summary>
public class HostCoverageMapTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Surface 1 is a welded logical vertex shared by two render vertices on different primitives.
    const string Canonical =
        "{\"formatVersion\":1,\"hostSha256\":\"" + Hash + "\"," +
        "\"topology\":[{\"mesh\":0,\"primitive\":0,\"vertex\":0,\"surfaceId\":0},{\"mesh\":0,\"primitive\":0,\"vertex\":1,\"surfaceId\":1}," +
        "{\"mesh\":0,\"primitive\":1,\"vertex\":0,\"surfaceId\":1},{\"mesh\":0,\"primitive\":1,\"vertex\":1,\"surfaceId\":2}]," +
        "\"regions\":[{\"key\":\"back\",\"triangles\":[{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":1,\"role\":\"collar\"}]," +
        "\"strengths\":[{\"surfaceId\":1,\"strength\":0},{\"surfaceId\":2,\"strength\":0.5}]}," +
        "{\"key\":\"chest\",\"triangles\":[{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":2,\"role\":\"core\"}," +
        "{\"mesh\":0,\"primitive\":0,\"firstTriangle\":2,\"count\":1,\"role\":\"collar\"}]," +
        "\"strengths\":[{\"surfaceId\":0,\"strength\":1},{\"surfaceId\":1,\"strength\":0}]}]," +
        "\"refinement\":[{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":3,\"level\":2}]," +
        "\"authoring\":{\"exporter\":\"synthetic-exporter\",\"revision\":\"r1\"}}";

    // Same semantics: different member order, unsorted arrays, whitespace and a negative zero strength.
    const string Reordered =
        " {\"authoring\":{\"revision\":\"r1\",\"exporter\":\"synthetic-exporter\"}," +
        "\"refinement\":[{\"level\":2,\"count\":3,\"firstTriangle\":0,\"primitive\":0,\"mesh\":0}]," +
        "\"regions\":[{\"strengths\":[{\"strength\":-0,\"surfaceId\":1},{\"surfaceId\":0,\"strength\":1.0}]," +
        "\"triangles\":[{\"role\":\"collar\",\"mesh\":0,\"primitive\":0,\"firstTriangle\":2,\"count\":1}," +
        "{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":2,\"role\":\"core\"}],\"key\":\"chest\"}," +
        "{\"key\":\"back\",\"triangles\":[{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":1,\"role\":\"collar\"}]," +
        "\"strengths\":[{\"surfaceId\":2,\"strength\":5e-1},{\"surfaceId\":1,\"strength\":0}]}]," +
        "\"topology\":[{\"surfaceId\":2,\"vertex\":1,\"primitive\":1,\"mesh\":0},{\"mesh\":0,\"primitive\":1,\"vertex\":0,\"surfaceId\":1}," +
        "{\"mesh\":0,\"primitive\":0,\"vertex\":1,\"surfaceId\":1},{\"mesh\":0,\"primitive\":0,\"vertex\":0,\"surfaceId\":0}]," +
        "\"hostSha256\":\"" + Hash + "\",\"formatVersion\":1}\n";

    [Fact]
    public void Canonical_round_trip_has_fixed_bytes()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        HostCoverageMap map = HostCoverageMapJson.Read(canonical, "host");
        Assert.Equal(canonical, HostCoverageMapJson.Write(map));

        HostCoverageMap reordered = HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Reordered), "host-reordered");
        Assert.Equal(Canonical, Encoding.UTF8.GetString(HostCoverageMapJson.Write(reordered)));

        Assert.Equal(Hash, map.HostSha256);
        Assert.Equal(new[] { "back", "chest" }, map.Regions.Select(region => region.Key));
        Assert.Equal(new CoverageTopologyEntry(0, 1, 0, 1), map.Topology[2]);
        HostCoverageRegion chest = map.Regions[1];
        Assert.Equal(new HostCoverageRegionTriangles(new CoverageTriangleRange(0, 0, 0, 2), CoverageTriangleRole.Core), chest.Triangles[0]);
        Assert.Equal(new CoverageSurfaceStrength(0, 1f), chest.Strengths[0]);
        Assert.Equal(new HostCoverageRefinement(new CoverageTriangleRange(0, 0, 0, 3), 2), Assert.Single(map.Refinement));
        Assert.Equal(new HostCoverageAuthoring("synthetic-exporter", "r1"), map.Authoring);
    }

    [Fact]
    public void Semantic_digest_is_domain_separated_and_independent_of_byte_hash()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        byte[] reorderedBytes = Encoding.UTF8.GetBytes(Reordered);
        string semantic = HostCoverageMapJson.SemanticSha256(HostCoverageMapJson.Read(canonical, "host"));

        byte[] prefixed = [.. Encoding.UTF8.GetBytes("KhaozEngine.HostCoverageMap/1\n"), .. canonical];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(prefixed)), semantic);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), semantic);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(canonical)), Convert.ToHexStringLower(SHA256.HashData(reorderedBytes)));
        Assert.Equal(semantic, HostCoverageMapJson.SemanticSha256(HostCoverageMapJson.Read(reorderedBytes, "host-reordered")));

        HostCoverageMap retagged = HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Canonical.Replace("\"r1\"", "\"r2\"", StringComparison.Ordinal)), "host");
        Assert.NotEqual(semantic, HostCoverageMapJson.SemanticSha256(retagged));
    }

    [Theory]
    [InlineData("not-object", "root: expected an object")]
    [InlineData("trailing-comma", "trailing comma")]
    [InlineData("unknown-root", "root: unknown property 'extra'")]
    [InlineData("unknown-topology", "topology[0]: unknown property 'extra'")]
    [InlineData("unknown-region", "regions[0]: unknown property 'extra'")]
    [InlineData("unknown-triangles", "triangles[0]: unknown property 'extra'")]
    [InlineData("unknown-strength", "strengths[1]: unknown property 'extra'")]
    [InlineData("unknown-refinement", "refinement[0]: unknown property 'extra'")]
    [InlineData("unknown-authoring", "authoring: unknown property 'extra'")]
    [InlineData("duplicate-root", "duplicate property 'formatVersion'")]
    [InlineData("escaped-duplicate-root", "duplicate property 'formatVersion'")]
    [InlineData("duplicate-region-member", "duplicate property 'key'")]
    [InlineData("missing-authoring", "missing 'authoring'")]
    [InlineData("missing-surface", "missing 'surfaceId'")]
    [InlineData("version-2", "unsupported formatVersion")]
    [InlineData("version-text", "unsupported formatVersion")]
    [InlineData("uppercase-hash", "hostSha256 must be a lowercase SHA-256")]
    [InlineData("short-hash", "hostSha256 must be a lowercase SHA-256")]
    [InlineData("fractional-index", "'vertex' must be a 32-bit integer")]
    [InlineData("negative-mesh", "invalid triangle range mesh -1")]
    [InlineData("negative-surface", "surface IDs must be non-negative")]
    [InlineData("zero-count", "invalid triangle range")]
    [InlineData("range-overflow", "invalid triangle range")]
    [InlineData("duplicate-topology-vertex", "is listed more than once")]
    [InlineData("empty-topology", "Topology must contain 1 to 65536")]
    [InlineData("empty-regions", "1 to 32 regions")]
    [InlineData("duplicate-region-key", "Region key 'chest' is not unique")]
    [InlineData("empty-region-key", "region key must be non-blank")]
    [InlineData("malformed-region-key", "UTF-16 JSON text")]
    [InlineData("empty-region-triangles", "at least one triangle range")]
    [InlineData("empty-region-strengths", "at least one surface strength")]
    [InlineData("overlap-within-region", "Region 'chest': triangle ranges")]
    [InlineData("overlap-across-regions", "Regions: triangle ranges")]
    [InlineData("unknown-role", "role must be")]
    [InlineData("case-changed-role", "role must be")]
    [InlineData("nan-strength", "invalid start of a value")]
    [InlineData("overflow-strength", "'strength' must be a finite float")]
    [InlineData("strength-above-one", "strength must be finite in [0,1]")]
    [InlineData("negative-strength", "strength must be finite in [0,1]")]
    [InlineData("duplicate-strength-surface", "has more than one strength")]
    [InlineData("unknown-strength-surface", "is not in the topology")]
    [InlineData("positive-strength-in-two-regions", "has positive strength in regions")]
    [InlineData("refinement-level-0", "Refinement level must be")]
    [InlineData("refinement-level-5", "Refinement level must be")]
    [InlineData("overlap-within-refinement", "Refinement: triangle ranges")]
    [InlineData("empty-exporter", "Authoring exporter and revision")]
    [InlineData("null-revision", "'revision' must be a string")]
    [InlineData("33-regions", "'regions' exceeds the limit of 32")]
    [InlineData("topology-over-limit", "'topology' exceeds the limit of 65536")]
    [InlineData("region-triangles-over-limit", "Regions reference more than 65536 triangles")]
    public void Malformed_or_over_limit_sidecar_refuses(string name, string reason)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Case(name)), name + ".coverage.json"));
        Assert.StartsWith(name + ".coverage.json: ", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Contains(reason, StringComparison.Ordinal), error.Message);
    }

    [Fact]
    public void Thirty_two_regions_are_accepted()
    {
        HostCoverageMap map = HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Regions(HostCoverageMap.MaxRegions)), "host");
        Assert.Equal(32, map.Regions.Count);
        Assert.Equal(map.Regions.Select(region => region.Key).Order(StringComparer.Ordinal), map.Regions.Select(region => region.Key));
    }

    [Fact]
    public void Constructor_snapshots_caller_collections_and_exposes_no_mutable_state()
    {
        var topology = new List<CoverageTopologyEntry> { new(0, 0, 1, 1), new(0, 0, 0, 0) };
        var triangles = new[] { new HostCoverageRegionTriangles(new(0, 0, 0, 1), CoverageTriangleRole.Core) };
        var strengths = new[] { new CoverageSurfaceStrength(1, 0f), new CoverageSurfaceStrength(0, 1f) };
        var regions = new List<HostCoverageRegion> { new("chest", triangles, strengths) };
        var refinement = new[] { new HostCoverageRefinement(new(0, 0, 0, 1), 1) };
        var map = new HostCoverageMap(Hash, topology, regions, refinement, new("synthetic-exporter", "r1"));
        string semantic = HostCoverageMapJson.SemanticSha256(map);

        topology[0] = new(9, 9, 9, 9);
        topology.Clear();
        triangles[0] = new(new(5, 5, 5, 5), CoverageTriangleRole.Collar);
        strengths[0] = new(7, 1f);
        regions.Clear();
        refinement[0] = new(new(4, 4, 4, 4), 4);

        Assert.Equal(new[] { new CoverageTopologyEntry(0, 0, 0, 0), new CoverageTopologyEntry(0, 0, 1, 1) }, map.Topology);
        HostCoverageRegion region = Assert.Single(map.Regions);
        Assert.Equal(new CoverageTriangleRange(0, 0, 0, 1), Assert.Single(region.Triangles).Triangles);
        Assert.Equal(new[] { new CoverageSurfaceStrength(0, 1f), new CoverageSurfaceStrength(1, 0f) }, region.Strengths);
        Assert.Equal(1, Assert.Single(map.Refinement).Level);
        Assert.Equal(semantic, HostCoverageMapJson.SemanticSha256(map));

        Assert.False(map.Topology is CoverageTopologyEntry[] || map.Topology is List<CoverageTopologyEntry>);
        Assert.False(map.Regions is HostCoverageRegion[] || map.Regions is List<HostCoverageRegion>);
        Assert.False(region.Triangles is HostCoverageRegionTriangles[] || region.Strengths is CoverageSurfaceStrength[]);
        Assert.False(map.Refinement is HostCoverageRefinement[]);
        Assert.Throws<NotSupportedException>(() => ((IList<CoverageTopologyEntry>)map.Topology)[0] = default);
    }

    [Fact]
    public void Constructor_refuses_invalid_programmatic_data()
    {
        var region = new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, 1f)]);
        HostCoverageAuthoring authoring = new("synthetic-exporter", "r1");
        Assert.Throws<ArgumentException>(() => new HostCoverageMap("ABC", [new(0, 0, 0, 0)], [region], [], authoring));
        Assert.Throws<ArgumentException>(() => new HostCoverageMap(Hash, [new(0, 0, 0, 0)], [region, region], [], authoring));
        Assert.Throws<ArgumentException>(() => new HostCoverageMap(Hash, [], [region], [], authoring));
        Assert.Throws<ArgumentException>(() => new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), (CoverageTriangleRole)9)], [new(0, 1f)]));
        Assert.Throws<ArgumentException>(() => new HostCoverageRegion("\uD800", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, 1f)]));
        Assert.Throws<ArgumentException>(() => new HostCoverageMap(Hash, [new(0, 0, 0, 0)], [region], [], new("synthetic-exporter", "r\uDC00")));
        Assert.Throws<ArgumentException>(() => new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, float.PositiveInfinity)]));
        Assert.Throws<ArgumentNullException>(() => new HostCoverageMap(Hash, null!, [region], [], authoring));
    }

    [Fact]
    public void Failure_vocabulary_is_stable_and_success_is_none()
    {
        Assert.True(CoverageFailure.Success.IsSuccess);
        Assert.Equal(CoverageFailureCode.None, default(CoverageFailure).Code);
        Assert.Equal(
            new[]
            {
                "None", "UnknownRegion", "IdentityMismatch", "UnsupportedSupport", "UnsupportedTransform", "CoreEscaped",
                "CapContact", "RecessionExceeded", "FoldedPatch", "EquipmentIntersection", "HostIntersection",
                "OverlappingConformers", "CapacityExceeded", "StaleFrame", "UnsupportedAdapter",
            },
            Enum.GetValues<CoverageFailureCode>().Select(code => $"{code}"));
        Assert.Equal(Enumerable.Range(0, 15), Enum.GetValues<CoverageFailureCode>().Select(code => (int)code));

        var failure = new CoverageFailure(CoverageFailureCode.CapacityExceeded, "host.glb", 3, 7, 1025, 1024);
        Assert.False(failure.IsSuccess);
        Assert.Equal(("host.glb", 3u, 7u, 1025, 1024), (failure.HostIdentity, failure.EquipmentInstanceId, failure.CommandId, failure.Count, failure.Limit));
        Assert.Equal(failure, new CoverageFailure(CoverageFailureCode.CapacityExceeded, "host.glb", 3, 7, 1025, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageFailure(CoverageFailureCode.None, "host.glb", 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageFailure((CoverageFailureCode)99, "host.glb", 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageFailure(CoverageFailureCode.StaleFrame, "host.glb", 0, 0, -1, 0));
    }

    static string Swap(string from, string to) => Canonical.Replace(from, to, StringComparison.Ordinal);

    static string Regions(int count, int surfaces = -1)
    {
        if (surfaces < 0) surfaces = Math.Max(count, 1);
        var builder = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append($$"""{"key":"r{{index:D2}}","triangles":[{"mesh":0,"primitive":0,"firstTriangle":{{index}},"count":1,"role":"core"}],"strengths":[{"surfaceId":{{index}},"strength":1}]}""");
        }
        string topology = string.Join(',', Enumerable.Range(0, surfaces).Select(index => $$$"""{"mesh":0,"primitive":0,"vertex":{{{index}}},"surfaceId":{{{index}}}}"""));
        return $$"""{"formatVersion":1,"hostSha256":"{{Hash}}","topology":[{{topology}}],"regions":[{{builder}}],"refinement":[],"authoring":{"exporter":"x","revision":"r"}""" + "}";
    }

    static string Case(string name) => name switch
    {
        "not-object" => "[]",
        "trailing-comma" => Swap("\"revision\":\"r1\"}", "\"revision\":\"r1\",}"),
        "unknown-root" => Swap("\"authoring\":", "\"extra\":0,\"authoring\":"),
        "unknown-topology" => Swap("\"vertex\":0,\"surfaceId\":0}", "\"vertex\":0,\"surfaceId\":0,\"extra\":0}"),
        "unknown-region" => Swap("{\"key\":\"back\",", "{\"key\":\"back\",\"extra\":0,"),
        "unknown-triangles" => Swap("\"role\":\"core\"", "\"role\":\"core\",\"extra\":0"),
        "unknown-strength" => Swap("\"strength\":0.5", "\"strength\":0.5,\"extra\":0"),
        "unknown-refinement" => Swap("\"level\":2", "\"level\":2,\"extra\":0"),
        "unknown-authoring" => Swap("\"revision\":\"r1\"", "\"revision\":\"r1\",\"extra\":0"),
        "duplicate-root" => Swap("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1"),
        "escaped-duplicate-root" => Swap("\"formatVersion\":1", "\"formatVersion\":1,\"\\u0066ormatVersion\":1"),
        "duplicate-region-member" => Swap("{\"key\":\"back\",", "{\"key\":\"back\",\"key\":\"back\","),
        "missing-authoring" => Swap(",\"authoring\":{\"exporter\":\"synthetic-exporter\",\"revision\":\"r1\"}", ""),
        "missing-surface" => Swap(",\"surfaceId\":2}]", "}]"),
        "version-2" => Swap("\"formatVersion\":1", "\"formatVersion\":2"),
        "version-text" => Swap("\"formatVersion\":1", "\"formatVersion\":\"1\""),
        "uppercase-hash" => Swap(Hash, Hash.ToUpperInvariant()),
        "short-hash" => Swap(Hash, Hash[..63]),
        "fractional-index" => Swap("\"vertex\":1,", "\"vertex\":1.0,"),
        "negative-mesh" => Swap("{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0", "{\"mesh\":-1,\"primitive\":1,\"firstTriangle\":0"),
        "negative-surface" => Swap("\"vertex\":0,\"surfaceId\":0}", "\"vertex\":0,\"surfaceId\":-1}"),
        "zero-count" => Swap("\"firstTriangle\":2,\"count\":1", "\"firstTriangle\":2,\"count\":0"),
        "range-overflow" => Swap("\"firstTriangle\":2,\"count\":1", "\"firstTriangle\":2147483647,\"count\":2"),
        "duplicate-topology-vertex" => Swap("\"primitive\":1,\"vertex\":1,", "\"primitive\":1,\"vertex\":0,"),
        "empty-topology" => Regions(1, 0),
        "empty-regions" => Regions(0),
        "duplicate-region-key" => Swap("\"key\":\"back\"", "\"key\":\"chest\""),
        "empty-region-key" => Swap("\"key\":\"back\"", "\"key\":\" \""),
        "malformed-region-key" => Swap("\"key\":\"back\"", "\"key\":\"\\uD800\""),
        "empty-region-triangles" => Swap("\"triangles\":[{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":1,\"role\":\"collar\"}]", "\"triangles\":[]"),
        "empty-region-strengths" => Swap("\"strengths\":[{\"surfaceId\":1,\"strength\":0},{\"surfaceId\":2,\"strength\":0.5}]", "\"strengths\":[]"),
        "overlap-within-region" => Swap("\"firstTriangle\":2,\"count\":1", "\"firstTriangle\":1,\"count\":2"),
        "overlap-across-regions" => Swap("\"primitive\":1,\"firstTriangle\":0,\"count\":1,\"role\":\"collar\"", "\"primitive\":0,\"firstTriangle\":2,\"count\":1,\"role\":\"collar\""),
        "unknown-role" => Swap("\"role\":\"core\"", "\"role\":\"rim\""),
        "case-changed-role" => Swap("\"role\":\"core\"", "\"role\":\"Core\""),
        "nan-strength" => Swap("\"strength\":0.5", "\"strength\":NaN"),
        "overflow-strength" => Swap("\"strength\":0.5", "\"strength\":1e100"),
        "strength-above-one" => Swap("\"strength\":0.5", "\"strength\":1.0001"),
        "negative-strength" => Swap("\"strength\":0.5", "\"strength\":-0.25"),
        "duplicate-strength-surface" => Swap("{\"surfaceId\":1,\"strength\":0},{\"surfaceId\":2", "{\"surfaceId\":2,\"strength\":0},{\"surfaceId\":2"),
        "unknown-strength-surface" => Swap("{\"surfaceId\":2,\"strength\":0.5}", "{\"surfaceId\":3,\"strength\":0.5}"),
        "positive-strength-in-two-regions" => Swap("{\"surfaceId\":1,\"strength\":0},{\"surfaceId\":2", "{\"surfaceId\":0,\"strength\":0.25},{\"surfaceId\":2"),
        "refinement-level-0" => Swap("\"level\":2", "\"level\":0"),
        "refinement-level-5" => Swap("\"level\":2", "\"level\":5"),
        "overlap-within-refinement" => Swap("\"count\":3,\"level\":2}", "\"count\":3,\"level\":2},{\"mesh\":0,\"primitive\":0,\"firstTriangle\":2,\"count\":1,\"level\":1}"),
        "empty-exporter" => Swap("\"synthetic-exporter\"", "\"\""),
        "null-revision" => Swap("\"revision\":\"r1\"", "\"revision\":null"),
        "33-regions" => Regions(HostCoverageMap.MaxRegions + 1),
        "topology-over-limit" => Swap("\"topology\":[", "\"topology\":[" + string.Concat(Enumerable.Range(0, HostCoverageMap.MaxSurfaceVertices - 3)
            .Select(index => $$$"""{"mesh":1,"primitive":0,"vertex":{{{index}}},"surfaceId":{{{index + 3}}}},"""))),
        "region-triangles-over-limit" => Swap("\"firstTriangle\":2,\"count\":1", $"\"firstTriangle\":2,\"count\":{HostCoverageMap.MaxTriangles - 2}"),
        _ => throw new ArgumentException(name),
    };
}
