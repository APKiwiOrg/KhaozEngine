using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapWindowFoliageOwnerParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DistantExceptionalOwner_SeatDependenciesMatchFull(bool marker, bool tiled)
    {
        var f = WindowFoliageFixtures.DistantExceptionalOwner(marker);
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, tiled);
        var full = FoliageFixtures.Context(f);
        var rows = new List<MapFoliageDecision>();
        var expected = MapFoliageDistribution.Generate(full, "cover-a", new(0, 0), observe: rows.Add);
        Assert.Equal(MapFoliageStatus.Complete, expected.Status);
        Assert.Contains(rows, r => r.Reason == MapFoliageReason.Solid);
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(full.Foliage!.Layers[0], new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        MapWindowFoliageTests.Same(expected, MapFoliageDistribution.Generate(result.Context!, "cover-a", new(0, 0)));
        Assert.Contains(result.Context!.Witness.Required, k => k.Kind == "Patch" && k.Id == "floor/1/0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistantExceptionalOwner_MissingAdvertisedSeatRefusesCapture(bool marker)
    {
        var f = WindowFoliageFixtures.DistantExceptionalOwner(marker);
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, true);
        SurfaceStorageFixtures.DeletePayload(Assert.Single(saved.StorageDirectories), new("floor", 1, 0));
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(f.Document.Foliage!.Layers[0], new(0, 0)));
        Assert.Equal(MapFoliageStatus.Unavailable, result.Status);
        Assert.Null(result.Context);
    }

    [Theory]
    [InlineData(MapPrefabChildKind.Surface, false, false)]
    [InlineData(MapPrefabChildKind.Surface, false, true)]
    [InlineData(MapPrefabChildKind.Surface, true, false)]
    [InlineData(MapPrefabChildKind.Surface, true, true)]
    [InlineData(MapPrefabChildKind.Record, false, false)]
    [InlineData(MapPrefabChildKind.Record, false, true)]
    [InlineData(MapPrefabChildKind.Record, true, false)]
    [InlineData(MapPrefabChildKind.Record, true, true)]
    public void NativeIdentityMaskOwners_MatchFull(MapPrefabChildKind kind, bool distant, bool tiled)
    {
        var f = WindowFoliageFixtures.NativeMask(kind, distant);
        Assert.Empty(f.Document.NativeResourceTransforms ?? []);
        using var saved = WindowFoliageFixtures.Scene(f);
        var source = NativeSceneFixtures.Open(saved, tiled);
        var full = FoliageFixtures.Context(f);
        var rows = new List<MapFoliageDecision>();
        var expected = MapFoliageDistribution.Generate(full, "cover-a", new(0, 0), observe: rows.Add);
        Assert.Equal(MapFoliageStatus.Complete, expected.Status);
        Assert.Contains(rows, r => r.Reason == MapFoliageReason.Solid);
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(full.Foliage!.Layers[0], new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        MapWindowFoliageTests.Same(expected, MapFoliageDistribution.Generate(result.Context!, "cover-a", new(0, 0)));
        Assert.Contains(result.Context!.Witness.Required, k => k.Kind == kind.ToString() && k.Id == (kind == MapPrefabChildKind.Surface ? "mask-surface" : "mask-record"));
    }
}
