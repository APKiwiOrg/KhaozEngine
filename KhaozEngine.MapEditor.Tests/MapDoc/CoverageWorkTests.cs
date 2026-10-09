using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class CoverageWorkTests
{
    // CoverageWorkTests (MapEditor.Tests, internal work counters)
    [Fact]
    public void CoverageValidator_SharesOneContextWithoutClonesAndRefusesOverBudgetDemandWithZeroCompiles()
    {
        var room = BoundFaceContextFixtures.EightByEight();
        var work = new MapBoundFaceWork();
        Assert.Empty(MapSpaceCoverageValidator.Validate(room.View, new MapRefinementLimits(), work));
        Assert.Equal((1, 2, 640L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));
        Assert.All(room.View.Witness.Present, p => Assert.Equal(p.Value, MapSurfaceSemantics.PatchDigest(room.View.Patch(p.Key).Patch!)));   // internal readers wrote nothing
        var refused = new MapBoundFaceWork();
        IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(room.View, new MapRefinementLimits(MaxContextFaces: 639), refused);
        Assert.Equal("refinement capacity: footprint 'room8-cells' (context faces)", Assert.Single(findings, f => f.Contains("room8-cells")));
        Assert.Equal((0, 0L, 640L), (refused.Compiles, refused.CompiledFaces, refused.ContextFacesCounted));
    }
    [Fact]
    public void Membership_ReadsOneQueryContextWithoutClones()
    {
        var room = BoundFaceContextFixtures.EightByEight();
        var work = new MapBoundFaceWork();
        MapMembershipResult m = new MapSpaceMembership(room.View).Query(new MapFramePoint(WorldFrame.Origin, new(2.5f, 1f, 3.5f)), work);
        Assert.Equal(("room8", MapMembershipStatus.Resolved), (m.SpaceId, m.Status));
        Assert.Equal((1, 2, 4L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // one floor cell and one ceiling cell
    }
}
