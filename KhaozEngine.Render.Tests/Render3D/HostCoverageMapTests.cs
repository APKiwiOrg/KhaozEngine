using System;
using System.Collections.Generic;
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
    public void Format_one_roundtrips_canonically()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical);
        HostCoverageMap map = HostCoverageMapJson.Read(canonical, "host");
        Assert.Equal(canonical, HostCoverageMapJson.Write(map));
        Assert.Equal("bbdfafa0b5d5e25dfc4210093bf8d0857a275362f72834ca8d080198a92de607", HostCoverageMapJson.SemanticSha256(map));
        byte[] escaped = Encoding.UTF8.GetBytes(Canonical.Replace("\"regions\"", "\"\\u0072egions\"", StringComparison.Ordinal));
        Assert.Equal(canonical, HostCoverageMapJson.Write(HostCoverageMapJson.Read(escaped, "host-escaped")));
        Assert.Equal(canonical, HostCoverageMapJson.Write(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Canonical + " \t\r\n"), "host-whitespace")));

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
    [InlineData("not-object", HostCoverageRefusal.ExpectedObject, "$")]
    [InlineData("trailing-comma", HostCoverageRefusal.InvalidJson, "$")]
    [InlineData("malformed-member-name", HostCoverageRefusal.InvalidText, "$")]
    [InlineData("unknown-root", HostCoverageRefusal.UnknownMember, "$.extra")]
    [InlineData("unknown-topology", HostCoverageRefusal.UnknownMember, "$.topology[0].extra")]
    [InlineData("unknown-region", HostCoverageRefusal.UnknownMember, "$.regions[0].extra")]
    [InlineData("unknown-triangles", HostCoverageRefusal.UnknownMember, "$.regions[1].triangles[0].extra")]
    [InlineData("unknown-strength", HostCoverageRefusal.UnknownMember, "$.regions[0].strengths[1].extra")]
    [InlineData("unknown-refinement", HostCoverageRefusal.UnknownMember, "$.refinement[0].extra")]
    [InlineData("unknown-authoring", HostCoverageRefusal.UnknownMember, "$.authoring.extra")]
    [InlineData("duplicate-root", HostCoverageRefusal.DuplicateMember, "$.formatVersion")]
    [InlineData("escaped-duplicate-root", HostCoverageRefusal.DuplicateMember, "$.formatVersion")]
    [InlineData("duplicate-region-member", HostCoverageRefusal.DuplicateMember, "$.regions[0].key")]
    [InlineData("missing-authoring", HostCoverageRefusal.MissingMember, "$.authoring")]
    [InlineData("missing-surface", HostCoverageRefusal.MissingMember, "$.topology[3].surfaceId")]
    [InlineData("version-2", HostCoverageRefusal.UnsupportedVersion, "$.formatVersion")]
    [InlineData("version-text", HostCoverageRefusal.UnsupportedVersion, "$.formatVersion")]
    [InlineData("uppercase-hash", HostCoverageRefusal.MalformedDigest, "$.hostSha256")]
    [InlineData("short-hash", HostCoverageRefusal.MalformedDigest, "$.hostSha256")]
    [InlineData("fractional-index", HostCoverageRefusal.InvalidInteger, "$.topology[1].vertex")]
    [InlineData("negative-mesh", HostCoverageRefusal.InvalidRange, "$.regions[0].triangles[0]")]
    [InlineData("negative-strength-surface", HostCoverageRefusal.InvalidSurfaceId, "$.regions[0].strengths[0].surfaceId")]
    [InlineData("negative-surface", HostCoverageRefusal.InvalidTopology, "$.topology[0]")]
    [InlineData("zero-count", HostCoverageRefusal.InvalidRange, "$.regions[1].triangles[1]")]
    [InlineData("range-overflow", HostCoverageRefusal.InvalidRange, "$.regions[1].triangles[1]")]
    [InlineData("duplicate-topology-vertex", HostCoverageRefusal.DuplicateTopologyVertex, "$.topology[3]")]
    [InlineData("empty-topology", HostCoverageRefusal.TopologyCapacity, "$.topology")]
    [InlineData("empty-regions", HostCoverageRefusal.RegionCapacity, "$.regions")]
    [InlineData("duplicate-region-key", HostCoverageRefusal.DuplicateRegion, "$.regions[1].key")]
    [InlineData("empty-region-key", HostCoverageRefusal.InvalidLabel, "$.regions[0].key")]
    [InlineData("malformed-region-key", HostCoverageRefusal.InvalidText, "$.regions[0].key")]
    [InlineData("empty-region-triangles", HostCoverageRefusal.RegionTriangleCapacity, "$.regions[0].triangles")]
    [InlineData("empty-region-strengths", HostCoverageRefusal.StrengthCapacity, "$.regions[0].strengths")]
    [InlineData("overlap-within-region", HostCoverageRefusal.RangeOverlap, "$.regions[1].triangles")]
    [InlineData("overlap-across-regions", HostCoverageRefusal.RangeOverlap, "$.regions")]
    [InlineData("unknown-role", HostCoverageRefusal.InvalidRole, "$.regions[1].triangles[0].role")]
    [InlineData("case-changed-role", HostCoverageRefusal.InvalidRole, "$.regions[1].triangles[0].role")]
    [InlineData("nan-strength", HostCoverageRefusal.InvalidJson, "$")]
    [InlineData("overflow-strength", HostCoverageRefusal.NonFiniteNumber, "$.regions[0].strengths[1].strength")]
    [InlineData("strength-above-one", HostCoverageRefusal.InvalidStrength, "$.regions[0].strengths[1].strength")]
    [InlineData("negative-strength", HostCoverageRefusal.InvalidStrength, "$.regions[0].strengths[1].strength")]
    [InlineData("duplicate-strength-surface", HostCoverageRefusal.DuplicateStrength, "$.regions[0].strengths[1].surfaceId")]
    [InlineData("unknown-strength-surface", HostCoverageRefusal.UnknownSurface, "$.regions[0].strengths[1].surfaceId")]
    [InlineData("positive-strength-in-two-regions", HostCoverageRefusal.PositiveStrengthOverlap, "$.regions[1].strengths[0].surfaceId")]
    [InlineData("refinement-level-0", HostCoverageRefusal.RefinementLevel, "$.refinement[0].level")]
    [InlineData("refinement-level-5", HostCoverageRefusal.RefinementLevel, "$.refinement[0].level")]
    [InlineData("overlap-within-refinement", HostCoverageRefusal.RangeOverlap, "$.refinement")]
    [InlineData("empty-exporter", HostCoverageRefusal.InvalidLabel, "$.authoring.exporter")]
    [InlineData("null-revision", HostCoverageRefusal.ExpectedString, "$.authoring.revision")]
    [InlineData("33-regions", HostCoverageRefusal.RegionCapacity, "$.regions")]
    [InlineData("topology-over-limit", HostCoverageRefusal.TopologyCapacity, "$.topology")]
    [InlineData("region-triangles-over-limit", HostCoverageRefusal.TotalTriangleCapacity, "$.regions")]
    public void Malformed_sidecar_preserves_reason_and_location(string name, HostCoverageRefusal expectedReason, string expectedRootedPath)
    {
        string identity = name + ".coverage.json";
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Case(name)), identity));
        Assert.Equal(expectedReason, refusal.Reason);
        Assert.Equal(identity, refusal.Identity);
        Assert.Equal(expectedRootedPath, refusal.Path);
        if (expectedReason != HostCoverageRefusal.InvalidJson)
            Assert.Equal(expectedReason, Assert.IsType<HostCoverageDataException>(refusal.InnerException).Reason);
        if (expectedReason is HostCoverageRefusal.DuplicateMember or HostCoverageRefusal.UnknownMember or HostCoverageRefusal.MissingMember)
            Assert.Equal(expectedRootedPath[(expectedRootedPath.LastIndexOf('.') + 1)..], refusal.MemberName);
        Assert.NotNull(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Control(name)), identity));
        if (name == "duplicate-region-key") Assert.Equal("chest", refusal.RegionKey);
        if (name is "unknown-strength-surface" or "duplicate-strength-surface")
            Assert.Equal(("back", name == "unknown-strength-surface" ? 3 : 2), (refusal.RegionKey, refusal.SurfaceId));
        if (name == "duplicate-topology-vertex") Assert.Equal((0, 1, 0), (refusal.Mesh, refusal.Primitive, refusal.Vertex));
    }

    [Fact]
    public void Thirty_two_regions_are_accepted()
    {
        HostCoverageMap map = HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Regions(HostCoverageMap.MaxRegions)), "host");
        Assert.Equal(32, map.Regions.Count);
        Assert.Equal(map.Regions.Select(region => region.Key).Order(StringComparer.Ordinal), map.Regions.Select(region => region.Key));
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("\\u0072egions")]
    public void Region_capacity_refuses_before_reading_invalid_tail(string property)
    {
        string prefix = "{\"" + property + "\":[" + string.Join(',', Enumerable.Repeat("{}", 33));
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(prefix + ",!"), "host"));
        Assert.Equal(HostCoverageRefusal.RegionCapacity, refusal.Reason);
        Assert.Equal("$.regions", refusal.Path);
        Assert.Equal((33L, 32L), (refusal.Count, refusal.Limit));
        Assert.NotNull(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Regions(32)), "host"));
    }

    [Theory]
    [InlineData("{\"extra\":", HostCoverageRefusal.UnknownMember, "$.extra")]
    [InlineData("{\"regions\":[],\"\\u0072egions\":", HostCoverageRefusal.DuplicateMember, "$.regions")]
    [InlineData("{\"regions\":[{\"extra\":", HostCoverageRefusal.UnknownMember, "$.regions[0].extra")]
    [InlineData("{\"topology\":{", HostCoverageRefusal.ExpectedArray, "$.topology")]
    [InlineData("{\"regions\":{", HostCoverageRefusal.ExpectedArray, "$.regions")]
    [InlineData("{\"refinement\":{", HostCoverageRefusal.ExpectedArray, "$.refinement")]
    [InlineData("{\"authoring\":[", HostCoverageRefusal.ExpectedObject, "$.authoring")]
    [InlineData("{\"regions\":[{\"triangles\":{", HostCoverageRefusal.ExpectedArray, "$.regions[0].triangles")]
    [InlineData("{\"regions\":[{\"strengths\":{", HostCoverageRefusal.ExpectedArray, "$.regions[0].strengths")]
    [InlineData("{\"topology\":[{\"mesh\":[", HostCoverageRefusal.InvalidInteger, "$.topology[0].mesh")]
    [InlineData("{\"hostSha256\":[", HostCoverageRefusal.ExpectedString, "$.hostSha256")]
    public void Schema_refuses_before_reading_invalid_tail(string prefix, HostCoverageRefusal reason, string path)
    {
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(prefix + "!"), "sidecar"));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(path, refusal.Path);
        Assert.Equal("sidecar", refusal.Identity);
        Assert.NotNull(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Canonical), "sidecar"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("!")]
    public void Trailing_value_or_garbage_refuses(string tail)
    {
        HostCoverageDataException error = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Canonical + tail), "host"));
        Assert.Equal(HostCoverageRefusal.InvalidJson, error.Reason);
        Assert.Equal("$", error.Path);
    }

    [Theory]
    [InlineData("topology")]
    [InlineData("regions")]
    [InlineData("triangles")]
    [InlineData("strengths")]
    [InlineData("refinement")]
    public void Constructor_rejects_known_over_limit_counts_before_copy(string collection)
    {
        HostCoverageRegionTriangles[] triangles = [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)];
        CoverageSurfaceStrength[] strengths = [new(0, 1f)];
        var region = new HostCoverageRegion("chest", triangles, strengths);
        CoverageTopologyEntry[] topology = [new(0, 0, 0, 0)];
        HostCoverageAuthoring authoring = new("synthetic-exporter", "r1");

        Assert.Throws<HostCoverageDataException>(() =>
        {
            switch (collection)
            {
                case "topology":
                    _ = new HostCoverageMap(Hash, new RefusingCollection<CoverageTopologyEntry>(65537), [region], [], authoring);
                    break;
                case "regions":
                    _ = new HostCoverageMap(Hash, topology, new RefusingCollection<HostCoverageRegion>(33), [], authoring);
                    break;
                case "triangles":
                    _ = new HostCoverageRegion("chest", new RefusingCollection<HostCoverageRegionTriangles>(65537), strengths);
                    break;
                case "strengths":
                    _ = new HostCoverageRegion("chest", triangles, new RefusingCollection<CoverageSurfaceStrength>(65537));
                    break;
                case "refinement":
                    _ = new HostCoverageMap(Hash, topology, [region], new RefusingCollection<HostCoverageRefinement>(65537), authoring);
                    break;
                default:
                    throw new InvalidOperationException(collection);
            }
        });
    }

    [Fact]
    public void Constructor_stops_unknown_region_input_at_refusal_witness()
    {
        var region = new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, 1f)]);
        int consumed = 0;
        IEnumerable<HostCoverageRegion> RegionsWithWitness()
        {
            for (int index = 0; index < 33; index++)
            {
                consumed++;
                yield return region;
            }
            throw new InvalidOperationException("Read beyond the capacity refusal witness.");
        }

        Assert.Throws<HostCoverageDataException>(() => new HostCoverageMap(Hash, [new(0, 0, 0, 0)], RegionsWithWitness(), [], new("synthetic-exporter", "r1")));
        Assert.Equal(33, consumed);
    }

    sealed class RefusingCollection<T>(int count) : ICollection<T>
    {
        public int Count => count;
        public bool IsReadOnly => true;
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumerated a known over-limit collection.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void CopyTo(T[] array, int arrayIndex) => throw new InvalidOperationException("Copied a known over-limit collection.");
        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(T item) => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
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
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageMap("ABC", [new(0, 0, 0, 0)], [region], [], authoring));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageMap(Hash, [new(0, 0, 0, 0)], [region, region], [], authoring));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageMap(Hash, [], [region], [], authoring));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), (CoverageTriangleRole)9)], [new(0, 1f)]));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageRegion("\uD800", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, 1f)]));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageMap(Hash, [new(0, 0, 0, 0)], [region], [], new("synthetic-exporter", "r\uDC00")));
        Assert.Throws<HostCoverageDataException>(() => new HostCoverageRegion("chest", [new(new(0, 0, 0, 1), CoverageTriangleRole.Core)], [new(0, float.PositiveInfinity)]));
        Assert.Throws<ArgumentNullException>(() => new HostCoverageMap(Hash, null!, [region], [], authoring));
    }

    [Theory]
    [InlineData("topology", HostCoverageRefusal.TopologyCapacity, 65536)]
    [InlineData("regions", HostCoverageRefusal.RegionCapacity, 32)]
    [InlineData("triangle-ranges", HostCoverageRefusal.RegionTriangleCapacity, 65536)]
    [InlineData("region-triangles", HostCoverageRefusal.RegionTriangleTotalCapacity, 65536)]
    [InlineData("strengths", HostCoverageRefusal.StrengthCapacity, 65536)]
    [InlineData("total-triangles", HostCoverageRefusal.TotalTriangleCapacity, 65536)]
    [InlineData("refinement-ranges", HostCoverageRefusal.RefinementRangeCapacity, 65536)]
    [InlineData("refinement-triangles", HostCoverageRefusal.RefinementTriangleCapacity, 65536)]
    public void Every_capacity_reports_its_witness_and_accepts_its_inclusive_limit(string kind, HostCoverageRefusal reason, int limit)
    {
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(CapacityFixture(kind, limit + 1)), "bounded"));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(((long)limit + 1, (long)limit), (refusal.Count, refusal.Limit));
        Assert.Equal("bounded", refusal.Identity);
        Assert.NotNull(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(CapacityFixture(kind, limit)), "bounded"));
    }

    [Fact]
    public void Sorted_strengths_keep_the_original_json_refusal_location()
    {
        string valid = Reordered;
        string invalid = valid.Replace("{\"surfaceId\":2,\"strength\":5e-1}", "{\"surfaceId\":3,\"strength\":5e-1}", StringComparison.Ordinal);
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(invalid), "reordered"));
        Assert.Equal(HostCoverageRefusal.UnknownSurface, refusal.Reason);
        Assert.Equal("$.regions[1].strengths[0].surfaceId", refusal.Path);
        Assert.Equal(("back", 3), (refusal.RegionKey, refusal.SurfaceId));
        Assert.NotNull(HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(valid), "reordered"));
    }

    [Fact]
    public void Context_wrapper_preserves_every_identifier_and_typed_inner_reason()
    {
        var original = new HostCoverageDataException(HostCoverageRefusal.InvalidRange, "data")
        {
            Identity = "source",
            Path = "$.triangles[2]",
            Count = 65537,
            Limit = 65536,
            MemberName = "count",
            FormatVersion = 3,
            RegionKey = "chest",
            SurfaceId = 9,
            Mesh = 4,
            Primitive = 5,
            Vertex = 6,
            FirstTriangle = 7,
        };
        var refusal = original.WithContext(identity: "outer", prefix: "$.regions[1]");
        Assert.Same(original, refusal.InnerException);
        Assert.Equal(original.Reason, refusal.Reason);
        Assert.Equal("outer", refusal.Identity);
        Assert.Equal("$.regions[1].triangles[2]", refusal.Path);
        Assert.Equal(new[] { "Count", "FirstTriangle", "FormatVersion", "Identity", "Limit", "MemberName", "Mesh", "Path", "Primitive", "Reason", "RegionKey", "SurfaceId", "Vertex" },
            typeof(HostCoverageDataException).GetProperties().Where(property => property.DeclaringType == typeof(HostCoverageDataException))
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal((original.Count, original.Limit, original.MemberName, original.FormatVersion), (refusal.Count, refusal.Limit, refusal.MemberName, refusal.FormatVersion));
        Assert.Equal((original.RegionKey, original.SurfaceId, original.Mesh, original.Primitive, original.Vertex, original.FirstTriangle),
            (refusal.RegionKey, refusal.SurfaceId, refusal.Mesh, refusal.Primitive, refusal.Vertex, refusal.FirstTriangle));
    }

    static string Control(string name) => name switch
    {
        "empty-topology" => Regions(1, 1),
        "empty-regions" => Regions(1),
        "33-regions" => Regions(32, 33),
        "topology-over-limit" => Case(name).Replace("{\"mesh\":1,\"primitive\":0,\"vertex\":0,\"surfaceId\":3},", "", StringComparison.Ordinal),
        "region-triangles-over-limit" => Case(name).Replace("\"count\":65534", "\"count\":65533", StringComparison.Ordinal),
        _ => Canonical,
    };

    static string CapacityFixture(string kind, int count)
    {
        if (kind == "regions") return Regions(count);
        string Ranges(int length, bool refinement) => string.Join(',', Enumerable.Range(0, length).Select(index =>
            $$$"""{"mesh":0,"primitive":0,"firstTriangle":{{{index}}},"count":1,{{{(refinement ? "\"level\":1" : "\"role\":\"core\"")}}}}"""));
        int vertices = kind is "topology" or "strengths" ? Math.Min(count, 65536) : 1;
        if (kind == "topology") vertices = count;
        string topology = string.Join(',', Enumerable.Range(0, vertices).Select(index =>
            $$$"""{"mesh":0,"primitive":0,"vertex":{{{index}}},"surfaceId":{{{index}}}}"""));
        string triangles = kind switch
        {
            "triangle-ranges" => Ranges(count, false),
            "region-triangles" => $$$"""{"mesh":0,"primitive":0,"firstTriangle":0,"count":{{{count}}},"role":"core"}""",
            "total-triangles" => $$$"""{"mesh":0,"primitive":0,"firstTriangle":0,"count":{{{count - 1}}},"role":"core"}""",
            _ => "{\"mesh\":0,\"primitive\":0,\"firstTriangle\":0,\"count\":1,\"role\":\"core\"}",
        };
        string strengths = kind == "strengths" ? string.Join(',', Enumerable.Range(0, count).Select(index =>
            $$$"""{"surfaceId":{{{index}}},"strength":1}""")) : "{\"surfaceId\":0,\"strength\":1}";
        string regions = $$$"""{"key":"region","triangles":[{{{triangles}}}],"strengths":[{{{strengths}}}]}""";
        if (kind == "total-triangles")
            regions += ",{\"key\":\"other\",\"triangles\":[{\"mesh\":0,\"primitive\":1,\"firstTriangle\":0,\"count\":1,\"role\":\"collar\"}],\"strengths\":[{\"surfaceId\":0,\"strength\":0}]}";
        string refinement = kind switch
        {
            "refinement-ranges" => Ranges(count, true),
            "refinement-triangles" => $$$"""{"mesh":0,"primitive":0,"firstTriangle":0,"count":{{{count}}},"level":1}""",
            _ => "",
        };
        return $$$"""{"formatVersion":1,"hostSha256":"{{{Hash}}}","topology":[{{{topology}}}],"regions":[{{{regions}}}],"refinement":[{{{refinement}}}],"authoring":{"exporter":"synthetic-exporter","revision":"r1"}}""";
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
        "malformed-member-name" => "{\"\\uD800\":0," + Canonical[1..],
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
        "negative-strength-surface" => Swap("{\"surfaceId\":1,\"strength\":0}", "{\"surfaceId\":-1,\"strength\":0}"),
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
