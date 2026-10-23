using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneEffectiveQueryTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(256, true)]
    public void EffectivePaint_UsesDeclaredRefinementFaces(int faces, bool accepts)
    {
        var fixture = PrefabFixtures.Create("uneven-painted");
        var queries = new MapQueryLimits { Refinement = new(MaxSourceFacesPerBound: faces) };
        if (accepts)
            Assert.NotEmpty(MapPrefabResolver.ExpandWithLimits(fixture.Document, fixture.Assets, queries).PaintRegions["ground"].Regions);
        else
        {
            var error = Assert.Throws<MapDocumentException>(() => MapPrefabResolver.ExpandWithLimits(fixture.Document, fixture.Assets, queries));
            Assert.Equal(new MapSceneCapacity("MaxSourceFacesPerBound", 8, faces), MapSceneCapacity.From(error));
        }
    }

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
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var capture = MapSceneSource.Capture(document, fixture.Assets);
        Assert.True(capture.Source is not null, string.Join("\n", capture.Findings));
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(0, -8), new(8, 0), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, queries), new(0, -100, -8, 8, 1000, 0), MapSceneChannels.All);
        var acquired = capture.Source.Acquire(scope);
        if (accepts)
        {
            Assert.True(acquired.Scene is not null, string.Join("\n", acquired.Findings));
            Assert.NotEmpty(acquired.Scene.Product.PaintRegions["ground"].Regions);
        }
        else
        {
            Assert.Equal(MapSceneStatus.CapacityExceeded, acquired.Status);
            Assert.Equal(new MapSceneCapacity("MaxPageMetadataChecks", 32770, 32769), acquired.Capacity);
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
