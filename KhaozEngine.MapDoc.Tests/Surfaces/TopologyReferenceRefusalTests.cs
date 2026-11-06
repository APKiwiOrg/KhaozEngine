using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TopologyReferenceRefusalTests
{
    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("anchor", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("type", MapDocumentRefusal.ReferenceKindMismatch)]
    public void Lookup_distinguishes_missing_anchor_and_record_kind(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var patch = world.Patches[0].Key;
        var target = new MapRecordRef(fault == "missing" ? "absent" : fault == "type" ? "a" : "lower",
            fault == "anchor" ? new("floor", 1, 0) : patch);
        TopologyRecordFixtures.Replace<MapWallStrip>(world, "wall", s => s with { LowerChain = target });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("wall", finding.RecordId);
        Assert.Equal(target.Id, finding.RelatedRecordId);
        Assert.Equal(target.Anchor, finding.Patch);
        if (fault == "anchor") Assert.Equal(patch, finding.ActualAnchor);
        if (fault == "type") Assert.Equal(typeof(MapSpaceDoc), finding.RecordType);
        TopologyRecordFixtures.Replace<MapWallStrip>(world, "wall", s => s with { LowerChain = new("lower", patch) });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("blank", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("denominator", MapDocumentRefusal.ValueOutsideRange)]
    public void Vertex_keeps_the_missing_surface_or_original_scalar_rule(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var vertex = new MapLatticeVertex(fault == "blank" ? " " : fault == "missing" ? "absent" : "floor",
            fault == "denominator" ? default : MapLatticeAddress.Corner(0, 0));
        TopologyRecordFixtures.Replace<MapSurfaceSeam>(world, "seam", s => s with { Pairs = new[] { (vertex, s.Pairs[1].Second) } });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("seam", finding.RecordId);
        Assert.Equal(vertex.SurfaceId, finding.SurfaceId);
        if (fault == "denominator")
        {
            Assert.Equal("denominator", finding.FieldPath);
            Assert.Equal(0L, finding.ActualInteger);
            Assert.Equal(1L, finding.MinimumInteger);
            Assert.Equal(64L, finding.MaximumInteger);
        }
        var accepted = new MapLatticeVertex("floor", MapLatticeAddress.Create(1, 0, 64));
        TopologyRecordFixtures.Replace<MapSurfaceSeam>(world, "seam", s => s with { Pairs = new[] { (accepted, s.Pairs[0].Second) } });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.ReferenceMissing)]
    [InlineData(true, MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void Edge_keeps_the_patch_and_mismatching_surface(bool surfaceFault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var seam = world.Patches[0].Records.OfType<MapSurfaceSeam>().Single();
        var edge = surfaceFault ? seam.First with { From = seam.First.From with { SurfaceId = "ceiling" } }
            : seam.First with { Patch = new("floor", 1, 0) };
        TopologyRecordFixtures.Replace<MapSurfaceSeam>(world, "seam", s => s with { First = edge });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("seam", finding.RecordId);
        Assert.Equal(edge.Patch, finding.Patch);
        if (surfaceFault) Assert.Equal("ceiling", finding.SurfaceId);
        TopologyRecordFixtures.Replace<MapSurfaceSeam>(world, "seam", _ => seam);
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4098, 4097)]
    public void Chain_cardinality_accepts_both_limits(int refused, int accepted)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var vertex = new MapChainVertex(new("floor", MapLatticeAddress.Corner(0, 0)), null);
        TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", c => c with { Vertices = Enumerable.Repeat(vertex, refused).ToArray() });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal("lower", finding.RecordId);
        Assert.Equal("Vertices", finding.FieldPath);
        Assert.Equal((long)refused, finding.ActualCount);
        Assert.Equal(2L, finding.MinimumInteger);
        Assert.Equal(4097L, finding.MaximumInteger);
        TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", c => c with { Vertices = Enumerable.Repeat(vertex, accepted).ToArray() });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData("source-null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("source-missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("authored-source", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("height", MapDocumentRefusal.SpaceChainHeightKindMismatch)]
    [InlineData("surface", MapDocumentRefusal.AuthoredValueMismatch)]
    public void Chain_distinguishes_kind_source_height_and_surface(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        var original = world.Patches[0].Records.OfType<MapBoundaryChain>().Single(c => c.Id == "lower");
        var vertices = original.Vertices.ToArray();
        if (fault == "height") vertices[0] = vertices[0] with { HeightUnits = 0 };
        if (fault == "surface") vertices[0] = vertices[0] with { Vertex = vertices[0].Vertex with { SurfaceId = "ceiling" } };
        var bad = original with
        {
            Kind = fault == "kind" ? (MapChainKind)255 : fault == "authored-source" ? MapChainKind.Authored : original.Kind,
            SourcePatch = fault == "source-null" ? null : fault == "source-missing" ? new("floor", 1, 0) : original.SourcePatch,
            Vertices = fault == "authored-source" ? vertices.Select(v => v with { HeightUnits = 0 }).ToArray() : vertices,
        };
        TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", _ => bad);
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("lower", finding.RecordId);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapChainKind), finding.EnumType);
            Assert.Equal(255L, finding.ActualInteger);
        }
        if (fault is "source-missing" or "authored-source" or "surface") Assert.Equal(bad.SourcePatch, finding.Patch);
        if (fault == "height") Assert.Equal(0, finding.Index);
        if (fault == "surface") Assert.Equal("ceiling", finding.SurfaceId);
        TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", _ => original);
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("open-top", MapDocumentRefusal.SpacePortalOpenTopInvalid)]
    [InlineData("interval", MapDocumentRefusal.GeometryVertexSequenceInvalid)]
    [InlineData("front", MapDocumentRefusal.SpacePortalMembershipMismatch)]
    [InlineData("back", MapDocumentRefusal.SpacePortalMembershipMismatch)]
    public void Portal_identifies_its_top_interval_and_endpoint_membership(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var original = world.Patches[0].Records.OfType<MapCavePortal>().Single();
        if (fault == "open-top") TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", p => p with { BandTop = null });
        if (fault == "interval") TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", p => p with { Interval = p.Interval.Take(1).ToArray() });
        if (fault is "front" or "back") TopologyRecordFixtures.Replace<MapSpaceDoc>(world, fault == "front" ? "a" : "b", s => s with { Portals = [] });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("portal", finding.RecordId);
        if (fault == "interval")
        {
            Assert.Equal(1L, finding.ActualCount);
            Assert.Equal(2L, finding.MinimumInteger);
        }
        if (fault is "front" or "back")
        {
            Assert.Equal(fault == "front" ? "a" : "b", finding.SpaceId);
            Assert.Equal(0L, finding.ActualCount);
            Assert.Equal(1L, finding.ExpectedCount);
        }
        if (fault == "open-top")
        {
            TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", _ => original);
        }
        else if (fault == "interval") TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", _ => original);
        else TopologyRecordFixtures.Replace<MapSpaceDoc>(world, fault == "front" ? "a" : "b", s => s with
        { Portals = new[] { new MapBoundaryRef(new("portal", world.Patches[0].Key), fault == "front" ? MapSide.Front : MapSide.Back) } });
        Assert.Empty(world.Validate());
    }

    [Theory]
    [InlineData("owner", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("upper", MapDocumentRefusal.SpaceLinkMembershipMismatch)]
    public void Link_retains_the_forbidden_owner_or_unlisted_endpoint(string fault, MapDocumentRefusal reason)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        if (fault == "owner") TopologyRecordFixtures.Replace<MapVerticalLink>(world, "link", l => l with { GeometryOwners = new[] { new MapRecordRef("a", world.Patches[0].Key) } });
        else TopologyRecordFixtures.Replace<MapSpaceDoc>(world, fault == "upper" ? "a" : "b", s => s with { Links = [] });
        var finding = Assert.Single(Findings(world));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("link", finding.RecordId);
        if (fault == "owner")
        {
            Assert.Equal("a", finding.RelatedRecordId);
            Assert.Equal(typeof(MapSpaceDoc), finding.RecordType);
        }
        else
        {
            Assert.Equal(fault == "upper" ? "a" : "b", finding.SpaceId);
            Assert.Equal(0L, finding.ActualCount);
            Assert.Equal(1L, finding.ExpectedCount);
        }
        if (fault == "owner") TopologyRecordFixtures.Replace<MapVerticalLink>(world, "link", l => l with { GeometryOwners = new[] { new MapRecordRef("wall", world.Patches[0].Key) } });
        else TopologyRecordFixtures.Replace<MapSpaceDoc>(world, fault == "upper" ? "a" : "b", s => s with { Links = new[] { new MapRecordRef("link", world.Patches[0].Key) } });
        Assert.Empty(world.Validate());
    }

    static IReadOnlyList<MapDocumentFinding> Findings(TopologyWorld world)
    {
        var findings = new List<MapDocumentFinding>();
        var errors = MapTopologyReferenceValidator.Validate(world.Surfaces, world.Patches, findings);
        Assert.NotEmpty(errors);
        Assert.NotEmpty(findings);
        return findings;
    }
}
