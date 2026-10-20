using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapFoliageHandoffBoundaryTests
{
    [Fact]
    public void HandoffCosmeticExtents_NeverChangeNavOrStatics()
    {
        var fixture = FoliageFixtures.Create();
        var before = Build();
        var beforeStatics = Statics(before);
        var beforeNavCapture = NavCapture(before);
        var batch = MapFoliageDistribution.Generate(FoliageFixtures.Context(fixture), "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        var extents = batch.Samples.Select(s => s.AssetId).Distinct().Select(id =>
        {
            var asset = fixture.Assets.GetAsset(id);
            return new MapFoliageModelExtent(id, asset.RenderBounds, asset.SourceUnitsToMetres, 0);
        }).ToArray();
        var unpadded = MapFoliageRenderHandoff.Create(batch, extents);
        var padded = MapFoliageRenderHandoff.Create(batch, extents.Select(e => e with
        {
            BoundsInSourceUnits = new(new(-1000), new(1000)),
            MotionPaddingMetres = 1000,
        }).ToArray());
        Assert.Equal(MapFoliageStatus.Complete, padded.Status);
        Assert.NotEqual(unpadded.DependencyDigest, padded.DependencyDigest);
        Assert.NotEqual(unpadded.Bounds, padded.Bounds);
        Assert.True(padded.Bounds!.Value.MaxY > before.Bounds.MaxY);
        var after = Build();
        var afterPaddingStatics = Statics(after);
        var afterPaddingNavCapture = NavCapture(after);
        Assert.Equal(beforeStatics, afterPaddingStatics);
        Assert.Equal(beforeNavCapture, afterPaddingNavCapture);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(batch.DependencyDigest, padded.Samples.DependencyDigest);
        Assert.Equal(batch.Samples, padded.Samples.Samples);

        MapBuiltWorld Build() => MapWorldBuilder.Build(fixture.Document, fixture.Assets,
            new(FoliageFixtures.Options(), "foliage-handoff/1", new()));
    }

    static string Statics(MapBuiltWorld world) => string.Join("\n", world.Statics.Select(s =>
        s.OwnerId + "|" + s.Kind + "|" + s.Digest + "|" + s.Bounds)) + "\n" +
        string.Join("\n", world.Placements.Select(p => p.PlacementId + "|" + p.Digest + "|" + p.Envelope.Bounds));

    static string NavCapture(MapBuiltWorld world) => string.Join("\n", MapNavTiling.Partition(world,
        new(world.TileSize, 1, 4, Vector2.Zero), new(2, "handoff-profile", "handoff-controller")).Select(t =>
        t.Coord + "|" + t.Bounds + "|" + t.CaptureBounds + "|" + t.GeometryDigest + "|" + t.MediumDigest + "|" + t.CaptureIdentity));
}
