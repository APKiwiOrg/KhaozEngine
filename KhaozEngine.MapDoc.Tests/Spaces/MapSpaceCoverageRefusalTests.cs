using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapSpaceCoverageRefusalTests
{
    [Fact]
    public void MixedTopologyDiagnostics_KeepTheirReleasedOwnerOrder()
    {
        // W6-P1d review regression. A SpaceId-only finding must not sort before record a.
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        patch.Records.Add(new MapSpaceDoc("a", MapSpaceKind.Exterior, null, null, [], [], [], []));
        var room = new MapSpaceDoc("z", MapSpaceKind.Room, null, null, [], [], [], []);
        patch.Records.Add(room);
        var view = MapScopedSurfaces.CompleteView(set);
        view.InjectInvalidSpaceKindForTests(new("a", patch.Key), (MapSpaceKind)255);

        Assert.Equal(new[] { "invalid space kind 'a'", "room requires one prism 'z'" },
            MapSpaceCoverageValidator.Validate(view));

        Replace(patch, room with { Kind = MapSpaceKind.Exterior });
        view = MapScopedSurfaces.CompleteView(set);
        view.InjectInvalidSpaceKindForTests(new("a", patch.Key), (MapSpaceKind)255);
        Assert.Equal(new[] { "invalid space kind 'a'" },
            MapSpaceCoverageValidator.Validate(view));
    }

    [Fact]
    public void MissingCoverage_NamesItsFootprintAndCell()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        patch.SetPresent(0, 0, false);
        var findings = Findings(set);
        var missing = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.SpaceCoverageMissingBound, missing.Reason);
        Assert.Equal("footprint", missing.RecordId);
        Assert.Equal("exterior", missing.SpaceId);
        Assert.Equal(0, missing.SlotCell);
        patch.SetPresent(0, 0, true);
        Assert.Empty(Findings(set));
    }

    [Fact]
    public void CollapsedCoverage_NamesItsFootprintAndAcceptsPositiveSeparation()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        var ceiling = patch.Clone(); ceiling.Key = new("ceiling", 0, 0); ceiling.Records.Clear();
        set.Refs.Add(set.Refs.Single() with { Id = "ceiling", Role = MapSurfaceRole.Ceiling });
        set.Patches.Add(ceiling.Key, ceiling);
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        Replace(patch, footprint with { Upper = new(MapBoundKind.Ceiling, "ceiling", null) });
        var findings = Findings(set);
        var separation = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.SpaceCoverageSeparationInvalid, separation.Reason);
        Assert.Equal(footprint.Id, separation.RecordId);
        Assert.Equal(footprint.Space.Id, separation.SpaceId);
        Assert.Equal(0, separation.SlotCell);
        // One authored height unit is the smallest positive separation in this fixture.
        Array.Fill(ceiling.Heights, 1);
        Assert.Empty(Findings(set));
    }

    [Fact]
    public void BoundarySequence_KeepsTheSourceChainWhenAddingPortalContext()
    {
        var document = PortalBandFixtures.Build("header");
        var patch = document.Surfaces.Patches[new("hall-floor", 0, 0)];
        var chain = patch.Records.OfType<MapBoundaryChain>().Single(c => c.Id == "door-bottom");
        Replace(patch, chain with { Vertices = [chain.Vertices[0], chain.Vertices[^1]] });
        var findings = Findings(document.Surfaces);
        Assert.Contains(findings, f => f.Reason == MapDocumentRefusal.GeometryVertexSequenceInvalid &&
            f.RecordId == chain.Id && f.Patch == chain.SourcePatch && f.FieldPath == "Vertices");
        Replace(patch, chain);
        Assert.Empty(Findings(document.Surfaces));
    }

    [Fact]
    public void CoincidentSupport_NamesBothFootprintsWithoutInventingPeerOverlap()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        var other = patch.Clone(); other.Key = new("other", 0, 0); other.Records.Clear();
        set.Refs.Add(set.Refs.Single() with { Id = "other" });
        set.Patches.Add(other.Key, other);
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        patch.Records.Add(footprint with
        {
            Id = "other-footprint",
            Lattice = other.Key,
            Lower = new(MapBoundKind.SupportFloor, "other", null)
        });
        var coincidence = Assert.Single(Findings(set));
        Assert.Equal(MapDocumentRefusal.SpaceSupportCoincident, coincidence.Reason);
        Assert.Equal(footprint.Id, coincidence.RecordId);
        Assert.Equal("other-footprint", coincidence.RelatedRecordId);
        Assert.Equal(0, coincidence.SlotCell);
        Assert.Equal(0, coincidence.RelatedSlotCell);
        Array.Fill(other.Heights, 1);
        Assert.Empty(Findings(set));
    }

    static IReadOnlyList<MapDocumentFinding> Findings(MapSurfaceSet set)
    {
        var view = MapScopedSurfaces.CompleteView(set);
        var findings = MapSpaceCoverageValidator.ValidateFindings(view);
        Assert.Equal(MapSpaceCoverageValidator.Validate(view), findings.Select(f => f.Message));
        if (findings.Count != 0)
        {
            var error = Assert.Throws<MapDocumentException>(() => MapSpaceCoverageValidator.Require(view));
            Assert.Equal(findings[0].Reason, error.Reason);
            Assert.Equal(findings, error.Findings);
            if (findings is IList<MapDocumentFinding> mutable)
                Assert.Throws<NotSupportedException>(() => mutable[0] = findings[0]);
        }
        else MapSpaceCoverageValidator.Require(view);
        return findings;
    }
    static void Replace(MapSurfacePatch patch, MapTopologyRecord replacement) => patch.Records[patch.Records.FindIndex(r => r.Id == replacement.Id)] = replacement;
}
