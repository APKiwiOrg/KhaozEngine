using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapSceneExtentBindingTests
{
    static MapSceneScope Window() => new(new(WorldFrame.Origin, new(-4, -4), new(4, 4), null, null,
        System.Enum.GetValues<MapSurfaceRole>(), null, new()), new MapBox3(-4, -500, -4, 4, 500, 4), MapSceneChannels.All);
    static MapDocument Document(MapAssetClosure closure, bool grown) => new()
    {
        Id = "invented-extent-scene",
        Bounds = new() { MinX = -32000, MinZ = -32000, MaxX = 32000, MaxZ = 32000 },
        NativeAssets = closure.Roots.ToList(),
        NumericIdHighWaterMark = 24,
        Placements = closure.Assets.Where(a => grown || a.Id == "prop-a").Select((a, i) => new MapPlacement
        { Id = a.Id, Kind = "cosmetic", AssetId = a.Id, NumericId = i + 1, X = a.Id == "prop-a" ? 0 : 1024 + i * 64, Y = 0, Z = 0 }).ToList(),
    };

    [Fact]
    public void RenderProofBinding_RequiresEverySourceAssetAndExactClosure()
    {
        var closure = NativeRenderAssetFixtures.Create("distant");
        using var document = MapDocumentSource.FromDocument(Document(closure, true));
        var source = MapSceneSource.Capture(document, closure).Source!;
        var budget = new GltfDecodeBudget(new());
        var partial = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        Assert.Equal(MapSceneSourceStatus.Invalid, source.WithRenderExtents(partial).Status);
        var proofs = MapRenderExtentValidator.Validate(closure, source.RequiredAssetIds, budget).Proofs!;
        var bound = source.WithRenderExtents(proofs);
        Assert.Equal(MapSceneSourceStatus.Ready, bound.Status);
        Assert.Null(source.Acquire(Window()).Witness.RenderExtentsDigest);
        Assert.Equal(proofs.Digest, bound.Source!.Acquire(Window()).Witness.RenderExtentsDigest);
        var other = NativeRenderAssetFixtures.Create();
        using var otherDocument = MapDocumentSource.FromDocument(Document(other, false));
        Assert.Equal(MapSceneSourceStatus.Invalid, MapSceneSource.Capture(otherDocument, other).Source!.WithRenderExtents(proofs).Status);
        var lie = NativeRenderAssetFixtures.Create("lying-distant");
        Assert.Equal(MapRenderAssetStatus.Invalid, MapRenderExtentValidator.Validate(lie, lie.Assets.Select(a => a.Id).ToArray(), budget).Status);
    }

    [Fact]
    public void TwentyFourDistinctDistantAssets_DoNotGrowSelectedDecodedRetention()
    {
        var closure = NativeRenderAssetFixtures.Create("distant");
        using var nearDocument = MapDocumentSource.FromDocument(Document(closure, false));
        using var grownDocument = MapDocumentSource.FromDocument(Document(closure, true));
        var near = MapSceneSource.Capture(nearDocument, closure).Source!;
        var grown = MapSceneSource.Capture(grownDocument, closure).Source!;
        var budget = new GltfDecodeBudget(new());
        var proofs = MapRenderExtentValidator.Validate(closure, grown.RequiredAssetIds, budget).Proofs!;
        var a = near.WithRenderExtents(proofs).Source!.Acquire(Window());
        var b = grown.WithRenderExtents(proofs).Source!.Acquire(Window());
        Assert.True(a.Witness.CompleteForScope);
        Assert.True(b.Witness.CompleteForScope);
        Assert.Equal(a.Witness.Required, b.Witness.Required);
        Assert.Equal(a.Work.PayloadReads, b.Work.PayloadReads);
        Assert.Equal(a.Work.PreparedFaces, b.Work.PreparedFaces);
        using var selectedA = MapRenderAssets.LoadSelected(closure, a.Scene!.Placements.Select(p => p.AssetId).ToArray(), proofs, budget).Assets!;
        long retained = selectedA.RetainedBytes;
        using var selectedB = MapRenderAssets.LoadSelected(closure, b.Scene!.Placements.Select(p => p.AssetId).ToArray(), proofs, budget).Assets!;
        Assert.Equal(retained, selectedB.RetainedBytes);
        Assert.DoesNotContain("prop-23", selectedB.ById.Keys);
    }
}
