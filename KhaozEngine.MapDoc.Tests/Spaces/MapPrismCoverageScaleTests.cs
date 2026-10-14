using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

/// <summary>F-R13. Room coverage work is proportional to the parent pieces and peers near each room.</summary>
public sealed class MapPrismCoverageScaleTests
{
    // Sixteen separated 4 by 4 m rooms on a 4 by 4 grid inside one 64 by 64 m exterior of one metre cells.
    static List<Vector3> Grid() => new[] { 2, 20, 38, 56 }.SelectMany(z => new[] { 2, 20, 38, 56 }.Select(x => new Vector3(x, 0, -z))).ToList();

    [Fact]
    public void SeparatedRooms_InOneLargeParent_ValidateAtDefaultBudgets()
    {
        var sites = Grid();
        var limits = new MapRefinementLimits();
        // The rooms' union bounds span 58 by 58 parent cells of two triangles each, against every room.
        Assert.True(sites.Count * 58L * 58 * 2 > limits.MaxPairChecks);
        var view = PrefabFixtures.Effective(PrefabFixtures.ScatteredRooms(sites, new(1, 1))).CompleteView();

        Assert.Empty(MapPrismCoverage.Validate(view, limits));
    }

    [Fact]
    public void SeparatedRooms_StillReportARealPeerOverlap()
    {
        var sites = Grid();
        sites.Add(new(4, 1, -2));
        var view = PrefabFixtures.Effective(PrefabFixtures.ScatteredRooms(sites, new(1, 1))).CompleteView();

        var errors = MapPrismCoverage.Validate(view, new());

        string peer = "copy" + (sites.Count - 1) + "/room";
        Assert.Equal(new[] { "peer occupied overlap: 'a/room' with '" + peer + "'" }, errors);
    }

    // Findings recorded from the coverage loops before the broad phase, joined in their returned order.
    [Theory]
    [InlineData("basic", "")]
    [InlineData("village", "")]
    [InlineData("adjacent", "")]
    [InlineData("nested", "")]
    [InlineData("transformed", "")]
    [InlineData("reflex-storeys", "")]
    [InlineData("uneven-parent", "")]
    [InlineData("peer-overlap", "peer occupied overlap: 'a/room' with 'prefab/a/Record/peer-room'")]
    [InlineData("missing-parent", "prism 'a/room-domain': prism parent mismatch")]
    [InlineData("outside-parent", "prism 'a/room-domain': outside parent footprint")]
    [InlineData("zero-height", "prism 'a/room-domain': invalid prism payload or separation")]
    [InlineData("self-intersection", "prism 'a/room-domain': water domain: ring 0 self-intersects.")]
    public void ExistingRoomFixtures_KeepTheirFindingsAndOrder(string variant, string findings)
    {
        var fixture = variant switch { "basic" => PrefabFixtures.Create(), "village" => PrefabFixtures.Village(), _ => PrefabFixtures.Cave(variant) };

        var errors = MapPrismCoverage.Validate(PrefabFixtures.Effective(fixture).CompleteView(), new());

        Assert.Equal(findings, string.Join("|", errors));
    }

    [Fact]
    public void DenseStackedRooms_StillRefuseTheirPairBudget()
    {
        // Sixteen rooms stacked in one column over quarter metre parent cells. Every peer candidate overlaps in plan.
        var sites = Enumerable.Range(0, 16).Select(i => new Vector3(6, 4 * i, -6)).ToList();
        var view = PrefabFixtures.Effective(PrefabFixtures.ScatteredRooms(sites, new(1, 4))).CompleteView();

        var errors = MapPrismCoverage.Validate(view, new());

        // The partition room stage and the parent loop see 512 candidates per room and pass. The peer loop refuses:
        // its message has no "capacity exceeded:" prefix, which a partition refusal would carry.
        Assert.Contains("prism coverage: domain work exceeds 65536 pair checks", errors);
    }
}
