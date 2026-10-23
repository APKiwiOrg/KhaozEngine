using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneEffectiveQueryTests
{
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
