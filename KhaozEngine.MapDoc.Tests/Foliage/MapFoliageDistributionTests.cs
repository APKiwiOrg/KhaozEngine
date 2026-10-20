using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageDistributionTests
{
    [Fact]
    public void SampleBatch_IsIndependentOfChunkOrderReloadAndLayerName()
    {
        FoliageFixture f = FoliageFixtures.Create();
        var context = FoliageFixtures.Context(f);
        MapFoliageSampleBatch a = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        MapFoliageSampleBatch b = MapFoliageDistribution.Generate(context, "cover-a", new(1, 0));
        Assert.Equal(MapFoliageStatus.Complete, a.Status);
        Assert.NotEmpty(a.Samples);
        var wholePoses = a.Samples.Concat(b.Samples).Select(Pose).Order().ToArray();
        var loaded = FoliageFixtures.Context(FoliageFixtures.Reload(f, tiled: true));
        var reorderedAndReloadedPoses = new[] { 1, 0 }.SelectMany(x => MapFoliageDistribution.Generate(loaded, "cover-a", new(x, 0)).Samples)
            .Select(Pose).Order().ToArray();
        Assert.Equal(wholePoses, reorderedAndReloadedPoses);
        FoliageFixture renamed = FoliageComparisonFixtures.Copy(f, l => l["id"] = "cover-renamed");
        MapFoliageSampleBatch renamedBatch = MapFoliageDistribution.Generate(FoliageFixtures.Context(renamed), "cover-renamed", new(0, 0));
        Assert.Equal(a.Samples.Select(Pose), renamedBatch.Samples.Select(Pose));
        Assert.NotEqual(a.Samples.Select(s => s.Key).ToArray(), renamedBatch.Samples.Select(s => s.Key).ToArray());
        Assert.All(a.Samples, s => Assert.Equal("floor", s.Key.Address.SupportOwnerId));
        Assert.Throws<NotSupportedException>(() => ((IList<MapFoliageSample>)a.Samples).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)a.Samples[0].MaterialResourceIds).Clear());
    }

    [Fact]
    public void ImmutableDtos_UseSpecifiedNamedArgumentsAndDetachMutableInputs()
    {
        var materials = new[] { "invented-material" };
        var sample = new MapFoliageSample(Key: new(new("invented", "floor", 0, 0), 1, new("legacy-tile", 1, 0)),
            OccupiedSpaceId: null, Face: null, AssetId: "invented-model", MaterialResourceIds: materials,
            VisibleMaterial: 10, Position: Vector3.Zero, Orientation: Quaternion.Identity, Scale: 1,
            Transform: Matrix4x4.Identity, ThinningRank: 0.25f);
        var samples = new List<MapFoliageSample> { sample };
        var batch = new MapFoliageSampleBatch(LayerId: "invented", Chunk: new(0, 0), Status: MapFoliageStatus.Complete,
            DependencyDigest: "invented-digest", Samples: samples, Work: new(1, 1, 1, 2, 0, 0, 0, 1024));
        var batches = new List<MapFoliageSampleBatch> { batch };
        var query = new MapFoliageQueryResult(Status: MapFoliageStatus.Complete, Batches: batches, Work: batch.Work);
        materials[0] = "mutated";
        samples.Clear();
        batches.Clear();
        Assert.Equal("invented-material", Assert.Single(Assert.Single(query.Batches).Samples).MaterialResourceIds[0]);
    }

    [Fact]
    public void Samples_KeepCandidateMatrixScaleRankAndSelectedModel()
    {
        FoliageFixture f = FoliageFixtures.Create();
        var rows = new List<MapFoliageDecision>();
        MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(FoliageFixtures.Context(f), "cover-a", new(0, 0), observe: rows.Add);
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        Assert.All(rows.Where(r => r.Sample is not null), r =>
        {
            MapFoliageSample s = r.Sample!;
            GroundCoverInstance instance = r.Distribution.Instance!.Value;
            Assert.Equal(instance.Transform, s.Transform);
            Assert.Equal(instance.Position, s.Position);
            Assert.Equal(BitConverter.SingleToInt32Bits(r.Distribution.Scale), BitConverter.SingleToInt32Bits(s.Scale));
            Assert.Equal(BitConverter.SingleToInt32Bits(instance.ThinningRank), BitConverter.SingleToInt32Bits(s.ThinningRank));
            Assert.Equal(instance.ModelId, s.AssetId);
            Assert.Equal(f.Assets.GetAsset(s.AssetId).MaterialResourceIds, s.MaterialResourceIds);
            Assert.True(Matrix4x4.Decompose(instance.Transform, out _, out Quaternion rotation, out _));
            Assert.Equal(Quaternion.Normalize(rotation), s.Orientation);
        });
        Assert.Contains(batch.Samples, s => s.AssetId == "reed-a");
        Assert.Contains(batch.Samples, s => s.AssetId == "reed-b");
    }

    [Fact]
    public void SpaceBoundSamples_KeepCanonicalSurfaceAndOccupiedSpaceSeparate()
    {
        var fixture = FoliageFixtures.Stacked(new(MapSupportBindingKind.Space, null, "level-1", 10.5f, 1, 1));
        var context = FoliageFixtures.Context(fixture);
        var batch = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        Assert.All(batch.Samples, s =>
        {
            Assert.Equal("floor-1", s.Key.Address.SupportOwnerId);
            Assert.Equal("level-1", s.OccupiedSpaceId);
        });
    }

    [Fact]
    public void DependencyDigest_ChangesOnlyForIntersectingInputs()
    {
        FoliageFixture f = FoliageComparisonFixtures.Grown(2, 8, 1, 8);
        string original = Generate(f).DependencyDigest;
        FoliageFixture unrelated = FoliageComparisonFixtures.Grown(64, 128, 32, 128);
        Assert.Equal(original, Generate(unrelated).DependencyDigest);
        FoliageFixture seed = FoliageComparisonFixtures.Copy(f, l => l["seed"] = 18);
        Assert.NotEqual(original, Generate(seed).DependencyDigest);
        FoliageFixture raster = FoliageComparisonFixtures.Copy(f, l =>
        {
            byte[] bytes = Convert.FromBase64String(l["chunks"]![0]!["density"]!.GetValue<string>());
            bytes[0]++;
            l["chunks"]![0]!["density"] = Convert.ToBase64String(bytes);
        });
        Assert.NotEqual(original, Generate(raster).DependencyDigest);
        FoliageFixture model = FoliageComparisonFixtures.Copy(f, l => l["models"]![0]!["weight"] = 2);
        Assert.NotEqual(original, Generate(model).DependencyDigest);
    }

    [Fact]
    public void DependencyDigest_ChangesForInputsUnderTheChunk()
    {
        FoliageFixture f = FoliageComparisonFixtures.Grown(2, 8, 1, 8);
        string original = Generate(f).DependencyDigest;
        Assert.NotEmpty(original);
        Assert.Equal(original, Changed(f, _ => { }, "reload"));
        // Paint over the chunk's floor.
        Assert.NotEqual(original, Changed(f, g => Paint(g, 10), "paint"));
        Assert.NotEqual(Changed(f, g => Paint(g, 10), "paint"), Changed(f, g => Paint(g, 11), "repaint"));
        // A clearance owner pose: tree-0 moves 1 m within the chunk.
        Assert.NotEqual(original, Changed(f, g => g.Document.Placements.Single(p => p.Id == "tree-0").X = 5, "pose"));
        // A consulted clearance rule's footprint.
        Assert.NotEqual(original, Generate(FoliageComparisonFixtures.Copy(f, l => l["clearanceRules"]![0]!["footprint"]!["maxX"] = 2)).DependencyDigest);
        // A material resource's bytes.
        var (roots, source) = FoliageFixtures.AssetInputs(materialSalt: "-revised");
        FoliageFixture revised = FoliageFixtures.Reload(f, tiled: false);
        revised.Document.NativeAssets = roots.ToList();
        Assert.NotEqual(original, Generate(revised with { Assets = MapAssetClosure.Load(roots, source) }).DependencyDigest);

        static string Changed(FoliageFixture f, Action<FoliageFixture> change, string label)
        {
            FoliageFixture copy = FoliageFixtures.Reload(f, tiled: false);
            change(copy);
            MapFoliageSampleBatch batch = Generate(copy);
            Assert.True(batch.Status == MapFoliageStatus.Complete, label + " " + batch.Status);
            return batch.DependencyDigest;
        }
        static void Paint(FoliageFixture g, ushort underlay)
        {
            var set = new MapSurfaceSet();
            KhaozEngine.Tests.MapDoc.Water.WaterFixtures.Surface(set, "near-paint", MapSurfaceRole.PaintOverride,
                MapLatticeFrame.ImportedMetreCentimetre, 2, 0, 6, 4, (_, _) => 100);
            foreach (MapSurfacePatch p in set.Patches.Values)
                for (int i = 0; i < p.Cells.Length; i++) p.Cells[i] = p.Cells[i] with { Underlay = underlay };
            foreach (var p in set.Patches) g.Document.Surfaces.Patches.Add(p.Key, p.Value);
            MapSurfaceRef surface = set.Refs.Single() with { PaintTargetSurfaceId = "floor" };
            g.Document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, set.Patches.Select(p =>
                    new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            });
        }
    }

    [Fact]
    public void GenerationContext_UnrelatedGrowthDoesNotGrowChunkWork()
    {
        FoliageFixture small = FoliageComparisonFixtures.Grown(2, 8, 1, 8);
        FoliageFixture large = FoliageComparisonFixtures.Grown(64, 128, 32, 128);
        var a = FoliageFixtures.Context(small);
        var b = FoliageFixtures.Context(large);
        var tiled = FoliageFixtures.Context(FoliageFixtures.Reload(large, tiled: true));
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageSampleBatch first = MapFoliageDistribution.Generate(a, "cover-a", new(0, 0));
        MapFoliageSampleBatch second = MapFoliageDistribution.Generate(b, "cover-a", new(0, 0));
        MapFoliageSampleBatch third = MapFoliageDistribution.Generate(tiled, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, third.Status);
        Assert.Equal(first.Work, third.Work);
        Assert.Equal(first.Samples.Select(Pose), third.Samples.Select(Pose));
        Assert.Equal(first.DependencyDigest, third.DependencyDigest);
        Assert.Equal(MapFoliageStatus.Complete, first.Status);
        Assert.Equal(MapFoliageStatus.Complete, second.Status);
        Assert.Equal((0L, 0L), (MapPrefabResolver.ThreadExpansions - expansions, MapFoliageJson.ThreadPayloadWrites - writes));
        Assert.Equal(first.Work, second.Work);
        Assert.Equal(first.Samples.Select(Pose), second.Samples.Select(Pose));
        Assert.Equal(first.DependencyDigest, second.DependencyDigest);
        Assert.Equal(0, first.Work.SurfaceQueries);
    }

    [Fact]
    public void Query_CoversNegativeOffRasterChunksAndClipsItsHalfOpenArea()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var area = new RectArea(-6, -2, 18, 2);
        MapFoliageQueryResult result = MapFoliageDistribution.Query(context, "cover-a", area);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.Equal(new[] { -1, 0, 1 }, result.Batches.Select(b => b.Chunk.X));
        Assert.Empty(result.Batches[0].Samples);
        Assert.All(result.Batches.SelectMany(b => b.Samples), s => Assert.True(s.Position.X >= -6 && s.Position.X < 18));
        MapFoliageQueryResult clipped = MapFoliageDistribution.Query(context, "cover-a", new(1, -1, 6, 1));
        Assert.All(clipped.Batches.SelectMany(b => b.Samples), s => Assert.True(s.Position.X >= 1 && s.Position.X < 6 && s.Position.Z >= -1 && s.Position.Z < 1));
    }

    [Fact]
    public void Generation_AbsentInvalidAndUnavailableInputsRefuseAtomically()
    {
        FoliageFixture f = FoliageFixtures.Create();
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageDistribution.Generate(FoliageFixtures.Context(f), "missing", new(0, 0)).Status);
        f.Document.Foliage = null;
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageDistribution.Generate(FoliageFixtures.Context(f), "cover-a", new(0, 0)).Status);
        var unavailable = FoliageFixtures.UnavailableContext(FoliageFixtures.UnavailableHalo());
        MapFoliageSampleBatch refused = MapFoliageDistribution.Generate(unavailable, "cover-a", new(0, 1));
        Assert.Equal(MapFoliageStatus.Unavailable, refused.Status);
        Assert.Empty(refused.Samples);
        Assert.Empty(refused.DependencyDigest);
        FoliageFixture invalid = FoliageComparisonFixtures.Copy(FoliageFixtures.Create(), l => l["policy"]!["version"] = 2);
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageGenerationContext.Capture(invalid.Document, invalid.Assets).Status);
    }

    [Theory]
    [InlineData("cut-diagonalhalf-r0")]
    [InlineData("nodraw-blocked")]
    public void Generation_PreservesSurfaceEligibility(string variant)
    {
        FoliageFixture f = FoliageFixtures.FlagsAndCuts(variant);
        var context = FoliageFixtures.Context(f);
        var layer = context.Foliage!.Layers[0];
        var support = MapFoliageSupport.Prepare(context, layer, new(0, 0), new());
        var rows = new List<MapFoliageDecision>();
        var batch = MapFoliageDistribution.Generate(context, layer.Id, new(0, 0), observe: rows.Add);
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.All(rows.Where(r => r.Sample is not null), r =>
        {
            var c = r.Distribution.Candidate;
            var point = support.Sample(c.WorldX, c.WorldZ);
            Assert.Equal(MapFoliageReason.Accepted, point.Reason);
            Assert.Equal(point.VisibleMaterial, r.Sample!.VisibleMaterial);
        });
    }

    [Fact]
    public void Preparation_CompilesEachSupportFaceOnceAndResolvesRecordsOnce()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.Flat());
        long scans = MapSpaceGeometry.ThreadRecordScans;
        var support = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new());
        Assert.Equal(MapFoliageStatus.Complete, support.Status);
        long uniqueFaces = support.Grids.SelectMany(g => g.Cells.Values).Select(c => c.Faces).Distinct().Sum(f => (long)f.Count);
        Assert.Equal(uniqueFaces, support.Work.Faces);
        _ = support.Sample(10.25f, 0.25f);
        _ = support.Sample(10.75f, 0.75f);
        Assert.Equal(1, MapSpaceGeometry.ThreadRecordScans - scans);
    }

    [Fact]
    public void Scope_DistantCoarseFloorDoesNotWidenChunkOrMembershipPreparation()
    {
        FoliageFixture small = FoliageFixtures.Create();
        FoliageFixture large = FoliageFixtures.Create();
        var set = new MapSurfaceSet();
        var frame = new MapLatticeFrame(new(1024, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        KhaozEngine.Tests.MapDoc.Water.WaterFixtures.Surface(set, "distant-coarse", MapSurfaceRole.SupportFloor, frame, 1, 1, 2, 2, (_, _) => 100);
        foreach (var p in set.Patches) large.Document.Surfaces.Patches.Add(p.Key, p.Value);
        large.Document.Surfaces.Refs.AddRange(set.Refs.Select(surface => surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, set.Patches.Select(p =>
                new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
        }));
        var a = Generate(small);
        var b = Generate(large);
        Assert.Equal(MapFoliageStatus.Complete, a.Status);
        Assert.Equal(MapFoliageStatus.Complete, b.Status);
        Assert.Equal(a.Work, b.Work);
        Assert.Equal(a.DependencyDigest, b.DependencyDigest);
        Assert.Equal(a.Samples.Select(Pose), b.Samples.Select(Pose));
    }

    [Fact]
    public void SurfaceBoundPreparation_DoesNotCompileUnreferencedCeilings()
    {
        FoliageFixture f = FoliageFixtures.Create();
        var small = FoliageFixtures.Context(f);
        var set = new MapSurfaceSet();
        KhaozEngine.Tests.MapDoc.Water.WaterFixtures.Surface(set, "unused-ceiling", MapSurfaceRole.Ceiling,
            MapLatticeFrame.ImportedMetreCentimetre, -8, -8, 72, 8, (_, _) => 900);
        foreach (var p in set.Patches) f.Document.Surfaces.Patches.Add(p.Key, p.Value);
        f.Document.Surfaces.Refs.AddRange(set.Refs.Select(surface => surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, set.Patches.Select(p =>
                new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
        }));
        var large = FoliageFixtures.Context(f);
        var a = MapFoliageSupport.Prepare(small, small.Foliage!.Layers[0], new(0, 0), new());
        var b = MapFoliageSupport.Prepare(large, large.Foliage!.Layers[0], new(0, 0), new());
        Assert.Equal(MapFoliageStatus.Complete, a.Status);
        Assert.Equal(MapFoliageStatus.Complete, b.Status);
        Assert.Equal(a.Work.Faces, b.Work.Faces);
        Assert.Equal(a.Sample(1, 0), b.Sample(1, 0));
    }

    [Fact]
    public void ChunkContext_AdmitsEveryPatchItsAcquisitionAdmitted()
    {
        // The context's patch admission is the acquisition's own allowance over a subset of the acquired patches. An
        // acquisition filled to exactly its allowance still prepares, and its context validates every acquired patch.
        var context = FoliageFixtures.Context(FoliageFixtures.Stacked(FoliageFixtures.Surface("floor-0")));
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        var chunk = new MapFoliageChunkKey(0, 0);
        MapSurfaceScope scope = MapFoliageSupport.Scope(layer, chunk, context.SupportFloorsFor(layer, chunk), context.Resources.Transforms);
        MapPatchKey[] acquired = context.Acquire(scope).Witness.Present.Select(p => p.Key).ToArray();
        Assert.True(acquired.Length > 1, $"{acquired.Length} patches");
        MapPreparedFoliageSupport Prepare(int patches)
        {
            MapSurfaceScope tight = scope with { Limits = new MapQueryLimits(MaxCandidatePatches: patches) };
            return MapFoliageSupport.Prepare(context.Acquire(tight), context.PaintFor(tight), context.Resources.SurfaceSource.Surfaces,
                layer, chunk, new(), context.DensityDigests(layer));
        }
        MapPreparedFoliageSupport exact = Prepare(acquired.Length);
        Assert.Equal(MapFoliageStatus.Complete, exact.Status);
        MapBoundFaceContext bound = exact.Candidates!.BoundContext;
        Assert.All(acquired, key => Assert.Equal(key, bound.ValidatedPatch(key).Patch.Key));
        MapPreparedFoliageSupport refused = Prepare(acquired.Length - 1);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
        Assert.Equal(0, refused.Work.Faces);
    }

    [Fact]
    public void Preparation_ReusedMembershipPatchesMatchAPhysicalCompile()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.FlagsAndCuts("nodraw"));
        var support = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), new());
        Assert.Equal(MapFoliageStatus.Complete, support.Status);
        MapBoundFaceContext bound = support.Candidates!.BoundContext;
        MapPatchKey[] keys = support.Grids.SelectMany(g => g.Cells.Values).Select(c => c.Key).Distinct().ToArray();
        Assert.NotEmpty(keys);
        foreach (MapPatchKey key in keys)
        {
            MapCompiledPatch reused = bound.LocalCompiled(key);
            MapCompiledPatch physical = MapSurfaceCompiler.Compile(bound.ValidatedPatch(key), bound.Mask(key), MapSurfaceCompiler.MaxFacesPerPatch);
            Assert.NotEmpty(physical.LegacyFallbackCells);
            Assert.Equal(physical.LegacyFallbackCells, reused.LegacyFallbackCells);
            Assert.Equal(physical.Faces.Select(f => (f.Key, physical.ExactTriangle(f))).OrderBy(f => f.Key),
                reused.Faces.Select(f => (f.Key, reused.ExactTriangle(f))).OrderBy(f => f.Key));
            Assert.Equal(physical.Paint.OrderBy(p => p.Face), reused.Paint.OrderBy(p => p.Face));
        }
    }

    [Fact]
    public void Query_SpaceBoundOffRasterChunksCompleteWithoutPreparation()
    {
        // The raster spans world X and Z -1 to 3. Chunks -2 and -1 lie wholly west of it, and chunk -2's acquisition
        // holds no part of the bound space, so preparing it would refuse Unavailable.
        FoliageFixture f = FoliageFixtures.Stacked(new(MapSupportBindingKind.Space, null, "level-1", 10.5f, 1, 1));
        var context = FoliageFixtures.Context(f);
        var rows = new List<MapFoliageDecision>();
        MapFoliageQueryResult result = MapFoliageDistribution.Query(context, "cover-a", new(-70, -1, 3, 3), observe: rows.Add);
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.Equal(new[] { -2, -1, 0 }, result.Batches.Select(b => b.Chunk.X));
        foreach (MapFoliageSampleBatch off in result.Batches.Take(2))
        {
            Assert.Equal(MapFoliageStatus.Complete, off.Status);
            Assert.Empty(off.Samples);
            Assert.Equal((0L, 0L, 0L), (off.Work.PrepareScopes, off.Work.Faces, off.Work.RuleCandidates));
            Assert.NotEmpty(off.DependencyDigest);
            Assert.True(off.Work.Candidates > 0);
        }
        Assert.NotEqual(result.Batches[0].DependencyDigest, result.Batches[1].DependencyDigest);
        Assert.Equal(1, result.Batches[2].Work.PrepareScopes);
        Assert.NotEmpty(result.Batches[2].Samples);
        Assert.All(rows.Where(r => r.Distribution.Candidate.WorldX < -1), r => Assert.Null(r.Sample));
        MapFoliageSampleBatch generated = MapFoliageDistribution.Generate(context, "cover-a", new(-2, 0));
        Assert.Equal((MapFoliageStatus.Complete, result.Batches[0].DependencyDigest), (generated.Status, generated.DependencyDigest));
    }

    [Fact]
    public void OffRasterDigest_FollowsOnlyTheRasterExtent()
    {
        FoliageFixture f = FoliageFixtures.Stacked(new(MapSupportBindingKind.Space, null, "level-1", 10.5f, 1, 1));
        string original = Off(f);
        Assert.NotEmpty(original);
        Assert.Equal(original, Off(FoliageComparisonFixtures.Copy(f, l => l["seed"] = 18)));
        Assert.NotEqual(original, Off(FoliageComparisonFixtures.Copy(f, l => l["width"] = 4)));
        Assert.NotEqual(original, Off(FoliageComparisonFixtures.Copy(f, l => l["originZ"] = -3)));
        static string Off(FoliageFixture fixture)
        {
            MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(FoliageFixtures.Context(fixture), "cover-a", new(-2, 0));
            Assert.Equal(MapFoliageStatus.Complete, batch.Status);
            Assert.Equal(0, batch.Work.PrepareScopes);
            return batch.DependencyDigest;
        }
    }

    [Fact]
    public void ChunkTouchingTheRasterEdge_StillPrepares()
    {
        // The raster's last column lands exactly on chunk 2's west edge at x = 128 when 65 samples wide, and 2 m short of
        // it when 64 wide. Only the touching chunk prepares.
        MapFoliageSampleBatch Chunk2(int width) => MapFoliageDistribution.Generate(FoliageFixtures.Context(FoliageComparisonFixtures.Set(
            FoliageComparisonFixtures.Flat(), FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), 0, -2, 2, width, 3,
                Enumerable.Repeat((byte)255, width * 3).ToArray(), []))), "cover-a", new(2, 0));
        MapFoliageSampleBatch touching = Chunk2(65), off = Chunk2(64);
        Assert.Equal(MapFoliageStatus.Complete, touching.Status);
        Assert.Equal(1, touching.Work.PrepareScopes);
        Assert.True(touching.Work.Faces > 0);
        Assert.Equal(MapFoliageStatus.Complete, off.Status);
        Assert.Equal((0L, 0L), (off.Work.PrepareScopes, off.Work.Faces));
        Assert.NotEmpty(off.DependencyDigest);
    }

    static MapFoliageSampleBatch Generate(FoliageFixture f) => MapFoliageDistribution.Generate(FoliageFixtures.Context(f), "cover-a", new(0, 0));
    static string Pose(MapFoliageSample s) => string.Join("|", s.Key.Address.X, s.Key.Address.Z, s.AssetId, s.Transform, s.Scale, s.ThinningRank);
}
