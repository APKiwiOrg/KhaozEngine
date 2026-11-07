using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapPrismCoverageRefusalTests
{
    [Theory]
    [InlineData(false, MapDocumentRefusal.WaterDomainEmpty)]
    [InlineData(true, MapDocumentRefusal.WaterDomainZeroArea)]
    public void DomainCompilation_KeepsItsConcreteRuleThroughThePublicSnapshotAndAdapter(bool collinear, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var patch = set.Patches.Values.Single();
        var prism = patch.Records.OfType<MapPrismDomain>().Single();
        var refused = prism.Footprint with
        {
            Rects = [],
            Rings = collinear
            ? [new([MapLatticeAddress.Corner(0, 0), MapLatticeAddress.Corner(1, 0), MapLatticeAddress.Corner(2, 0)])] : []
        };
        Replace(patch, prism with { Footprint = refused });
        var finding = Assert.Single(Findings(MapOccupiedPartitionRefusalTests.View(set)));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(prism.Id, finding.RecordId);
        Assert.Equal(prism.Space.Id, finding.SpaceId);
        Replace(patch, prism);
        Assert.Empty(Findings(MapOccupiedPartitionRefusalTests.View(set)));
    }

    [Fact]
    public void ParentPairBudget_KeepsTheMeasuredFindingAndAcceptsItsInclusiveBoundary()
    {
        var view = MapOccupiedPartitionRefusalTests.View(MapOccupiedPartitionRefusalTests.Scene(1));
        // One rectangular prism is checked against the parent's two triangular floor pieces.
        var findings = MapPrismCoverage.ValidateFindings(view, new(MaxPairChecks: 1));
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal("WaterDomainPairChecks", finding.Budget);
        Assert.Equal(2, finding.ActualCount);
        Assert.Equal(1, finding.Limit);
        var error = Assert.Throws<MapDocumentException>(() => MapPrismCoverage.Require(view, new(MaxPairChecks: 1)));
        Assert.Equal(findings, error.Findings);
        Assert.Empty(MapPrismCoverage.ValidateFindings(view, new(MaxPairChecks: 2)));
        MapPrismCoverage.Require(view, new(MaxPairChecks: 2));
    }

    [Theory]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("parent", MapDocumentRefusal.SpacePrismParentMismatch)]
    [InlineData("footprint", MapDocumentRefusal.SpaceRoomOccupancyInvalid)]
    [InlineData("count", MapDocumentRefusal.SpaceRoomOccupancyInvalid)]
    public void PrismAdmission_KeepsTheFailedRuleThroughItsFinding(string defect, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var patch = set.Patches.Values.Single();
        var room = patch.Records.OfType<MapSpaceDoc>().Single(s => s.Kind == MapSpaceKind.Room);
        var prism = patch.Records.OfType<MapPrismDomain>().Single();
        if (defect is "kind" or "parent") Replace(patch, defect == "kind"
            ? room with { Kind = MapSpaceKind.Exterior } : room with { Parent = null });
        else if (defect == "footprint") patch.Records.Add(patch.Records.OfType<MapSpaceFootprint>().Single() with
        { Id = "second-domain", Space = prism.Space });
        else patch.Records.Add(prism with { Id = "second-prism" });
        var findings = Findings(MapOccupiedPartitionRefusalTests.View(set));
        Assert.Equal(defect == "count" ? 2 : 1, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(reason, f.Reason);
            Assert.Equal(room.Id, f.SpaceId);
            Assert.Equal(defect == "kind" ? "Kind" : defect == "parent" ? "Parent" : defect == "footprint" ? "Footprints" : "Prisms", f.FieldPath);
        });
        if (defect == "count") Assert.All(findings, f => { Assert.Equal(2L, f.ActualCount); Assert.Equal(1L, f.ExpectedCount); });
        if (defect is "kind" or "parent") Replace(patch, room);
        else patch.Records.RemoveAll(r => r.Id is "second-domain" or "second-prism");
        Assert.Empty(Findings(MapOccupiedPartitionRefusalTests.View(set)));
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("owner", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void EffectiveStorey_ReportsVersionBeforeOwnership(string defect, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var prism = set.AllRecords().OfType<MapPrismDomain>().Single();
        var valid = new MapStoreyDoc(1, prism.StoreyId, "Storey", null);
        var refused = defect == "version" ? valid with { PayloadVersion = 2 } : valid with { InstanceId = "other" };
        var error = Assert.Throws<MapDocumentException>(() => MapPrismCoverage.RequireStorey(View(set, defect == "missing" ? [] : [refused]), prism));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(prism.Id, error.RecordId);
        Assert.Equal(prism.StoreyId, error.EntityId);
        if (defect == "version") { Assert.Equal(2, error.ActualVersion); Assert.Equal(new[] { 1 }, error.SupportedVersions); }
        if (defect == "owner") { Assert.Equal("other", error.ActualText); Assert.Equal(prism.InstanceId, error.InstanceId); }
        MapPrismCoverage.RequireStorey(View(set, [valid]), prism);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("prism-owner", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("storey-owner", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void StandaloneStorey_SelectsTheFirstFailedOwnershipField(string defect, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var patch = set.Patches.Values.Single();
        var prism = patch.Records.OfType<MapPrismDomain>().Single();
        var storey = new MapStoreyDoc(1, prism.StoreyId, "Storey", null);
        var document = new MapDocument
        {
            Surfaces = set,
            BuildingIntent = new(1, [], [], [], [])
            {
                Storeys = defect == "missing" ? [] : [defect == "version" ? storey with { PayloadVersion = 2 }
            : defect == "storey-owner" ? storey with { InstanceId = "other" } : storey]
            }
        };
        if (defect == "prism-owner") Replace(patch, prism with { InstanceId = "other" });
        var error = Assert.Throws<MapDocumentException>(() => MapPrismCoverage.RequireStandaloneStoreys(document));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(prism.Id, error.RecordId);
        Assert.Equal(prism.StoreyId, error.EntityId);
        Assert.Equal(defect == "missing" ? "StoreyId" : defect == "version" ? "Storey.PayloadVersion"
            : defect == "prism-owner" ? "InstanceId" : "Storey.InstanceId", error.FieldPath);
        if (defect == "version") { Assert.Equal(2, error.ActualVersion); Assert.Equal(new[] { 1 }, error.SupportedVersions); }
        Replace(patch, prism);
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = [storey] };
        MapPrismCoverage.RequireStandaloneStoreys(document);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("record-retired")]
    [InlineData("record-removed")]
    [InlineData("storey-retired")]
    [InlineData("storey-removed")]
    [InlineData("other-owner")]
    public void UnpackedPrism_RequiresTheMatchingLiveStoreyTransfer(string defect)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var prism = set.AllRecords().OfType<MapPrismDomain>().Single();
        var record = new MapPrefabBindingTransfer("source", new(new(MapPrefabChildKind.Record, "room"), prism.Id, null, false));
        var storey = new MapPrefabBindingTransfer("source", new(new(MapPrefabChildKind.Storey, "storey"), prism.StoreyId, null, false));
        var document = new MapDocument
        {
            Surfaces = set,
            BuildingIntent = new(1, [], [], [], [])
            { Storeys = [new(1, prism.StoreyId, "Storey", null)] }
        };
        document.Prefabs = new(1, 1, [], defect == "missing" ? [record] :
            [defect == "record-retired" ? record with { Retired = true } : defect == "record-removed"
                ? record with { Binding = record.Binding with { Removed = true } } : record,
                defect == "storey-retired" ? storey with { Retired = true } : defect == "storey-removed"
                    ? storey with { Binding = storey.Binding with { Removed = true } } : defect == "other-owner"
                        ? storey with { InstanceId = "other" } : storey]);
        var error = Assert.Throws<MapDocumentException>(() => MapPrismCoverage.RequireStandaloneStoreys(document));
        Assert.Equal(MapDocumentRefusal.SpaceStoreyTransferMissing, error.Reason);
        Assert.Equal(prism.Id, error.RecordId);
        Assert.Equal("source", error.InstanceId);
        Assert.Equal(prism.StoreyId, error.EntityId);
        document.Prefabs = new(1, 1, [], [record, storey]);
        MapPrismCoverage.RequireStandaloneStoreys(document);
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.SpacePrismOutsideParent)]
    [InlineData(true, MapDocumentRefusal.SpacePrismParentSeparationInvalid)]
    public void PrismContainment_RetainsItsPrismAndParent(bool vertical, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var patch = set.Patches.Values.Single();
        var prism = patch.Records.OfType<MapPrismDomain>().Single();
        Replace(patch, vertical ? prism with { LowerY = -2, UpperY = -1 }
            : prism with { Footprint = prism.Footprint with { Rects = [new(0, 0, 2, 1)] } });
        var findings = Findings(MapOccupiedPartitionRefusalTests.View(set));
        Assert.NotEmpty(findings);
        Assert.All(findings, f =>
        {
            Assert.Equal(reason, f.Reason);
            Assert.Equal(prism.Id, f.RecordId);
            Assert.Equal(prism.Space.Id, f.SpaceId);
            Assert.Equal(prism.Parent.Id, f.RelatedRecordId);
        });
        var coverage = new List<MapDocumentFinding>();
        MapSpaceCoverageValidator.Validate(MapOccupiedPartitionRefusalTests.View(set), new(), coverage);
        Assert.Contains(coverage, f => f.Reason == reason && f.RecordId == prism.Id && f.RelatedRecordId == prism.Parent.Id);
        Replace(patch, prism);
        Assert.Empty(Findings(MapOccupiedPartitionRefusalTests.View(set)));
    }

    [Fact]
    public void PeerOverlap_NamesBothOccupiedOwners()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var patch = set.Patches.Values.Single();
        var prism = patch.Records.OfType<MapPrismDomain>().Single();
        patch.Records.Add(new MapSpaceDoc("peer", MapSpaceKind.Room, prism.Parent, null, [], [], [], []));
        patch.Records.Add(prism with { Id = "peer-prism", Space = new("peer", patch.Key) });
        var findings = Findings(MapOccupiedPartitionRefusalTests.View(set));
        Assert.NotEmpty(findings);
        Assert.All(findings, f =>
        {
            Assert.Equal(MapDocumentRefusal.SpaceOccupiedOverlap, f.Reason);
            Assert.Equal(new[] { "peer", prism.Space.Id }, new[] { f.SpaceId!, f.RelatedRecordId! }.Order());
        });
        Replace(patch, (prism with { Id = "peer-prism", Space = new("peer", patch.Key), LowerY = 1, UpperY = 2 }));
        Assert.Empty(Findings(MapOccupiedPartitionRefusalTests.View(set)));
    }

    [Fact]
    public void StripFinding_KeepsTheChainSubjectWhenAddingStripContext()
    {
        var (set, portal) = MapApertureRefusalTests.Portal();
        var patch = set.Patches.Values.Single();
        var chain = patch.Records.OfType<MapBoundaryChain>().Single(c => c.Id == portal.BandBottom.Id);
        Replace(patch, chain with
        {
            Vertices = chain.Vertices.Select(v => v with
            { Vertex = new("missing", v.Vertex.Address) }).ToArray()
        });
        patch.Records.Add(new MapWallStrip("strip", portal.BandBottom, portal.BandTop!, MapStripFacing.Front, 1));
        var findings = new List<MapDocumentFinding>();
        var messages = new List<string>();
        Validate();
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.SpaceChainUnresolved, finding.Reason);
        Assert.Equal(chain.Id, finding.RecordId);
        Assert.Equal(portal.BandBottom.Anchor, finding.Patch);
        Assert.Contains(finding.Message, messages);
        Replace(patch, chain);
        findings.Clear(); messages.Clear();
        Validate();
        Assert.Empty(messages);
        Assert.Empty(findings);

        void Validate()
        {
            var view = MapScopedSurfaces.CompleteView(set);
            MapSpaceBoundaryCoverage.Validate(view, MapSpaceGeometry.Records(view).ToArray(), [],
                (_, _, text) => messages.Add(text), (id, _, f) => { if (id == "strip") findings.Add(f); });
        }
    }

    static IReadOnlyList<MapDocumentFinding> Findings(MapScopedSurfaces view)
    {
        var findings = MapPrismCoverage.ValidateFindings(view, new());
        Assert.Equal(MapPrismCoverage.Validate(view, new()), findings.Select(f => f.Message));
        if (findings.Count != 0)
        {
            var error = Assert.Throws<MapDocumentException>(() => MapPrismCoverage.Require(view, new()));
            Assert.Equal(findings[0].Reason, error.Reason);
            Assert.Equal(findings, error.Findings);
        }
        else MapPrismCoverage.Require(view, new());
        return findings;
    }

    static MapScopedSurfaces View(MapSurfaceSet set, IReadOnlyList<MapStoreyDoc> storeys) => MapScopedSurfaces.CompleteEffective(
        new(set, new Dictionary<string, MapEffectiveTransform>(), new string('a', 64), storeys));
    static void Replace(MapSurfacePatch patch, MapTopologyRecord replacement) => patch.Records[patch.Records.FindIndex(r => r.Id == replacement.Id)] = replacement;
}
