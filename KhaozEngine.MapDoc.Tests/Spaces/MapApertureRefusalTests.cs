using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapApertureRefusalTests
{
    [Theory]
    [InlineData("interval", MapDocumentRefusal.GeometryVertexSequenceInvalid)]
    [InlineData("height", MapDocumentRefusal.SpacePortalHeightInvalid)]
    [InlineData("repeated", MapDocumentRefusal.GeometryVertexSequenceInvalid)]
    [InlineData("open", MapDocumentRefusal.SpacePortalOpenTopInvalid)]
    [InlineData("chain", MapDocumentRefusal.SpaceChainUnresolved)]
    public void PortalGuard_ReportsTheRuleAndPortalWithoutChangingAdmission(string defect, MapDocumentRefusal reason)
    {
        var (set, portal) = Portal();
        MapSurfacePatch patch = set.Patches.Values.Single();
        if (defect == "interval") portal = portal with { Interval = portal.Interval.Take(1).ToArray() };
        else if (defect == "open")
        {
            portal = portal with { BandTop = null };
            int owner = patch.Records.FindIndex(r => r.Id == "from");
            patch.Records[owner] = ((MapSpaceDoc)patch.Records[owner]) with { Kind = MapSpaceKind.Cave };
        }
        else if (defect == "chain")
        {
            int chain = patch.Records.FindIndex(r => r.Id == "bottom");
            var original = (MapBoundaryChain)patch.Records[chain];
            patch.Records[chain] = original with { Vertices = original.Vertices.Select(v =>
                v with { Vertex = new("missing", v.Vertex.Address) }).ToArray() };
        }
        else if (defect == "height")
        {
            int top = patch.Records.FindIndex(r => r.Id == "top");
            var chain = (MapBoundaryChain)patch.Records[top];
            patch.Records[top] = chain with { Vertices = chain.Vertices.Select(v => v with { HeightUnits = 0 }).ToArray() };
        }
        else
        {
            portal = portal with { Interval = [portal.Interval[0], portal.Interval[0]] };
            foreach (string id in new[] { "bottom", "top" })
            {
                int index = patch.Records.FindIndex(r => r.Id == id);
                var chain = (MapBoundaryChain)patch.Records[index];
                patch.Records[index] = chain with { Vertices = [chain.Vertices[0], chain.Vertices[0]] };
            }
        }
        patch.Records[patch.Records.FindIndex(r => r.Id == portal.Id)] = portal;
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => Compile(set, portal));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(defect == "chain" ? "bottom" : "portal", error.RecordId);
        if (defect == "height") Assert.Equal(0, error.Index);
        var (repaired, accepted) = Portal();
        Assert.Equal(MapPortalKind.WallPortal, Compile(repaired, accepted).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChainFinding_PreservesOriginalIdentifiersAndReleasesTheStripAdapter(bool numeric)
    {
        var (set, portal) = Portal();
        var view = MapScopedSurfaces.CompleteView(set);
        var cause = new MapDocumentException(MapDocumentRefusal.GeometryVertexHeightMismatch, "known chain fault")
        { Patch = portal.BandBottom.Anchor, Index = 3, SurfaceId = "floor", FieldPath = "Heights" };
        var source = new MapBoundaryChain("source", MapChainKind.SurfaceEdge, portal.BandBottom.Anchor,
            [new(new("floor", MapLatticeAddress.Corner(0, 0)), null), new(new("floor", MapLatticeAddress.Corner(1, 0)), null)]);
        MapChainResolution refused = MapBoundaryGeometry.ResolveChain(source, view, _ =>
            throw (numeric ? new MapExactOverflowException() : cause));
        Assert.Equal(MapResolveStatus.Invalid, refused.Status);
        Assert.Empty(refused.Points);
        Assert.Equal(numeric ? MapDocumentRefusal.GeometryNotRepresentable : cause.Reason, refused.Finding!.Reason);
        var strip = new MapWallStrip("strip", portal.BandBottom, portal.BandTop!, MapStripFacing.Front, 1);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(strip, refused, new(MapResolveStatus.Resolved, [], null)));
        Assert.Equal(refused.Finding.Reason, error.Reason);
        if (!numeric)
        {
            Assert.Same(cause, error.InnerException);
            Assert.Equal(cause.Patch, error.Patch);
            Assert.Equal(3, error.Index);
            Assert.Equal("Heights", error.FieldPath);
        }
        MapChainResolution accepted = MapBoundaryGeometry.ResolveChain(source, view);
        Assert.Equal(MapResolveStatus.Resolved, accepted.Status);
        Assert.Null(accepted.Finding);
    }

    [Fact]
    public void ApertureBottom_MissingGeometryNamesTheAperture()
    {
        var (set, portal) = Portal();
        MapPortalFact valid = Compile(set, portal);
        MapPortalFact missing = valid with { Geometry = valid.Geometry with { Columns = [] } };
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapRelationApertures.BottomOf(missing));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal(portal.Id, error.RecordId);
        Assert.Equal(portal.BandBottom.Anchor, error.Patch);
        Assert.Equal(new MapExactValue(0, 1), MapRelationApertures.BottomOf(valid));
    }

    internal static (MapSurfaceSet Set, MapCavePortal Portal) Portal()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        MapLatticeVertex[] interval = [new("floor", MapLatticeAddress.Corner(0, 0)), new("floor", MapLatticeAddress.Corner(1, 0))];
        patch.Records.Add(new MapSpaceDoc("from", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceDoc("to", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapBoundaryChain("bottom", MapChainKind.Authored, null, interval.Select(v => new MapChainVertex(v, 0)).ToArray()));
        patch.Records.Add(new MapBoundaryChain("top", MapChainKind.Authored, null, interval.Select(v => new MapChainVertex(v, 100)).ToArray()));
        var portal = new MapCavePortal("portal", new("from", patch.Key), new("to", patch.Key), interval, new("bottom", patch.Key), new("top", patch.Key));
        patch.Records.Add(portal);
        return (set, portal);
    }

    [Fact]
    public void TerrainEditChainFallback_KeepsTheReferencedAnchor()
    {
        var (set, portal) = Portal();
        var patch = set.Patches.Values.Single();
        var chain = (MapBoundaryChain)patch.Records.Single(r => r.Id == "bottom");
        patch.Records.Remove(chain);
        var remote = MixedResolutionFixtures.Patch(new("floor", 1, 0), 1, 1, (_, _) => 0);
        var reference = new MapRecordRef(chain.Id, remote.Key);
        patch.Records[patch.Records.FindIndex(r => r.Id == portal.Id)] = portal with { BandBottom = reference };
        patch.Records.Add(new MapWallStrip("strip", reference, portal.BandTop!, MapStripFacing.Front, 1));
        remote.Records.Add(chain with { Vertices = chain.Vertices.Select(v =>
            v with { Vertex = new("missing", v.Vertex.Address) }).ToArray() });
        set.Patches.Add(remote.Key, remote);
        var after = set.Clone();
        after.Patches[patch.Key].Heights[0] = 1;

        MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
            MapTerrainEditChanges.Describe(set, after, MapNativeInvalidation.Terrain));
        Assert.Equal(MapDocumentRefusal.SpaceChainUnresolved, error.Reason);
        Assert.Equal(chain.Id, error.RecordId);
        Assert.Equal(remote.Key, error.Patch);

        remote.Records[0] = chain;
        after = set.Clone();
        after.Patches[patch.Key].Heights[0] = 1;
        Assert.Contains(patch.Key, MapTerrainEditChanges.Describe(set, after, MapNativeInvalidation.Terrain).WriteSet.Patches);
    }

    static MapPortalFact Compile(MapSurfaceSet set, MapCavePortal portal) => MapRelationApertures.Compile(MapScopedSurfaces.CompleteView(set),
        new(portal.Id, portal.BandBottom.Anchor), portal.FromSpace, portal.ToSpace, null);
}
