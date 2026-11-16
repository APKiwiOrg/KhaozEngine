using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Child role overrides on host coverage regions, programmatic and in JSON format 2: each rule refuses with its
/// own reason and promised identifiers beside a passing control, and each capacity accepts its inclusive limit. All data
/// is synthetic.</summary>
public class HostCoverageChildRoleTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    static readonly CoverageChildRole BaseOverride = new(0, 0, 1, 2, 0, CoverageTriangleRole.Core);

    const string ChestStrengths = "{\"surfaceId\":0,\"strength\":1},{\"surfaceId\":1,\"strength\":0}]";
    const string ChildRolesMember = "\"childRoles\":[{\"mesh\":0,\"primitive\":0,\"triangle\":2,\"level\":2,\"cells\":[1,2],\"role\":\"core\"}]";

    // Chest owns triangle 2 as collar at refinement level 2, and two of its four cells take the core role.
    static readonly string Format2Canonical = HostCoverageMapTests.Canonical
        .Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal)
        .Replace(ChestStrengths, ChestStrengths + "," + ChildRolesMember, StringComparison.Ordinal);

    // The same map with childRoles first, other member orders, triangle 2 split over two entries and extra whitespace.
    static readonly string Format2Reordered = " " + HostCoverageMapTests.Canonical
        .Replace("\"formatVersion\":1", "\"formatVersion\" : 2", StringComparison.Ordinal)
        .Replace("{\"key\":\"chest\",", "{\"key\":\"chest\", \"childRoles\" : [ {\"role\":\"core\",\"cells\":[ 2 ],\"level\":2," +
            "\"triangle\":2,\"primitive\":0,\"mesh\":0}, {\"mesh\":0,\"triangle\":2,\"primitive\":0,\"cells\":[1],\"role\":\"core\",\"level\":2} ],",
            StringComparison.Ordinal) + "\n";

    [Fact]
    public void Format_two_roundtrips_canonically_in_its_own_digest_domain()
    {
        byte[] canonical = Encoding.UTF8.GetBytes(Format2Canonical);
        HostCoverageMap map = HostCoverageMapJson.Read(canonical, "host");
        Assert.Equal(canonical, HostCoverageMapJson.Write(map));
        Assert.Equal(Format2Canonical, Encoding.UTF8.GetString(HostCoverageMapJson.Write(
            HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Format2Reordered), "host-reordered"))));

        string semantic = HostCoverageMapJson.SemanticSha256(map);
        Assert.Equal("1ef068fdea3ce00b7a8d7283af0d050776d9527b56a9904fb515d9e512b4e89d", semantic);
        Assert.Equal(Digest("KhaozEngine.HostCoverageMap/2\n", canonical), semantic);
        Assert.NotEqual(Digest("KhaozEngine.HostCoverageMap/1\n", canonical), semantic);

        // Without its child roles the map writes format 1 again and keeps the frozen format 1 identity.
        var plain = new HostCoverageMap(map.HostSha256, map.Topology,
            map.Regions.Select(region => new HostCoverageRegion(region.Key, region.Triangles, region.Strengths)), map.Refinement, map.Authoring);
        Assert.Equal(HostCoverageMapTests.Canonical, Encoding.UTF8.GetString(HostCoverageMapJson.Write(plain)));
        Assert.Equal("bbdfafa0b5d5e25dfc4210093bf8d0857a275362f72834ca8d080198a92de607", HostCoverageMapJson.SemanticSha256(plain));

        CoverageChildRole[] cells = [new(0, 0, 2, 2, 2, CoverageTriangleRole.Core), new(0, 0, 2, 2, 1, CoverageTriangleRole.Core)];
        var programmatic = new HostCoverageMap(plain.HostSha256, plain.Topology, plain.Regions.Select(region => region.Key == "chest"
            ? new HostCoverageRegion(region.Key, region.Triangles, region.Strengths, cells) : region), plain.Refinement, plain.Authoring);
        Assert.Equal(Format2Canonical, Encoding.UTF8.GetString(HostCoverageMapJson.Write(programmatic)));
    }

    [Theory]
    [InlineData("format-one", HostCoverageRefusal.UnknownMember, "$.regions[1].childRoles", null, null, null, null, null)]
    [InlineData("no-child-roles", HostCoverageRefusal.UnnecessaryFormatVersion, "$.formatVersion", null, null, null, null, null)]
    [InlineData("empty-child-roles", HostCoverageRefusal.ChildRoleCapacity, "$.regions[1].childRoles", "chest", null, null, null, null)]
    [InlineData("empty-cells", HostCoverageRefusal.ChildRoleCapacity, "$.regions[1].childRoles[0].cells", "chest", null, null, null, null)]
    [InlineData("outside-region", HostCoverageRefusal.ChildRoleOutsideRegion, "$.regions[1].childRoles[0]", "chest", 1, 0, null, 1)]
    [InlineData("level-3", HostCoverageRefusal.ChildRoleLevelMismatch, "$.regions[1].childRoles[0].level", "chest", 0, 2, 3, null)]
    [InlineData("level-5", HostCoverageRefusal.RefinementLevel, "$.regions[1].childRoles[0].level", null, 0, 2, null, null)]
    [InlineData("cell-4", HostCoverageRefusal.ChildRoleCell, "$.regions[1].childRoles[0].cells[1]", "chest", 0, 2, 2, 4)]
    [InlineData("duplicate-cell", HostCoverageRefusal.DuplicateChildRole, "$.regions[1].childRoles[0].cells[1]", "chest", 0, 2, null, 1)]
    [InlineData("role-collar", HostCoverageRefusal.ChildRoleSameAsParent, "$.regions[1].childRoles[0].role", "chest", 0, 2, null, 1)]
    [InlineData("role-capitalised", HostCoverageRefusal.InvalidRole, "$.regions[1].childRoles[0].role", "chest", null, null, null, null)]
    [InlineData("covers", HostCoverageRefusal.ChildRoleCoversTriangle, "$.regions[1].childRoles[0]", "chest", 0, 2, 2, null)]
    [InlineData("fractional-cell", HostCoverageRefusal.InvalidInteger, "$.regions[1].childRoles[0].cells[0]", null, null, null, null, null)]
    [InlineData("unknown-entry-member", HostCoverageRefusal.UnknownMember, "$.regions[1].childRoles[0].extra", null, null, null, null, null)]
    public void Format_two_refusals_name_their_source_position_beside_a_passing_control(string row, HostCoverageRefusal reason,
        string path, string? regionKey, int? primitive, int? triangle, int? level, int? cell)
    {
        Assert.Equal(2, HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(Format2Canonical), "control").Regions[1].ChildRoles.Count);

        string Swap(string from, string to) => Format2Canonical.Replace(from, to, StringComparison.Ordinal);
        string json = row switch
        {
            "format-one" => Swap("\"formatVersion\":2", "\"formatVersion\":1"),
            "no-child-roles" => HostCoverageMapTests.Canonical.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal),
            "empty-child-roles" => Swap(ChildRolesMember, "\"childRoles\":[]"),
            "empty-cells" => Swap("\"cells\":[1,2]", "\"cells\":[]"),
            "outside-region" => Swap("\"primitive\":0,\"triangle\":2", "\"primitive\":1,\"triangle\":0"),
            "level-3" => Swap("\"level\":2,\"cells\"", "\"level\":3,\"cells\""),
            "level-5" => Swap("\"level\":2,\"cells\"", "\"level\":5,\"cells\""),
            "cell-4" => Swap("\"cells\":[1,2]", "\"cells\":[1,4]"),
            "duplicate-cell" => Swap("\"cells\":[1,2]", "\"cells\":[1,1]"),
            "role-collar" => Swap("\"cells\":[1,2],\"role\":\"core\"", "\"cells\":[1,2],\"role\":\"collar\""),
            "role-capitalised" => Swap("\"cells\":[1,2],\"role\":\"core\"", "\"cells\":[1,2],\"role\":\"Core\""),
            "covers" => Swap("\"cells\":[1,2]", "\"cells\":[0,1,2,3]"),
            "fractional-cell" => Swap("\"cells\":[1,2]", "\"cells\":[1.5,2]"),
            "unknown-entry-member" => Swap("\"cells\":[1,2],\"role\":\"core\"", "\"cells\":[1,2],\"role\":\"core\",\"extra\":0"),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        Assert.NotEqual(Format2Canonical, json);
        string identity = row + ".coverage.json";
        var refusal = Assert.Throws<HostCoverageDataException>(() => HostCoverageMapJson.Read(Encoding.UTF8.GetBytes(json), identity));
        Assert.Equal((reason, path, identity, regionKey), (refusal.Reason, refusal.Path, refusal.Identity, refusal.RegionKey));
        int? mesh = triangle is null ? null : 0;
        Assert.Equal((mesh, primitive, triangle), (refusal.Mesh, refusal.Primitive, refusal.FirstTriangle));
        Assert.Equal((level, cell), (refusal.Level, refusal.Cell));
        (long?, long?) bound = row switch { "empty-child-roles" => (0, HostCoverageMap.MaxTriangles), "empty-cells" => (0, 15), _ => (null, null) };
        Assert.Equal(bound, (refusal.Count, refusal.Limit));
        Assert.Equal(row == "no-child-roles" ? 2 : (int?)null, refusal.FormatVersion);
        if (reason == HostCoverageRefusal.UnknownMember) Assert.Equal(path[(path.LastIndexOf('.') + 1)..], refusal.MemberName);
    }

    [Theory]
    [InlineData("triangle-7", HostCoverageRefusal.ChildRoleOutsideRegion, "$.childRoles[0]", "r0", 7, null, 0)]
    [InlineData("level-0", HostCoverageRefusal.RefinementLevel, "$.childRoles[0].level", null, 1, null, null)]
    [InlineData("level-5", HostCoverageRefusal.RefinementLevel, "$.childRoles[0].level", null, 1, null, null)]
    [InlineData("level-3", HostCoverageRefusal.ChildRoleLevelMismatch, "$.regions[0].childRoles[0].level", "r0", 1, 3, null)]
    [InlineData("unrefined", HostCoverageRefusal.ChildRoleLevelMismatch, "$.regions[0].childRoles[0].level", "r0", 4, 2, null)]
    [InlineData("cell-4", HostCoverageRefusal.ChildRoleCell, "$.childRoles[0].cell", "r0", 1, 2, 4)]
    [InlineData("cell-minus-1", HostCoverageRefusal.ChildRoleCell, "$.childRoles[0].cell", "r0", 1, 2, -1)]
    [InlineData("duplicate", HostCoverageRefusal.DuplicateChildRole, "$.childRoles[1].cell", "r0", 1, null, 0)]
    [InlineData("role-collar", HostCoverageRefusal.ChildRoleSameAsParent, "$.childRoles[0].role", "r0", 1, null, 0)]
    [InlineData("role-zero", HostCoverageRefusal.InvalidRole, "$.childRoles[0].role", "r0", null, null, null)]
    [InlineData("covers", HostCoverageRefusal.ChildRoleCoversTriangle, "$.childRoles[3]", "r0", 1, 2, null)]
    public void Child_role_refusals_name_their_rule_beside_a_passing_control(string row, HostCoverageRefusal reason, string path,
        string? regionKey, int? triangle, int? level, int? cell)
    {
        HostCoverageMap control = BaseMap([BaseOverride]);
        Assert.Equal(BaseOverride, Assert.Single(Assert.Single(control.Regions).ChildRoles));

        CoverageChildRole[] broken = row switch
        {
            "triangle-7" => [BaseOverride with { Triangle = 7 }],
            "level-0" => [BaseOverride with { Level = 0 }],
            "level-5" => [BaseOverride with { Level = 5 }],
            "level-3" => [BaseOverride with { Level = 3 }],
            "unrefined" => [BaseOverride with { Triangle = 4 }],
            "cell-4" => [BaseOverride with { Cell = 4 }],
            "cell-minus-1" => [BaseOverride with { Cell = -1 }],
            "duplicate" => [BaseOverride, BaseOverride],
            "role-collar" => [BaseOverride with { Role = CoverageTriangleRole.Collar }],
            "role-zero" => [BaseOverride with { Role = (CoverageTriangleRole)0 }],
            "covers" => [.. Enumerable.Range(0, 4).Select(index => BaseOverride with { Cell = index })],
            _ => throw new System.ArgumentOutOfRangeException(nameof(row)),
        };
        var refusal = Assert.Throws<HostCoverageDataException>(() => BaseMap(broken));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(path, refusal.Path);
        Assert.Equal(regionKey, refusal.RegionKey);
        int? ordinal = triangle is null ? null : 0;
        Assert.Equal((ordinal, ordinal, triangle), (refusal.Mesh, refusal.Primitive, refusal.FirstTriangle));
        Assert.Equal((level, cell), (refusal.Level, refusal.Cell));
    }

    [Fact]
    public void Child_role_capacities_refuse_their_witness_and_accept_their_inclusive_limit()
    {
        CoverageChildRole[] fifteen = [.. Cells(0, 15)];
        Assert.Equal(15, Assert.Single(LevelFourMap(Region("r0", 1, fifteen)).Regions).ChildRoles.Count);
        var perTriangle = Assert.Throws<HostCoverageDataException>(() => Region("r0", 1, [.. Cells(0, 16)]));
        Assert.Equal((HostCoverageRefusal.ChildRoleCapacity, "$.childRoles[15].cell", "r0"), (perTriangle.Reason, perTriangle.Path, perTriangle.RegionKey));
        Assert.Equal((16L, 15L), (perTriangle.Count, perTriangle.Limit));

        // 4,369 triangles with 15 cells and one with 1 cell is 65,536 cells in one region.
        var full = Enumerable.Range(0, 4369).SelectMany(triangle => Cells(triangle, 15)).Concat(Cells(4369, 1)).ToList();
        Assert.Equal(HostCoverageMap.MaxChildCells, full.Count);
        HostCoverageRegion r0 = Region("r0", 4370, full);
        Assert.Equal(HostCoverageMap.MaxChildCells, LevelFourMap(r0).Regions.Single().ChildRoles.Count);
        var perRegion = Assert.Throws<HostCoverageDataException>(() => Region("r0", 4370, [.. full, .. Cells(4369, 2).Skip(1)]));
        Assert.Equal((HostCoverageRefusal.ChildRoleCapacity, "$.childRoles", "r0"), (perRegion.Reason, perRegion.Path, perRegion.RegionKey));
        Assert.Equal((65_537L, 65_536L), (perRegion.Count, perRegion.Limit));

        var r1 = new HostCoverageRegion("r1", [new(new(0, 0, 4370, 1), CoverageTriangleRole.Collar)], [new(1, 0f)], Cells(4370, 1));
        var perMap = Assert.Throws<HostCoverageDataException>(() => LevelFourMap(r0, r1));
        Assert.Equal((HostCoverageRefusal.ChildRoleCapacity, "$.regions", (string?)null), (perMap.Reason, perMap.Path, perMap.RegionKey));
        Assert.Equal((65_537L, 65_536L), (perMap.Count, perMap.Limit));
    }

    // r0 owns triangles 0 to 2 and 4 as collar and 3 as core. Triangles 0 to 3 refine at level 2, and triangle 4 has no
    // refinement range, so its effective level is 1.
    static HostCoverageMap BaseMap(IEnumerable<CoverageChildRole> childRoles) => new(Hash, [new(0, 0, 0, 0)],
        [new HostCoverageRegion("r0", [new(new(0, 0, 0, 3), CoverageTriangleRole.Collar), new(new(0, 0, 3, 1), CoverageTriangleRole.Core),
            new(new(0, 0, 4, 1), CoverageTriangleRole.Collar)], [new(0, 0f)], childRoles)],
        [new HostCoverageRefinement(new(0, 0, 0, 4), 2)], new("synthetic-exporter", "r1"));

    static string Digest(string domain, byte[] canonical) =>
        Convert.ToHexStringLower(SHA256.HashData([.. Encoding.UTF8.GetBytes(domain), .. canonical]));

    static IEnumerable<CoverageChildRole> Cells(int triangle, int count) =>
        Enumerable.Range(0, count).Select(cell => new CoverageChildRole(0, 0, triangle, 4, cell, CoverageTriangleRole.Core));

    static HostCoverageRegion Region(string key, int triangles, IEnumerable<CoverageChildRole> childRoles) =>
        new(key, [new(new(0, 0, 0, triangles), CoverageTriangleRole.Collar)], [new(0, 0f)], childRoles);

    static HostCoverageMap LevelFourMap(params HostCoverageRegion[] regions) => new(Hash, [new(0, 0, 0, 0), new(0, 0, 1, 1)], regions,
        [new HostCoverageRefinement(new(0, 0, 0, regions.Sum(region => region.Triangles.Sum(entry => entry.Triangles.Count))), 4)],
        new("synthetic-exporter", "r1"));
}
