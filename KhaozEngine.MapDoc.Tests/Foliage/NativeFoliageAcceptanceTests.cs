using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;
using static KhaozEngine.Tests.MapDoc.Foliage.FoliageFixtures;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Public synthetic acceptance on the released stacked prefab-cave and water-cave topologies: owned domains,
/// exclusions that reach only their intended layer, no surface transfer into caves, dependent-only invalidation of a
/// moved cave owner, independent heads in both storage forms, the read-reach third term and surfaces whose expectations
/// come from independent planes.</summary>
public sealed class NativeFoliageAcceptanceTests
{
    static readonly MapFoliageChunkKey Home = new(0, 0);

    [Fact]
    public void StackedLayerFoliageAndExclusions_RemainInOwnedDomain()
    {
        FoliageFixture f = Cave();
        IReadOnlyList<FoliageFixture> independentHeads = Heads(f);
        Assert.Equal(4, independentHeads.Count);
        string expected = Canonical(Batches(f));
        Assert.All(independentHeads, head => Assert.Equal(expected, Canonical(Batches(head))));
        Assert.Equal(4, independentHeads.Select(h => h.Document).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(4, independentHeads.Select(h => h.Assets).Distinct(ReferenceEqualityComparer.Instance).Count());

        Dictionary<string, MapFoliageSampleBatch> batches = Batches(f).ToDictionary(b => b.LayerId);
        Assert.All(batches.Values, b => Assert.Equal(MapFoliageStatus.Complete, b.Status));
        IReadOnlyList<MapFoliageSample> surfaceSamples = batches[SurfaceLayer].Samples, caveSamples = batches[CaveLayer].Samples;
        const string caveOwner = DeepFloor;
        Assert.All(caveSamples, s => Assert.Equal(caveOwner, s.Key.Address.SupportOwnerId));
        Assert.DoesNotContain(surfaceSamples, s => s.Key.Address.SupportOwnerId == caveOwner);
        Assert.All(surfaceSamples, s => Assert.Equal(SurfaceFloor, s.Key.Address.SupportOwnerId));
        Assert.All(batches[BridgeLayer].Samples, s => Assert.Equal(BridgeFloor, s.Key.Address.SupportOwnerId));
        Assert.All(batches[RoomLayer].Samples, s =>
            Assert.Equal((BridgeFloor, "upper-room"), (s.Key.Address.SupportOwnerId, s.OccupiedSpaceId)));
        Assert.All(caveSamples, s => Assert.Equal("deep-cave", s.OccupiedSpaceId));
        // All four domains place samples over the shared XZ of the caves, X [0, 4] and Z [-4, 0], each on its own support
        // height: the surface at 0 m, the bridge and the room at 3 m and the deep floor at -60 + 0.25 x.
        foreach (MapFoliageSampleBatch batch in batches.Values) Assert.Contains(batch.Samples, OverCaves);
        Assert.All(surfaceSamples, s => Assert.InRange(Support(s, CaveRoot), -1e-5f, 1e-5f));
        Assert.All(batches[BridgeLayer].Samples.Concat(batches[RoomLayer].Samples), s =>
            Assert.InRange(Support(s, CaveRoot) - 3, -1e-5f, 1e-5f));
        Assert.All(caveSamples, s =>
            Assert.InRange(Support(s, CaveRoot) - (-60 + 0.25f * Candidate(s, CaveRoot).X), -1e-4f, 1e-4f));

        // Each exclusion rejects its own reason in its intended layer only. Without it, every other layer's samples are
        // unchanged and the intended layer never reports that reason.
        foreach (var (without, layer, reason) in new (string, string, MapFoliageReason)[]
        {
            ("solid", SurfaceLayer, MapFoliageReason.Solid),
            ("roof", SurfaceLayer, MapFoliageReason.RoofAbove),
            ("room", BridgeLayer, MapFoliageReason.Indoor),
            ("door", BridgeLayer, MapFoliageReason.Door),
            ("mask", CaveLayer, MapFoliageReason.Solid),
        })
        {
            Dictionary<string, Generated> with = Generate(Cave()), control = Generate(Cave(without));
            Assert.Contains(reason, with[layer].Reasons);
            Assert.DoesNotContain(reason, control[layer].Reasons);
            foreach (string other in with.Keys.Where(k => k != layer))
                Assert.True(with[other].Samples == control[other].Samples, $"{without} changed {other}");
            Assert.NotEqual(with[layer].Samples, control[layer].Samples);
        }
        Dictionary<string, Generated> all = Generate(f);
        Assert.Equal([MapFoliageReason.Indoor, MapFoliageReason.Door], all[BridgeLayer].Reasons.Intersect(Exclusions).Order());
        Assert.Equal([MapFoliageReason.Solid, MapFoliageReason.RoofAbove], all[SurfaceLayer].Reasons.Intersect(Exclusions).Order());
        Assert.Empty(all[RoomLayer].Reasons.Intersect(Exclusions));
        Assert.Equal([MapFoliageReason.Solid], all[CaveLayer].Reasons.Intersect(Exclusions));
    }

    [Fact]
    public void SurfaceDensity_DoesNotPopulateDeepCave()
    {
        FoliageFixture f = PrefabCaves();
        int[] full = Enumerable.Repeat(255, 32 * 32).ToArray(), none = new int[32 * 32];
        MapDocument dense = MapFoliageEdits.Prepare(f.Document, f.Assets, new MapSetFoliageDensity(ShoreLayer, 0, 0, 32, 32, full)).After;
        foreach (string cave in new[] { DryLayer, FloodLayer })
            dense = MapFoliageEdits.Prepare(dense, f.Assets, new MapSetFoliageDensity(cave, 0, 0, 32, 32, none)).After;
        Dictionary<string, Generated> surfaceOnly = Generate(new(dense, f.Assets));
        Assert.All(surfaceOnly[ShoreLayer].Batch.Samples, s => Assert.Equal("exterior", s.Key.Address.SupportOwnerId));
        Assert.Contains(surfaceOnly[ShoreLayer].Batch.Samples, OverCaves);
        Assert.All(surfaceOnly[ShoreLayer].Batch.Samples, s => Assert.InRange(Support(s, CaveRoot) + 2, -1e-5f, 1e-5f));
        foreach (string cave in new[] { DryLayer, FloodLayer })
        {
            Assert.Equal(MapFoliageStatus.Complete, surfaceOnly[cave].Batch.Status);
            Assert.Empty(surfaceOnly[cave].Batch.Samples);
            Assert.DoesNotContain(MapFoliageReason.Accepted, surfaceOnly[cave].Reasons);
        }

        // The world ocean covers the exterior and the dry cave, and the lake fills the flooded cave. Only visible water
        // material rejects: the exterior's water row and the flooded floor's one water cell. The dry cave under the ocean
        // and the submerged flooded floor still place samples, with no global or contained-medium rule.
        Assert.Contains(f.Document.Water!.Bodies, b => b.Id == "ocean");
        Assert.Contains(f.Assets.GetPrefab("hut").Children, c => c is MapPrefabWaterChild { Value.Id: "lake" });
        Dictionary<string, Generated> authored = Generate(f);
        Assert.All(authored[ShoreLayer].Decisions.Where(d => d.Reason == MapFoliageReason.WaterMaterial),
            d => Assert.InRange(d.Distribution.Candidate.WorldZ, -1f, 0f));
        Assert.Contains(MapFoliageReason.WaterMaterial, authored[ShoreLayer].Reasons);
        Assert.Contains(authored[DryLayer].Batch.Samples, s => s.Position.Z > -1 && s.Position.Z < 0);
        Assert.All(authored[DryLayer].Batch.Samples, s =>
            Assert.Equal(("a/dry-floor", "a/dry"), (s.Key.Address.SupportOwnerId, s.OccupiedSpaceId)));
        Assert.DoesNotContain(MapFoliageReason.WaterMaterial, authored[DryLayer].Reasons);
        Assert.NotEmpty(authored[FloodLayer].Batch.Samples);
        Assert.All(authored[FloodLayer].Batch.Samples, s =>
            Assert.Equal(("a/flooded-floor", "a/flooded"), (s.Key.Address.SupportOwnerId, s.OccupiedSpaceId)));
        Assert.All(authored[FloodLayer].Decisions.Where(d => d.Reason == MapFoliageReason.WaterMaterial), d =>
        {
            Assert.InRange(d.Distribution.Candidate.WorldX, 3f, 4f);
            Assert.InRange(d.Distribution.Candidate.WorldZ, -4f, -3f);
        });
        Assert.Contains(MapFoliageReason.WaterMaterial, authored[FloodLayer].Reasons);

        // Removing the exterior cells over the caves leaves the Surface-bound shore without samples there: a known hole,
        // never a borrowed cave floor. The exterior footprint still claims those cells, so every read that crosses the
        // column refuses with a typed status: a Space-bound shore and both caves below it. The other cave keeps its batch.
        int[] column = Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * MapPatchKey.SlotCells + x)).ToArray();
        var hole = new MapSetPresence(new("exterior", 0, 0), column, false);
        MapPrefabEditResult removed = MapDocumentTerrainEdits.Prepare(f.Document, f.Assets, hole);
        Assert.Equal([DryLayer, FloodLayer, ShoreLayer], removed.Effects.FoliageChunks.Select(r => r.LayerId).Order(StringComparer.Ordinal));
        Dictionary<string, MapFoliageSampleBatch> holed = Statuses(new FoliageFixture(removed.After, f.Assets));
        Assert.Equal(MapFoliageStatus.Complete, holed[ShoreLayer].Status);
        Assert.DoesNotContain(holed[ShoreLayer].Samples, OverCaves);
        Assert.All(holed[ShoreLayer].Samples, s => Assert.Equal("exterior", s.Key.Address.SupportOwnerId));
        AssertRefused(holed, DryLayer, FloodLayer);
        Assert.Equal(authored[OtherCaveLayer].Samples, SampleText(holed[OtherCaveLayer].Samples));
        MapDocument spaceBound = MapDocumentFile.LoadText(MapDocumentFile.SaveText(removed.After));
        MapFoliageLayer shore = spaceBound.Foliage!.Layers.Single(l => l.Id == ShoreLayer);
        spaceBound.Foliage = new(1, spaceBound.Foliage.Layers.Select(l => l.Id == ShoreLayer
            ? ClearanceLayer(shore, support: new(MapSupportBindingKind.Space, null, "exterior", -1.8f, 1, 0.5f)) : l).ToArray());
        AssertRefused(Statuses(new FoliageFixture(spaceBound, f.Assets)), ShoreLayer);

        // Unloading the exterior's stored payload refuses the shore and both caves whose scopes read it, with a typed
        // status and no sample, and transfers nothing to any other layer.
        string root = SaveTiled(f);
        try
        {
            SurfaceStorageFixtures.DeletePayload(root, new("exterior", 0, 0));
            MapEffectiveResources resources = MapPrefabResolver.Expand(f.Document, f.Assets, new(), null,
                MapStoredSurfaceSource.Open(root));
            MapFoliageContextResult unloaded = MapFoliageGenerationContext.CaptureEffective(f.Document, f.Assets, resources);
            Assert.Equal(MapFoliageStatus.Complete, unloaded.Status);
            Dictionary<string, MapFoliageSampleBatch> partial = Statuses(unloaded.Context!);
            AssertRefused(partial, ShoreLayer, DryLayer, FloodLayer);
            Assert.Equal(authored[OtherCaveLayer].Samples, SampleText(partial[OtherCaveLayer].Samples));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CaveOwnerTransform_InvalidatesOnlyDependentSamples()
    {
        FoliageFixture f = PrefabCaves();
        IReadOnlyList<FoliageFixture> independentHeads = Heads(f);
        Assert.Equal(4, independentHeads.Count);
        string original = Canonical(Batches(f));
        FoliageFixture head = independentHeads[0];
        Assert.Equal(original, Canonical(Batches(head)));
        const string caveLayer = DryLayer, otherCaveLayer = OtherCaveLayer;
        MapFoliageSampleBatch unrelatedBefore = MapFoliageDistribution.Generate(Context(head), otherCaveLayer, Home);
        string unrelatedBatchBefore = Canonical([unrelatedBefore]);
        Assert.NotEmpty(unrelatedBefore.Samples);
        IReadOnlyList<MapFoliageSample> caveBefore = MapFoliageDistribution.Generate(Context(head), caveLayer, Home).Samples;
        AssertOnPosedFloor(caveBefore, new(Vector3.Zero, 0, 1), -60);

        // Move, rotate and scale the original cave owner on one head.
        var pose = new MapTransform(new(2.5f, -1, 1.25f), 0.371f, 1.2f);
        MapPrefabEditResult moved = MapPrefabEdits.Prepare(head.Document, head.Assets, new MapMovePrefab("a", pose));
        MapNativeEditEffects effects = moved.Effects;
        Assert.Contains(effects.FoliageChunks, c => c.LayerId == caveLayer);
        Assert.DoesNotContain(effects.FoliageChunks, c => c.LayerId == otherCaveLayer);
        Assert.Empty(effects.FoliageLayers);
        MapFoliageChunkChange row = effects.FoliageChunks.Single(c => c.LayerId == caveLayer);
        // The cosmetic bounds are the chunk rectangle clipped to the raster domain on both sides, and the digests differ.
        Assert.Equal((Home, new MapFoliageRect(-12, -16, 19, 15)), (row.Chunk, row.OldBounds!.Value));
        Assert.Equal(row.OldBounds, row.NewBounds);
        Assert.NotEqual(row.BeforeDigest, row.AfterDigest);
        FoliageFixture after = new(moved.After, head.Assets);
        MapFoliageSampleBatch movedCave = MapFoliageDistribution.Generate(Context(after), caveLayer, Home);
        Assert.Equal(row.AfterDigest, movedCave.DependencyDigest);
        AssertOnPosedFloor(movedCave.Samples, pose, -60);
        Assert.NotEqual(SampleText(caveBefore), SampleText(movedCave.Samples));
        string unrelatedBatchAfter = Canonical([MapFoliageDistribution.Generate(Context(after), otherCaveLayer, Home)]);
        Assert.Equal(unrelatedBatchBefore, unrelatedBatchAfter);
        // The edit is detached, and the other three heads keep their own state.
        Assert.Equal(original, Canonical(Batches(head)));
        Assert.All(independentHeads.Skip(1), other => Assert.Equal(original, Canonical(Batches(other))));

        // Override: replacing the moved instance's dry floor with one raised 1 m changes the cave rows and heights only.
        var dryFloor = (MapPrefabSurfaceChild)head.Assets.GetPrefab("hut").Children.Single(c => c.Key.Key == "dry-floor");
        MapPrefabSurfaceChild raised = dryFloor with
        {
            Patches = dryFloor.Patches.Select(p =>
            {
                MapSurfacePatch copy = p.Clone();
                for (int i = 0; i < copy.Heights.Length; i++) copy.Heights[i] += 100;
                return copy;
            }).ToArray(),
        };
        MapPrefabEditResult overridden = MapPrefabEdits.Prepare(moved.After, head.Assets,
            new MapSetPrefabOverride("a", new MapPrefabReplaceOverride(dryFloor.Key, raised)));
        Assert.Contains(overridden.Effects.FoliageChunks, c => c.LayerId == caveLayer);
        Assert.DoesNotContain(overridden.Effects.FoliageChunks, c => c.LayerId == otherCaveLayer);
        FoliageFixture over = new(overridden.After, head.Assets);
        AssertOnPosedFloor(MapFoliageDistribution.Generate(Context(over), caveLayer, Home).Samples, pose, -59);
        Assert.Equal(unrelatedBatchBefore, Canonical([MapFoliageDistribution.Generate(Context(over), otherCaveLayer, Home)]));

        // Remap: renaming the instance's dry floor World ID renames the layer binding and every sample owner, nothing else.
        MapDocument remapped = MapPrefabReferences.RemapWorld(overridden.After, MapPrefabChildKind.Surface, "a/dry-floor",
            "a/hollow-floor");
        Assert.Equal(Surface("a/hollow-floor"), remapped.Foliage!.Layers.Single(l => l.Id == caveLayer).Support);
        FoliageFixture renamed = new(remapped, head.Assets);
        Assert.Equal(Samples(Context(over), caveLayer).Replace("a/dry-floor", "a/hollow-floor", StringComparison.Ordinal),
            Samples(Context(renamed), caveLayer));
        Assert.Equal(unrelatedBatchBefore, Canonical([MapFoliageDistribution.Generate(Context(renamed), otherCaveLayer, Home)]));

        // Unpack: the cave becomes world authoring with the same World IDs and the same samples.
        MapPrefabEditResult unpacked = MapPrefabEdits.Prepare(remapped, head.Assets, new MapUnpackPrefab("a"));
        Assert.True(unpacked.After.Prefabs!.Instances.Single(i => i.Id == "a").Unpacked);
        FoliageFixture flat = new(unpacked.After, head.Assets);
        Assert.Equal(Samples(Context(renamed), caveLayer), Samples(Context(flat), caveLayer));
        Assert.Equal(unrelatedBatchBefore, Canonical([MapFoliageDistribution.Generate(Context(flat), otherCaveLayer, Home)]));

        // Save and reload in both storage forms keeps every batch of the moved document.
        string movedBatches = Canonical(Batches(after));
        foreach (bool tiled in new[] { false, true }) Assert.Equal(movedBatches, Canonical(Batches(Reload(after, tiled))));

        // Remove: the owner alone refuses while layers bind its floors, and one sequence removes both atomically.
        string state = MapDocumentFile.SaveText(after.Document);
        Assert.Contains("a/dry-floor", Assert.Throws<MapDocumentException>(() =>
            MapPrefabEdits.Prepare(after.Document, after.Assets, new MapRemovePrefab("a"))).Message, StringComparison.Ordinal);
        Assert.Equal(state, MapDocumentFile.SaveText(after.Document));
        MapPrefabEditResult removed = MapFoliageEdits.Prepare(after.Document, after.Assets, new MapFoliageEditSequence(
            [new MapRemoveFoliage(caveLayer), new MapRemoveFoliage(FloodLayer)], new MapRemovePrefab("a")));
        Assert.Equal([caveLayer, FloodLayer], removed.Effects.FoliageLayers.Select(l => l.LayerId).Order(StringComparer.Ordinal));
        Assert.All(removed.Effects.FoliageLayers, l => Assert.Null(l.AfterDigest));
        Assert.DoesNotContain(removed.Effects.FoliageChunks, c => c.LayerId is caveLayer or otherCaveLayer);
        Assert.Equal(unrelatedBatchBefore,
            Canonical([MapFoliageDistribution.Generate(Context(new(removed.After, after.Assets)), otherCaveLayer, Home)]));
    }

    [Fact]
    public void ReadReach_ThirdTermCompilesTheNeighboursOwnCellCentre()
    {
        FoliageFixture f = ReachLattices();
        MapFoliageGenerationContext context = Context(f);
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        MapPreparedFoliageSupport support = MapFoliageSupport.Prepare(context, layer, Home, new());
        Assert.Equal(MapFoliageStatus.Complete, support.Status);
        // The east neighbour centre 38 selects the 2 m floor, whose own cell centre 39 holds the post's mask.
        MapFoliageSurfacePoint neighbour = support.Eligibility(38, -6);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted, "mid"),
            (neighbour.Status, neighbour.Reason, neighbour.SupportOwnerId));
        MapPreparedFoliageClearance clearance = MapFoliageClearance.Prepare(context, layer, Home, new());
        Assert.Equal(MapFoliageStatus.Complete, clearance.Status);
        MapFoliageSurfacePoint point = clearance.Evaluate(support.Sample(32.25f, -6), 32.25f, -6);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted, "coarse"), (point.Status, point.Reason, point.SupportOwnerId));
        // The masked east neighbour fades the sample by its distance 3.75 m over the 4 m edge fade.
        Assert.Equal(0.9375f, point.Density, 0.0001f);
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageDistribution.Generate(context, layer.Id, Home).Status);

        // Without the mask the neighbour stays eligible and the sample keeps full density.
        f.Document.Foliage = new(1, [ClearanceLayer(layer, masks: [])]);
        MapFoliageGenerationContext open = Context(f);
        MapFoliageLayer bare = open.Foliage!.Layers[0];
        Assert.Equal(1f, MapFoliageClearance.Prepare(open, bare, Home, new())
            .Evaluate(MapFoliageSupport.Prepare(open, bare, Home, new()).Sample(32.25f, -6), 32.25f, -6).Density);
    }

    [Fact]
    public void PositiveZSubdividedAndTransformedSurfaces_MatchIndependentPlanes()
    {
        FoliageFixture f = Planes();
        Dictionary<string, MapFoliageSampleBatch> batches = Batches(f).ToDictionary(b => b.LayerId);
        IReadOnlyList<MapFoliageSample> plane = batches[PlaneLayer].Samples, deck = batches[DeckLayer].Samples;
        Assert.NotEmpty(plane);
        Assert.NotEmpty(deck);
        const float RootOffset = -0.015f;

        // PositiveZ: world y = 0.07 x - 0.03 z, so the upward normal leans to -X and +Z. A NegativeZ reading would flip
        // Z. Every sample, subdivided boundary cells included, sits on the plane once its root offset is taken back
        // along its own normal. Away from the boundary, where central differences read only authored corners, the
        // smoothed normal is the plane's.
        Vector3 planeNormal = Vector3.Normalize(new(-0.07f, 1, 0.03f));
        foreach (MapFoliageSample s in plane)
        {
            Assert.Equal(PlaneFloor, s.Key.Address.SupportOwnerId);
            Vector3 candidate = Candidate(s, RootOffset);
            Assert.InRange(Support(s, RootOffset) - (0.07f * candidate.X - 0.03f * candidate.Z), -1e-5f, 1e-5f);
            if (candidate is { X: > -19 and < -13, Z: > -5 and < 1 }) AssertNear(planeNormal, Normal(s), 1e-5f);
        }
        Assert.Contains(plane, s => Candidate(s, RootOffset) is { X: > -19 and < -13, Z: > -5 and < 1 });
        Assert.Contains(plane, s => Candidate(s, RootOffset) is { Z: < -5 or > 1 } or { X: < -19 or > -13 });

        // Transformed: local y = 0.1 lx - 0.04 lz, posed at (-31, 2.25, -25) with yaw 0.371 and scale 1.25. The whole deck
        // is boundary, so its heights carry the expectation.
        var pose = new MapTransform(new(-31, 2.25f, -25), 0.371f, 1.25f);
        double c = Math.Cos(pose.YawRadians), sn = Math.Sin(pose.YawRadians);
        foreach (MapFoliageSample s in deck)
        {
            Assert.Equal("p/deck", s.Key.Address.SupportOwnerId);
            Vector3 candidate = Candidate(s, RootOffset);
            double dx = candidate.X - pose.Position.X, dz = candidate.Z - pose.Position.Z;
            double lx = (c * dx - sn * dz) / pose.Scale, lz = (sn * dx + c * dz) / pose.Scale;
            Assert.InRange(lx, -1e-4, 3 + 1e-4);
            Assert.InRange(lz, -2 - 1e-4, 1e-4);
            Assert.InRange(Support(s, RootOffset) - (pose.Position.Y + pose.Scale * (0.1 * lx - 0.04 * lz)), -1e-4, 1e-4);
        }
    }

    static readonly MapFoliageReason[] Exclusions =
        [MapFoliageReason.Solid, MapFoliageReason.RoofAbove, MapFoliageReason.Door, MapFoliageReason.Indoor];

    sealed record Generated(MapFoliageSampleBatch Batch, IReadOnlyList<MapFoliageDecision> Decisions, string Samples)
    {
        internal IReadOnlyCollection<MapFoliageReason> Reasons => Decisions.Select(d => d.Reason).Distinct().Order().ToArray();
    }

    // Chunk (0, 0) of every layer, with its decision rows and its samples in canonical form, from one fresh context.
    static Dictionary<string, Generated> Generate(FoliageFixture fixture)
    {
        MapFoliageGenerationContext context = Context(fixture);
        var result = new Dictionary<string, Generated>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in context.Foliage!.Layers)
        {
            var rows = new List<MapFoliageDecision>();
            MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(context, layer.Id, Home, null, rows.Add);
            Assert.Equal(MapFoliageStatus.Complete, batch.Status);
            result.Add(layer.Id, new(batch, rows, SampleText(batch.Samples)));
        }
        return result;
    }

    static Dictionary<string, MapFoliageSampleBatch> Statuses(FoliageFixture fixture) => Statuses(Context(fixture));

    static Dictionary<string, MapFoliageSampleBatch> Statuses(MapFoliageGenerationContext context) =>
        context.Foliage!.Layers.ToDictionary(l => l.Id, l => MapFoliageDistribution.Generate(context, l.Id, Home), StringComparer.Ordinal);

    static void AssertRefused(Dictionary<string, MapFoliageSampleBatch> batches, params string[] layers)
    {
        foreach (string layer in layers)
        {
            Assert.Equal((layer, MapFoliageStatus.Unavailable), (layer, batches[layer].Status));
            Assert.Empty(batches[layer].Samples);
        }
    }

    static string SampleText(IEnumerable<MapFoliageSample> samples) => string.Join("\n", samples.Select(FoliageFixtures.Canonical));

    static string Samples(MapFoliageGenerationContext context, string layer) =>
        SampleText(MapFoliageDistribution.Generate(context, layer, Home).Samples);

    // Over the shared XZ of the caves, X [0, 4] and Z [-4, 0].
    static bool OverCaves(MapFoliageSample s) => s.Position is { X: > 0 and < 4, Z: > -4 and < 0 };

    // Every sample lies on the dry floor, flat at localY metres in the instance frame, posed by pose.
    static void AssertOnPosedFloor(IReadOnlyList<MapFoliageSample> samples, MapTransform pose, float localY)
    {
        Assert.NotEmpty(samples);
        double c = Math.Cos(pose.YawRadians), s = Math.Sin(pose.YawRadians);
        foreach (MapFoliageSample sample in samples)
        {
            Vector3 candidate = Candidate(sample, CaveRoot);
            double dx = candidate.X - pose.Position.X, dz = candidate.Z - pose.Position.Z;
            double lx = (c * dx - s * dz) / pose.Scale, lz = (s * dx + c * dz) / pose.Scale;
            Assert.InRange(lx, -1e-4, 4 + 1e-4);
            Assert.InRange(lz, -4 - 1e-4, 1e-4);
            Assert.InRange(Support(sample, CaveRoot) - (pose.Position.Y + pose.Scale * localY), -1e-4, 1e-4);
        }
    }

    const float CaveRoot = -0.02f;

    // A sample stands at its candidate's support point plus the root offset along its own unit normal.
    static Vector3 Candidate(MapFoliageSample s, float rootOffset) => s.Position - Normal(s) * rootOffset;

    static float Support(MapFoliageSample s, float rootOffset) => Candidate(s, rootOffset).Y;

    static Vector3 Normal(MapFoliageSample s) => new Vector3(s.Transform.M21, s.Transform.M22, s.Transform.M23) / s.Scale;

    static void AssertNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(actual.X - expected.X, -tolerance, tolerance);
        Assert.InRange(actual.Y - expected.Y, -tolerance, tolerance);
        Assert.InRange(actual.Z - expected.Z, -tolerance, tolerance);
    }
}
