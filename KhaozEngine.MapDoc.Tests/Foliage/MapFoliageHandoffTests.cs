using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageHandoffTests
{
    [Fact]
    public void HandoffBounds_IncludeSourceUnitsExactPoseAndMotionPadding()
    {
        var bounds = new MapLocalBounds(new(-70, -20, -10), new(30, 180, 45));
        Matrix4x4 pose = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(0.371f) *
            Matrix4x4.CreateTranslation(-4, -0.01f, -7);
        var batch = Batch(Sample("reed-a", pose));
        var extent = new MapFoliageModelExtent("reed-a", bounds, 0.01f, 0.35f);
        var handoff = MapFoliageRenderHandoff.Create(batch, [extent]);
        Assert.Equal(MapFoliageStatus.Complete, handoff.Status);
        Assert.Equal(Expected(bounds, pose, 0.01f, 0.35f), handoff.Bounds);

        // A second model has its own source units, root offset and motion envelope.
        var secondBounds = new MapLocalBounds(new(-2, -1, -3), new(1, 4, 2));
        Matrix4x4 secondPose = Matrix4x4.CreateTranslation(8, -3, -12);
        var two = MapFoliageRenderHandoff.Create(Batch(batch.Samples[0], Sample("reed-b", secondPose)),
            [extent, new("reed-b", secondBounds, 1, 0)]);
        Assert.Equal(Expected(bounds, pose, 0.01f, 0.35f).Union(new(6, -4, -15, 9, 1, -10)), two.Bounds);
        var unpadded = MapFoliageRenderHandoff.Create(batch, [extent with { MotionPaddingMetres = 0 }]);
        Assert.Equal(MapFoliageStatus.Complete, unpadded.Status);
        Assert.NotEqual(unpadded.DependencyDigest, handoff.DependencyDigest);
        Assert.Equal(Expected(bounds, pose, 0.01f, 0), unpadded.Bounds);
    }

    [Fact]
    public void HandoffMissingExtentOrPadding_RefusesInsteadOfCulling()
    {
        var batch = Batch(Sample("reed-a", Matrix4x4.Identity));
        var valid = new MapFoliageModelExtent("reed-a", new(Vector3.Zero, Vector3.One), 1, 0);
        IReadOnlyList<MapFoliageModelExtent>[] invalid =
        [
            [], [valid with { AssetId = "other" }], [valid, valid],
            [valid with { MotionPaddingMetres = float.NaN }],
            [valid with { MotionPaddingMetres = float.PositiveInfinity }],
            [valid with { MotionPaddingMetres = float.NegativeInfinity }],
            [valid with { MotionPaddingMetres = -0.01f }],
            [valid with { SourceUnitsToMetres = 0 }], [valid with { SourceUnitsToMetres = -1 }],
            [valid with { SourceUnitsToMetres = float.NaN }],
            [valid with { SourceUnitsToMetres = float.PositiveInfinity }],
            [valid with { BoundsInSourceUnits = new(Vector3.One, Vector3.Zero) }],
            [valid with { BoundsInSourceUnits = new(new(float.NaN, 0, 0), Vector3.One) }],
            [valid with { BoundsInSourceUnits = new(Vector3.Zero, new(float.PositiveInfinity, 1, 1)) }],
            [valid with { BoundsInSourceUnits = new(Vector3.Zero, new(float.MaxValue)), SourceUnitsToMetres = 2 }],
        ];
        foreach (var extents in invalid)
        {
            var missingExtent = MapFoliageRenderHandoff.Create(batch, extents);
            Assert.Equal(MapFoliageStatus.Invalid, missingExtent.Status);
            Assert.Null(missingExtent.Bounds);
            Assert.Empty(missingExtent.DependencyDigest);
        }
        var nonfinitePose = Matrix4x4.Identity;
        nonfinitePose.M41 = float.NaN;
        Assert.Equal(MapFoliageStatus.Invalid,
            MapFoliageRenderHandoff.Create(Batch(Sample("reed-a", nonfinitePose)), [valid]).Status);
    }

    [Theory]
    [InlineData(MapFoliageStatus.PartialResidency)]
    [InlineData(MapFoliageStatus.Unavailable)]
    [InlineData(MapFoliageStatus.CapacityExceeded)]
    [InlineData(MapFoliageStatus.MissingSupport)]
    [InlineData(MapFoliageStatus.Ambiguous)]
    [InlineData(MapFoliageStatus.NotRepresentable)]
    [InlineData(MapFoliageStatus.Invalid)]
    public void HandoffIncompleteSource_PreservesRefusal(MapFoliageStatus status)
    {
        var batch = new MapFoliageSampleBatch("cover-a", new(0, 0), status, "unusable", [], new(0, 0, 0, 0, 0, 0, 0, 0));
        var handoff = MapFoliageRenderHandoff.Create(batch, []);
        Assert.Equal(status, handoff.Status);
        Assert.Null(handoff.Bounds);
        Assert.Empty(handoff.DependencyDigest);
        Assert.Empty(handoff.Samples.Samples);
    }

    [Fact]
    public void HandoffEmptyCompleteBatch_HasNoBounds()
    {
        var handoff = MapFoliageRenderHandoff.Create(Batch(), []);
        Assert.Equal(MapFoliageStatus.Complete, handoff.Status);
        Assert.Null(handoff.Bounds);
        Assert.NotEmpty(handoff.DependencyDigest);
    }

    [Fact]
    public void HandoffRetainsKeysMatricesMaterialsRankAndCompleteness()
    {
        FoliageFixture fixture = FoliageFixtures.Create();
        var batch = MapFoliageDistribution.Generate(FoliageFixtures.Context(fixture), "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        string unchangedDistributionDigest = batch.DependencyDigest;
        var extents = batch.Samples.Select(s => s.AssetId).Distinct().Select(id =>
        {
            MapResolvedAsset asset = fixture.Assets.GetAsset(id);
            return new MapFoliageModelExtent(id, asset.RenderBounds, asset.SourceUnitsToMetres, 0.35f);
        }).ToArray();
        var handoff = MapFoliageRenderHandoff.Create(batch, extents);
        string digest = handoff.DependencyDigest;
        Assert.Equal(MapFoliageStatus.Complete, handoff.Status);
        Assert.Equal(batch.Status, handoff.Samples.Status);
        Assert.Equal(batch.Work, handoff.Samples.Work);
        Assert.Equal((batch.LayerId, batch.Chunk), (handoff.Samples.LayerId, handoff.Samples.Chunk));
        Assert.Equal(batch.Samples, handoff.Samples.Samples);
        Assert.Equal(unchangedDistributionDigest, handoff.Samples.DependencyDigest);
        Assert.Equal(batch.DependencyDigest, unchangedDistributionDigest);
        Assert.NotEqual(batch.DependencyDigest, digest);
        Assert.Equal(digest, MapFoliageRenderHandoff.Create(batch, extents.Reverse().ToArray()).DependencyDigest);
        Assert.Equal(digest, MapFoliageRenderHandoff.Create(batch,
            extents.Append(new("unused", new(Vector3.Zero, Vector3.One), 1, 100)).ToArray()).DependencyDigest);
        Assert.NotEqual(digest, MapFoliageRenderHandoff.Create(batch,
            extents.Select(e => e with { SourceUnitsToMetres = e.SourceUnitsToMetres * 2 }).ToArray()).DependencyDigest);
        Assert.NotEqual(digest, MapFoliageRenderHandoff.Create(batch,
            extents.Select(e => e with { BoundsInSourceUnits = new(e.BoundsInSourceUnits.Min, e.BoundsInSourceUnits.Max + Vector3.One) }).ToArray()).DependencyDigest);
        Array.Clear(extents);
        Assert.Equal(digest, handoff.DependencyDigest);
        Assert.Throws<NotSupportedException>((Action)(() => ((IList<MapFoliageSample>)handoff.Samples.Samples).Clear()));
        Assert.Throws<NotSupportedException>((Action)(() => ((IList<string>)handoff.Samples.Samples[0].MaterialResourceIds).Clear()));

        var materials = new[] { "material-a", "material-b" };
        var source = Sample("reed-a", Matrix4x4.Identity, materials);
        var rows = new[] { source };
        var detached = new MapFoliageRenderBatch(Samples: Batch(rows), Status: MapFoliageStatus.Complete,
            Bounds: new(0, 0, 0, 1, 1, 1), DependencyDigest: "invented-render-digest");
        materials[0] = "mutated";
        rows[0] = batch.Samples[0];
        Assert.Equal(new[] { "material-a", "material-b" }, detached.Samples.Samples[0].MaterialResourceIds);
    }

    static MapFoliageSample Sample(string asset, Matrix4x4 pose, IReadOnlyList<string>? materials = null) =>
        new(new(new("cover-a", "floor", -2, 3), 1, new("legacy-tile", 1, 0)), "room", null, asset,
            materials ?? ["material"], 10, pose.Translation, Quaternion.Identity, 1.2f, pose, 0.25f);

    static MapFoliageSampleBatch Batch(params MapFoliageSample[] samples) =>
        new("cover-a", new(-1, 0), MapFoliageStatus.Complete, "invented-distribution-digest", samples,
            new(2, samples.Length, 1, 2, 0, 0, 0, 256));

    static MapBox3 Expected(MapLocalBounds box, Matrix4x4 pose, float units, float padding)
    {
        var points = (from x in new[] { box.Min.X, box.Max.X }
                      from y in new[] { box.Min.Y, box.Max.Y }
                      from z in new[] { box.Min.Z, box.Max.Z }
                      select Vector3.Transform(new Vector3(x, y, z) * units, pose)).ToArray();
        return new((double)points.Min(p => p.X) - padding, (double)points.Min(p => p.Y) - padding,
            (double)points.Min(p => p.Z) - padding, (double)points.Max(p => p.X) + padding,
            (double)points.Max(p => p.Y) + padding, (double)points.Max(p => p.Z) + padding);
    }
}
