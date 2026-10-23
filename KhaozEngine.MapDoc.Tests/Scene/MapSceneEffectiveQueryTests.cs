using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneEffectiveQueryTests
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(65536, true)]
    public void TransformedPartialPaint_UsesDeclaredMetadataAllowance(int pages, bool accepts)
    {
        var fixture = PrefabFixtures.Create("uneven-painted");
        var ground = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        for (int i = 0; i < 9; i++)
        {
            string id = "exterior-" + i;
            ground.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Exterior, null, null, [], [], [], []));
            ground.Records.Add(new MapSpaceFootprint(id + "-domain", new(id, ground.Key), ground.Key,
                Enumerable.Range(0, 4096).ToArray(), new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
        }
        var queries = new MapQueryLimits(MaxCandidatePatches: 128, MaxPageReads: pages, MaxRecordReads: 128, MaxRecordDepth: 128);
        if (accepts)
        {
            var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document),
                fixture.Assets, MapDocumentSurfaceSource.Capture(fixture.Document), queryLimits: queries);
            Assert.NotEmpty(product.PaintRegions["ground"].Regions);
        }
        else
        {
            var error = Assert.Throws<MapDocumentException>(() => MapEffectiveResolutionKernel.Resolve(
                MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets,
                MapDocumentSurfaceSource.Capture(fixture.Document), queryLimits: queries));
            Assert.Equal(new MapSceneCapacity("MaxPageMetadataChecks", 32770, 32769), MapSceneCapacity.From(error));
        }
    }

    [Fact]
    public void EffectivePaint_RefusesTheDeclaredCandidateAllowanceWithCapacity()
    {
        var fixture = PrefabFixtures.Create("uneven-painted");
        var queries = new MapQueryLimits(MaxCandidatePatches: 1);
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveResolutionKernel.Resolve(
            MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets,
            MapDocumentSurfaceSource.Capture(fixture.Document), queryLimits: queries));
        Assert.Equal(new MapSceneCapacity("MaxCandidatePatches", 2, 1), MapSceneCapacity.From(error));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, MapSceneSource.Classify(error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveProduct_UsesCallerLimitsForPaintAndCompleteViews(bool partialPaint)
    {
        var fixture = PrefabFixtures.Create(partialPaint ? "uneven-painted" : "uneven-unpainted");
        var chain = new MapDocument();
        MapSceneQueryFixtures.AddNativeChain(chain);
        fixture.Document.Surfaces.Refs.AddRange(chain.Surfaces.Refs);
        foreach (var patch in chain.Surfaces.Patches) fixture.Document.Surfaces.Patches.Add(patch.Key, patch.Value);
        var queries = new MapQueryLimits(MaxCandidatePatches: 128, MaxInspectedFaces: 8192,
            MaxPageReads: 256, MaxRecordReads: 128, MaxRecordDepth: 128);
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document),
            fixture.Assets, MapDocumentSurfaceSource.Capture(fixture.Document), queryLimits: queries);
        var view = product.CompleteView();
        Assert.Equal(queries, view.Scope.Limits);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        if (partialPaint) Assert.NotEmpty(product.PaintRegions["ground"].Regions);
    }
}
