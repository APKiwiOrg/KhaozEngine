using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class ToolQueryRefusalTests
{
    [Theory]
    [InlineData("rectangle", MapDocumentRefusal.ToolRectangleIncomplete)]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    public void FoliageQuery_IdentifiesItsRequirementAndAdmitsRepair(string fault, MapDocumentRefusal reason)
    {
        var session = FoliageEditorFixtures.Session(FoliageFixtures.ReachableWirePreview());
        try
        {
            var query = new QueryService(session);
            string before = FoliageEditorFixtures.State(session);
            var error = Assert.Throws<MapDocumentException>(() => fault switch
            {
                "rectangle" => query.FoliageGet(FoliageFixtures.WireLayerId, x: 0),
                _ => query.FoliageGet("absent"),
            });
            Assert.Equal(reason, error.Reason);
            Assert.Equal(fault == "rectangle" ? "x,z,width,depth" : "layerId", error.FieldPath);
            if (fault == "rectangle") Assert.Equal("foliage_get", error.Operation);
            if (fault == "missing") Assert.Equal("absent", error.LayerId);
            Assert.Equal(before, FoliageEditorFixtures.State(session));
            Assert.NotNull(query.FoliageGet(FoliageFixtures.WireLayerId, 0, 0, 1, 1).Layer);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void AdvertisedFoliageChunk_IdentifiesUnavailableDensityAndAdmitsResidentBytes()
    {
        var session = FoliageEditorFixtures.Session(FoliageFixtures.ReachableWirePreview());
        try
        {
            var layer = session.WithDocument((doc, _) => doc.Foliage!.Layers[0]);
            var unavailable = MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, []);
            session.WithDocument((doc, _) => doc.Foliage = new(1, [unavailable]));
            var error = Assert.Throws<MapDocumentException>(() => new QueryService(session).FoliageGet(layer.Id, 0, 0, 1, 1));
            Assert.Equal(MapDocumentRefusal.FoliageChunkUnavailable, error.Reason);
            Assert.Equal(layer.Id, error.LayerId);
            Assert.Equal(new MapFoliageChunkKey(0, 0), error.FoliageChunk);
            Assert.Empty(unavailable.Chunks);
            Assert.Equal(layer.ChunkDigests, unavailable.ChunkDigests);
            session.WithDocument((doc, _) => doc.Foliage = new(1, [layer]));
            Assert.Single(new QueryService(session).FoliageGet(layer.Id, 0, 0, 1, 1).Chunks);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(256, 255)]
    public void FoliagePaint_ReportsItsInclusiveByteDomainAndWritesNothing(int target, int repaired)
    {
        var session = FoliageEditorFixtures.Session(FoliageFixtures.ReachableWirePreview());
        try
        {
            var tools = new FoliageTools(new(session), new(session));
            string before = FoliageEditorFixtures.State(session);
            var error = Assert.IsType<MapDocumentException>(Assert.Throws<McpException>(() =>
                tools.FoliagePaint(FoliageFixtures.WireLayerId, 0.5f, 0.5f, 1, target, 1)).InnerException);
            Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
            Assert.Equal(target, error.ActualInteger);
            Assert.Equal(0, error.MinimumInteger);
            Assert.Equal(255, error.MaximumInteger);
            Assert.Equal("target", error.FieldPath);
            Assert.Equal(before, FoliageEditorFixtures.State(session));
            tools.FoliagePaint(FoliageFixtures.WireLayerId, 0.5f, 0.5f, 1, repaired, 1);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void PrefabExport_RequiresALocalSessionAndAdmitsAnOpenedAsset()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("roomless"));
        var service = new PrefabService(session);
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => service.Export());
        Assert.Equal(MapDocumentRefusal.ToolPrefabSessionRequired, error.Reason);
        Assert.Equal("Export", error.Operation);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        service.Open("hut");
        Assert.NotNull(service.Export());
    }

    [Fact]
    public void MissingEffectiveResources_RefusesWithoutInventingAResolution()
    {
        var error = Assert.Throws<MapDocumentException>(() => QueryService.RequireMissingEffectiveResourcesForTests(null));
        Assert.Equal(MapDocumentRefusal.ToolEffectiveResourcesMissing, error.Reason);
        Assert.Equal("placements_in_rect", error.Operation);
        var fixture = PrefabFixtures.Create();
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        QueryService.RequireMissingEffectiveResourcesForTests(resources);
    }

    [Theory]
    [InlineData("device", MapDocumentRefusal.RenderFailed)]
    [InlineData("document", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("legacy", MapDocumentRefusal.RenderFailed)]
    [InlineData("render", MapDocumentRefusal.ReferenceMissing)]
    public void RenderCaptureFailure_PreservesItsCauseAndKnownFinding(string fault, MapDocumentRefusal reason)
    {
        Exception cause = fault switch
        {
            "document" => new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "source refusal") { ResourceId = "mesh" },
            "legacy" => new MapDocumentException("consumer refusal") { ResourceId = "mesh" },
            "render" => new MapRenderException(MapDocumentRefusal.ReferenceMissing, "nested capture"),
            _ => new InvalidOperationException("device failure"),
        };
        var error = Assert.Throws<MapRenderException>(() => MapRenderCapture.CaptureFailureForTests(cause));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(GpuBackendSelector.Select(), error.Backend);
        Assert.Equal("render", error.Operation);
        Assert.Same(cause, error.InnerException);
        if (fault == "document") Assert.Equal("mesh", error.Finding!.ResourceId);
        else Assert.Null(error.Finding);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, MapRenderCapture.CaptureFailureForTests(null));
    }
}
