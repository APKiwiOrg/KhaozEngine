using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Programmatic child role overrides on host coverage regions: each rule refuses with its own reason and
/// promised identifiers beside a passing control, and each capacity accepts its inclusive limit. All data is synthetic.</summary>
public class HostCoverageChildRoleTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    static readonly CoverageChildRole BaseOverride = new(0, 0, 1, 2, 0, CoverageTriangleRole.Core);

    [Theory]
    [InlineData("triangle-7", HostCoverageRefusal.ChildRoleOutsideRegion, "$.childRoles[0]", "r0", 7, null, 0)]
    [InlineData("level-0", HostCoverageRefusal.RefinementLevel, "$.childRoles[0].level", null, 1, null, null)]
    [InlineData("level-5", HostCoverageRefusal.RefinementLevel, "$.childRoles[0].level", null, 1, null, null)]
    [InlineData("level-3", HostCoverageRefusal.ChildRoleLevelMismatch, "$.regions[0].childRoles[0].level", "r0", 1, 3, null)]
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

    // r0 owns triangles 0 to 2 as collar and 3 as core, and triangles 0 to 3 refine at level 2.
    static HostCoverageMap BaseMap(IEnumerable<CoverageChildRole> childRoles) => new(Hash, [new(0, 0, 0, 0)],
        [new HostCoverageRegion("r0", [new(new(0, 0, 0, 3), CoverageTriangleRole.Collar), new(new(0, 0, 3, 1), CoverageTriangleRole.Core)],
            [new(0, 0f)], childRoles)],
        [new HostCoverageRefinement(new(0, 0, 0, 4), 2)], new("synthetic-exporter", "r1"));

    static IEnumerable<CoverageChildRole> Cells(int triangle, int count) =>
        Enumerable.Range(0, count).Select(cell => new CoverageChildRole(0, 0, triangle, 4, cell, CoverageTriangleRole.Core));

    static HostCoverageRegion Region(string key, int triangles, IEnumerable<CoverageChildRole> childRoles) =>
        new(key, [new(new(0, 0, 0, triangles), CoverageTriangleRole.Collar)], [new(0, 0f)], childRoles);

    static HostCoverageMap LevelFourMap(params HostCoverageRegion[] regions) => new(Hash, [new(0, 0, 0, 0), new(0, 0, 1, 1)], regions,
        [new HostCoverageRefinement(new(0, 0, 0, regions.Sum(region => region.Triangles.Sum(entry => entry.Triangles.Count))), 4)],
        new("synthetic-exporter", "r1"));
}
