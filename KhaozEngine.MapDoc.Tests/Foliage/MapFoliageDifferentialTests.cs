using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageDifferentialTests
{
    [Fact]
    public void PolicyDifferential_JoinsStableAddressesBeforeVersionedKeys()
    {
        MapFoliageDecision old = Decision(1);
        MapFoliageDecision next = Decision(2);
        Assert.NotEqual(old.Sample!.Key, next.Sample!.Key);
        var changes = MapFoliageDifferentialCore.JoinBlock([old], [next]);
        Assert.Equal(MapFoliageChangeKind.Changed, Assert.Single(changes).Kind);
        Assert.Equal(new MapFoliageComparisonAddress("invented", -1, 64), changes[0].Address);
        Assert.Empty(MapFoliageDifferentialCore.JoinBlock([old], [Decision(1)]));
        Assert.Equal(MapFoliageChangeKind.Changed, Assert.Single(MapFoliageDifferentialCore.JoinBlock([old], [Decision(1, "different-floor")])).Kind);
        Assert.Equal(MapFoliageChangeKind.Added, Assert.Single(MapFoliageDifferentialCore.JoinBlock([], [old])).Kind);
        Assert.Equal(MapFoliageChangeKind.Removed, Assert.Single(MapFoliageDifferentialCore.JoinBlock([old], [])).Kind);
        var rejected = old with { Sample = null, Reason = MapFoliageReason.MaterialNotAllowed };
        Assert.Empty(MapFoliageDifferentialCore.JoinBlock([rejected], [rejected with { }]));
        Assert.Equal(MapFoliageChangeKind.Changed, Assert.Single(MapFoliageDifferentialCore.JoinBlock([rejected], [rejected with { Reason = MapFoliageReason.WaterMaterial }])).Kind);
        Assert.Throws<ArgumentException>(() => MapFoliageDifferentialCore.JoinBlock(Enumerable.Repeat(old, 4097).ToArray(), []));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("spacing")]
    [InlineData("origin")]
    [InlineData("uniform-origin")]
    public void Compare_SeedSpacingAndOriginCrossingsHaveOneChangedRow(string edit)
    {
        bool origin = edit.Contains("origin", StringComparison.Ordinal);
        bool varied = edit == "origin";
        FoliageFixture f = FoliageComparisonFixtures.Flat(seed: origin ? 1 : 0, varied: varied);
        FoliageFixture g = FoliageComparisonFixtures.Copy(f, l =>
        {
            if (edit == "seed") l["seed"] = 1;
            else if (edit == "spacing") l["spacing"] = 0.51f;
            else l["originX"] = 1;
        });
        var before = FoliageFixtures.Context(f);
        var after = FoliageFixtures.Context(g);
        MapFoliageSample a = Target(before), b = Target(after);
        Assert.Equal(origin ? 64.07940673828125f : 63.8536376953125f, a.Position.X);
        if (edit == "seed") Assert.Equal(64.07940673828125f, b.Position.X);
        if (edit == "spacing") Assert.Equal(65.130706787109375f, b.Position.X);
        Assert.NotEqual(OwnerChunk(before, a), OwnerChunk(after, b));
        var changes = new List<MapFoliageChange>();
        MapFoliageDifferentialResult result = MapFoliageDifferential.Compare(before, after, "cover-a", observe: changes.Add);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var targetChanges = changes.Where(c => c.Address == new MapFoliageComparisonAddress("cover-a", 128, 0)).ToArray();
        if (edit == "uniform-origin") Assert.Empty(targetChanges);
        else
        {
            Assert.Single(targetChanges);
            Assert.Equal(MapFoliageChangeKind.Changed, targetChanges[0].Kind);
            Assert.DoesNotContain(targetChanges, c => c.Kind is MapFoliageChangeKind.Added or MapFoliageChangeKind.Removed);
        }
        Assert.Equal(before.SnapshotId, result.BeforeIdentity);
        Assert.Equal(after.SnapshotId, result.ProposalDigest);
        Assert.Equal(64, result.Digest.Length);
        Assert.Equal(changes.LongCount(c => c.Kind == MapFoliageChangeKind.Changed), result.Changed);
        Assert.Equal(changes.OrderBy(c => MapFoliageComparisonBlock.For(c.Address.X, c.Address.Z).Z)
            .ThenBy(c => MapFoliageComparisonBlock.For(c.Address.X, c.Address.Z).X).ThenBy(c => c.Address.Z).ThenBy(c => c.Address.X), changes);
    }

    [Fact]
    public void Compare_ZeroDensityStillRetainsDiagnosticMaterialChanges()
    {
        FoliageFixture f = FoliageComparisonFixtures.Flat();
        var layer = FoliageComparisonFixtures.Layer(f);
        FoliageComparisonFixtures.Set(f, FoliageComparisonFixtures.Change(layer, l =>
        {
            foreach (var chunk in l["chunks"]!.AsArray()) chunk!["density"] = Convert.ToBase64String(new byte[1024]);
        }));
        FoliageFixture g = FoliageFixtures.Reload(f, tiled: false);
        foreach (var patch in g.Document.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 11 };
        for (int i = 0; i < g.Document.Surfaces.Refs.Count; i++)
        {
            var surface = g.Document.Surfaces.Refs[i];
            g.Document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = KhaozEngine.MapDoc.Surfaces.MapSurfaceSemantics.SurfaceDigest(surface,
                    g.Document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p =>
                        new KeyValuePair<KhaozEngine.MapDoc.Surfaces.MapPatchKey, string>(p.Key, KhaozEngine.MapDoc.Surfaces.MapSurfaceSemantics.PatchDigest(p.Value)))),
            };
        }
        var changes = new List<MapFoliageChange>();
        var result = MapFoliageDifferential.Compare(f.Document, g.Document, f.Assets, "cover-a", observe: changes.Add);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var target = Assert.Single(changes, c => c.Address == new MapFoliageComparisonAddress("cover-a", 128, 0));
        Assert.Equal(MapFoliageChangeKind.Changed, target.Kind);
        Assert.Null(target.Before!.Sample);
        Assert.Null(target.After!.Sample);
    }

    [Fact]
    public void ComparisonBlocks_UseSignedFloorAndCompareBothProposalsOutsideGenerationOwnership()
    {
        Assert.Equal(new MapFoliageComparisonBlock(-1, -2), MapFoliageComparisonBlock.For(-1, -65));
        FoliageFixture f = FoliageComparisonFixtures.Flat();
        var context = FoliageFixtures.Context(f);
        Assert.Empty(MapFoliageDifferential.CompareBlock(context, context, "cover-a", new(2, 0), new()));
        var observed = new List<MapFoliageChange>();
        MapFoliageDifferentialResult same = MapFoliageDifferential.Compare(f.Document, f.Document, f.Assets, "cover-a", observe: observed.Add);
        Assert.Equal(MapFoliageStatus.Complete, same.Status);
        Assert.Empty(observed);
        Assert.Equal((0L, 0L, 0L), (same.Added, same.Removed, same.Changed));
        FoliageFixture invalid = FoliageComparisonFixtures.Copy(f, l => l["policy"]!["version"] = 2);
        var refused = MapFoliageDifferential.Compare(f.Document, invalid.Document, f.Assets, "cover-a");
        Assert.Equal(MapFoliageStatus.Invalid, refused.Status);
        Assert.Empty(refused.Digest);
    }

    [Fact]
    public void Comparison_AdmitsUnionWithoutScanningTheGap()
    {
        FoliageFixture f = FoliageComparisonFixtures.TwoSamplesPerChunk();
        FoliageFixture far = FoliageComparisonFixtures.Copy(f, l => l["originX"] = 1_000_000);
        var result = MapFoliageDifferential.Compare(f.Document, far.Document, f.Assets, "cover-a", new(MaxDifferentialCandidates: 200));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.InRange(result.Work.Candidates, 1, 200);
    }

    internal static MapFoliageDecision Decision(int version, string surface = "invented-floor")
    {
        var evaluator = GroundCoverDistribution.Prepare(new() { Models = [new("invented-model", 1)] });
        GroundCoverDecision distribution = evaluator.Evaluate(-1, 64, _ => new(new GroundCoverSample(1, Vector3.UnitY, 1)));
        var instance = distribution.Instance!.Value;
        Matrix4x4.Decompose(instance.Transform, out _, out Quaternion rotation, out _);
        var sample = new MapFoliageSample(new(new("invented", surface, -1, 64), version, new("invented-policy", version, 0)),
            null, null, "invented-model", new[] { "one", "two" }, 10, instance.Position, Quaternion.Normalize(rotation),
            distribution.Scale, instance.Transform, instance.ThinningRank);
        return new(sample.Key.Address, null, MapFoliageReason.Accepted, distribution, sample);
    }

    static MapFoliageSample Target(MapFoliageGenerationContext c) => new[] { 0, 1 }.SelectMany(x =>
        MapFoliageDistribution.Generate(c, "cover-a", new(x, 0)).Samples).Single(s => s.Key.Address.X == 128 && s.Key.Address.Z == 0);

    static int OwnerChunk(MapFoliageGenerationContext c, MapFoliageSample sample) =>
        (int)MathF.Floor((sample.Position.X - c.Foliage!.Layers[0].OriginX) / 64);
}
