using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapFoliageTests
{
    static readonly FoliageMotionProfile[] Profiles = [FoliageMotionProfile.Blade,
        new(.3f, .15f, .4f, .035f), new(.6f, .2f, .1f, .07f), new(.4f, .25f, .3f, .05f), new(.2f, .1f, .2f, .02f)];
    static readonly MapFrameState Frame = new(WorldFrame.Origin, new(WorldFrame.Origin, new(1, 2, 10)),
        MapRoofMode.AlwaysVisible, 12.5, 19, new(WorldFrame.Origin, Vector3.Zero));

    [Fact]
    public void FoliageRegistration_UsesOriginalMatrixAndRank()
    {
        using var f = new Fixture();
        var source = NativeRenderAssetFixtures.FoliageSource();
        var samples = Samples(source);
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [samples]);
        var set = Record(batch, f.Stamp);
        var resource = Assert.IsType<MapFoliageResource>(Assert.Single(set.Resources, r => r.Payload is MapFoliageResource).Payload);
        Assert.Equal(MapFoliageTransformSpace.World, resource.TransformSpace);
        Assert.Throws<InvalidOperationException>(() => resource.AnchorLocalTransforms);
        Assert.Equal(Bits(source.WorldTransforms.ToArray()), Bits(resource.WorldTransforms.ToArray()));
        Assert.Equal(source.Ranks.ToArray().Select(BitConverter.SingleToInt32Bits), resource.Ranks.ToArray().Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(samples.Samples.Select(s => s.Key), batch.GeneratedIdentities);
        Assert.Equal("fixed-distribution", Assert.Single(batch.GenerationKeys).DependencyDigest);
        Assert.Same(f.Routing, batch.Routing);
        Assert.Equal(Matrix4x4.Identity, Assert.Single(set.Draws).LocalTransform);
    }

    [Fact]
    public void ExplicitAnimatedPlants_DrawOnceOutsideDensityInventory()
    {
        using var f = new Fixture(plants: true);
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        using var rigid = NativeMapPlacements.Prepare(f.Scene, f.Assets, f.PlacementPolicy, f.Routing);
        var recorder = new MapDrawRecorder();
        batch.Register(f.Stamp, recorder); rigid.Register(f.Stamp, recorder);
        batch.Record(Frame, recorder); rigid.Record(Frame, recorder);
        var set = recorder.Snapshot();
        Assert.Same(f.Routing, batch.Routing);
        Assert.Equal(new[] { "plant" }, batch.ExplicitIds);
        Assert.Equal(1, set.Draws.Count(d => d.Resource.Key.OwnerId == "plant"));
        Assert.DoesNotContain(set.Resources, r => r.Payload is MapRigidResource && r.Reference.Key.OwnerId == "plant");
        Assert.NotEmpty(rigid.Clusters);
        Assert.DoesNotContain(rigid.Clusters.SelectMany(c => c.RepresentedIds), id => id is "plant" or "static");
        Assert.Equal(new[] { "clustered" }, rigid.Clusters.SelectMany(c => c.RepresentedIds));
        Assert.All(set.Draws.Where(d => d.Resource.Key.OwnerId == "static"), d => Assert.NotNull(d.Rigid));
        Assert.Single(set.Draws.Where(d => d.Resource.Key.OwnerId == "static").Select(d => d.Resource.Key.OwnerId).Distinct());
        Assert.DoesNotContain(batch.ExplicitIds, id => id == "static");
        Assert.All(batch.GeneratedIdentities, id => Assert.Equal("cover", id.Address.LayerId));
        var plant = Assert.IsType<MapFoliageResource>(set.Resources.Single(r => r.Reference.Key.OwnerId == "plant" && r.Payload is MapFoliageResource).Payload);
        Assert.Equal(MapFoliageTransformSpace.AnchorLocal, plant.TransformSpace);
        Assert.Equal(3, plant.Anchor.X); Assert.Equal(0, Assert.Single(plant.AnchorLocalTransforms.ToArray()).M41);
        Assert.Throws<InvalidOperationException>(() => plant.WorldTransforms);
        Assert.Equal(new[] { 0f }, plant.Ranks.ToArray());
    }

    [Fact]
    public void MotionStylePadding_ChangesRegistrationNotDistribution()
    {
        using var f = new Fixture();
        var cache = MapFoliageRenderCache.For(f.Context);
        using var first = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        Assert.NotEmpty(first.GeneratedIdentities);
        long generated = cache.GenerationCalls;
        var changedOptions = Options();
        changedOptions.MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["A"] = Profiles[4], ["B"] = Profiles[3] };
        changedOptions.ModelCastsShadows = new Dictionary<string, bool> { ["A"] = false, ["B"] = true };
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(changedOptions), Extents(f.Closure, 0));
        using var changed = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, policy, f.Routing);
        var before = Record(first, f.Stamp); var after = Record(changed, f.Stamp with { PolicyDigest = "changed", Generation = 2 });
        Assert.Equal(0, cache.GenerationCalls - generated);
        Assert.Equal(first.GenerationKeys, changed.GenerationKeys);
        Assert.Equal(first.GeneratedIdentities, changed.GeneratedIdentities);
        foreach (var group in ColumnGroups(first).Where(g => !g.Explicit))
            Assert.Same(ColumnSource(group.Cover)!.Columns, ColumnSource(ColumnGroups(changed).Single(g => g.OwnerId == group.OwnerId).Cover)!.Columns);
        var a = Foliage(before); var b = Foliage(after);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(Bits(a[i].WorldTransforms.ToArray()), Bits(b[i].WorldTransforms.ToArray()));
            Assert.Equal(a[i].Ranks.ToArray(), b[i].Ranks.ToArray());
            Assert.Equal(a[i].ModelIndices.ToArray(), b[i].ModelIndices.ToArray());
            Assert.Equal(a[i].Models.Select(m => m.ModelId), b[i].Models.Select(m => m.ModelId));
        }
        Assert.NotEqual(first.RenderDigest, changed.RenderDigest);
        Assert.NotEqual(first.UploadKey(f.Stamp), changed.UploadKey(f.Stamp));
        Assert.Equal(.35, first.Bounds!.Value.MaxX - changed.Bounds!.Value.MaxX, 5);
        Assert.Contains(a.SelectMany(r => r.Models), m => m.ModelId == "A" && m.Motion == Profiles[1] && m.CastShadows);
        Assert.Contains(b.SelectMany(r => r.Models), m => m.ModelId == "A" && m.Motion == Profiles[4] && !m.CastShadows);
        Assert.Equal(5, Profiles.Distinct().Count());
        var frozen = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(Options()), Extents(f.Closure, .35f));
        foreach (string id in new[] { "A", "B", "C", "D", "E" })
            Assert.Equal(GroundCoverModelPolicy.Resolve(Options(), id), frozen.Models[id]);
    }

    [Fact]
    public void LayerEffectsInvalidateEveryResidentBatch()
    {
        using var f = new Fixture();
        var cache = MapFoliageRenderCache.For(f.Context);
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        Assert.True(cache.Count >= 2);
        int count = cache.Count;
        var effects = new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.Foliage)
        { FoliageLayers = [new("cover", "same", "same")] };
        var invalidated = cache.Invalidate(effects);
        Assert.Equal(count, invalidated.Count); Assert.Equal(0, cache.Count);
        Assert.All(invalidated, key => Assert.Equal("cover", key.LayerId));
        using var rebuilt = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        var row = Assert.Single(rebuilt.GenerationKeys, k => k.Chunk == new MapFoliageChunkKey(0, 0));
        effects = effects with { FoliageLayers = [], FoliageChunks = [new(row.LayerId, row.Chunk, null, null, "same", "same")] };
        Assert.Single(cache.Invalidate(effects)); Assert.Equal(count - 1, cache.Count);
        Assert.Empty(cache.Invalidate(effects with { FoliageChunks = [], FoliageLayers = [new("absent", null, "new")] }));
        Assert.Equal(count - 1, cache.Invalidate(effects with { Invalidates = MapNativeInvalidation.Unbounded }).Count);
    }

    [Fact]
    public void MixedModelRegistration_PreservesEveryBinding()
    {
        using var f = new Fixture();
        var source = NativeRenderAssetFixtures.FoliageSource();
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [Samples(source)]);
        var recorded = Record(batch, f.Stamp);
        var resource = Assert.Single(Foliage(recorded));
        Assert.Equal(new[] { 0, 1, 0, 1 }, resource.ModelIndices.ToArray());
        Assert.Equal(new[] { "A", "B" }, resource.Models.Select(m => m.ModelId));
        Assert.Equal(new[] { 2, 1 }, resource.Models.Select(m => m.Parts.Count));
        Assert.Equal(new[] { Profiles[1], Profiles[2] }, resource.Models.Select(m => m.Motion));
        Assert.Equal(new[] { true, false }, resource.Models.Select(m => m.CastShadows));
        foreach (var model in resource.Models)
            for (int i = 0; i < model.Parts.Count; i++)
            {
                var part = recorded.Resources.Single(r => r.Reference == model.Parts[i]);
                Assert.Equal(model.ModelId, part.Reference.Key.OwnerId);
                Assert.Equal("model-part/" + i, part.Reference.Key.PartId);
                var expected = NativeRenderAssetFixtures.FoliageParts()[model.ModelId == "A" ? i : 2];
                var payload = Assert.IsType<MapRigidResource>(part.Payload);
                Assert.Equal(expected.Maps.AlphaCutoff, payload.Maps.AlphaCutoff);
                Assert.Equal(expected.Mesh.Vertices.Select(v => v.Position), payload.Mesh.Vertices.Select(v => v.Position));
                Assert.Equal(expected.Mesh.Indices32, payload.Mesh.Indices32);
            }
        using var swapped = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [Samples(NativeRenderAssetFixtures.FoliageSource(true))]);
        var swapResource = Assert.Single(Foliage(Record(swapped, f.Stamp with { Generation = 2 })));
        Assert.Equal(new[] { "B", "A" }, swapResource.Models.Select(m => m.ModelId));
        Assert.Equal(new[] { 0, 1, 0, 1 }, swapResource.ModelIndices.ToArray());
        Assert.Equal(new[] { "B", "A", "B", "A" }, swapResource.ModelIndices.ToArray().Select(i => swapResource.Models[i].ModelId));
        Assert.Equal(Bits(resource.WorldTransforms.ToArray()), Bits(swapResource.WorldTransforms.ToArray()));
        Assert.Equal(resource.Ranks.ToArray(), swapResource.Ranks.ToArray());
        Assert.NotEqual(batch.RenderDigest, swapped.RenderDigest);
        Assert.NotEqual(batch.UploadKey(f.Stamp), swapped.UploadKey(f.Stamp));
    }

    [Fact]
    public void FocusRecords_UseOneAnchorWithoutChangingBinding()
    {
        using var f = new Fixture(plants: true);
        var rows = Samples(NativeRenderAssetFixtures.FoliageSource());
        rows = new(rows.LayerId, rows.Chunk, rows.Status, rows.DependencyDigest,
            rows.Samples.Take(3).Select((s, i) => Sample(s.AssetId, i, Matrix4x4.CreateTranslation(i * 3, 0, 0), s.ThinningRank)).ToArray(), rows.Work);
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [rows]);
        using var props = NativeMapPlacements.Prepare(f.Scene, f.Assets, f.PlacementPolicy, f.Routing);
        var recorder = new MapDrawRecorder(); batch.Register(f.Stamp, recorder); props.Register(f.Stamp, recorder);
        var moving = new MapFoliageFrameState(new(.25f, .75f), .7f, 2.25f, .4f, 4,
            new(new(2, 1, 3), 2, .8f, .2f), new(new(4, 2, 1), 3, .6f, .4f),
            new(new(6, 3, 2), 4, .4f, .6f), new(new(8, 4, 4), 5, .2f, .8f));
        var before = Frame with { FoliageState = moving };
        batch.Record(before, recorder); props.Record(before, recorder);
        var moved = moving with { WindStrength = .2f, I0 = moving.I0 with { Position = new(7, 1, 3) } };
        var after = before with { Focus = new(WorldFrame.Origin, new(5, 0, 0)), TimeSeconds = 13, WindSeed = 23, FoliageState = moved };
        batch.Record(after, recorder); props.Record(after, recorder);
        var set = recorder.Snapshot();
        var draws = set.Draws.Where(d => d.Resource.Key.OwnerId == "cover/0/0").ToArray();
        Assert.Equal(2, draws.Length);
        Assert.Equal(new Vector3(0, 0, 0), draws[0].LocalFocus);
        Assert.Equal(new Vector3(5, 0, 0), draws[1].LocalFocus);
        Assert.Equal(draws[0].Resource, draws[1].Resource);
        Assert.Equal(new MapFoliageFrame(MapFoliageFrameOptions.From(f.Policy.Options), moving, 12.5, 19), draws[0].Foliage);
        Assert.Equal(new MapFoliageFrame(MapFoliageFrameOptions.From(f.Policy.Options), moved, 13, 23), draws[1].Foliage);
        Assert.Equal(4, draws[0].Foliage!.Value.Options.DrawRadius); Assert.Equal(2, draws[0].Foliage!.Value.Options.DensityRadius);
        var plant = set.Draws.Where(d => d.Resource.Key.OwnerId == "plant").ToArray();
        Assert.Equal(new Vector3(-3, 0, 0), plant[0].LocalFocus); Assert.Equal(new Vector3(2, 0, 0), plant[1].LocalFocus);
        Assert.Equal(moving.I0 with { Position = new(-1, 1, 3) }, plant[0].Foliage!.Value.State.I0);
        Assert.Equal(moving.I1 with { Position = new(1, 2, 1) }, plant[0].Foliage!.Value.State.I1);
        Assert.Equal(moving.I2 with { Position = new(3, 3, 2) }, plant[0].Foliage!.Value.State.I2);
        Assert.Equal(moving.I3 with { Position = new(5, 4, 4) }, plant[0].Foliage!.Value.State.I3);
        var staticDraws = set.Draws.Where(d => d.Resource.Key.OwnerId == "static").ToArray();
        Assert.Equal(new[] { 0, 1, 0, 1 }, staticDraws.Select(d => d.Resource.Key.Lod));
        Assert.Equal(new[] { .5f, .5f, 0f, 0f }, staticDraws.Select(d => d.Rigid!.Value.Dissolve));
        Assert.Equal(new[] { 0f, 1f, 0f, 1f }, staticDraws.Select(d => d.Rigid!.Value.DissolveComplement));
        Assert.Equal(new[] { 0f, 3f, 6f }, Foliage(set).First().WorldTransforms.ToArray().Select(m => m.M41));
        Assert.Equal(new[] { 0, 1, 0 }, Foliage(set).First().ModelIndices.ToArray());
        Assert.Equal(0, MapFoliageRenderCache.For(f.Context).GenerationCalls);
    }

    [Fact]
    public void ConflictingPartitionProvenanceAndUnsupportedCpuFallback_RefuseBeforeGeneration()
    {
        using var f = new Fixture(); using var other = new Fixture(plants: true);
        var cache = MapFoliageRenderCache.For(f.Context);
        Assert.Throws<MapDocumentException>(() => NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, other.Routing));
        Assert.Throws<MapDocumentException>(() => NativeMapFoliage.Prepare(f.Scene, other.Context, f.Assets, f.Policy, f.Routing));
        var options = Options(); options.UseGpuBatches = false;
        Assert.Throws<MapRenderAssetRefusal>(() => NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets,
            new(MapFrozenFoliageOptions.Capture(options), Extents(f.Closure, .35f)), f.Routing));
        Assert.Equal(0, cache.GenerationCalls);
        var extents = Extents(f.Closure, .35f); extents.Remove("A");
        Assert.Throws<MapDocumentException>(() => NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets,
            new(MapFrozenFoliageOptions.Capture(Options()), extents), f.Routing));
        Assert.Equal(0, cache.GenerationCalls);
    }

    [Fact]
    public void SkewedMatricesAndFarAnchors_PreserveOriginalRowsAndExactLocalValues()
    {
        using var f = new Fixture();
        var transform = new Matrix4x4(1.125f, .03125f, -.0625f, 0, .25f, .75f, .125f, 0,
            -.125f, .0625f, 1.5f, 0, 32000.125f, -499.75f, -31999.5f, 1);
        var sample = Sample("A", 0, transform, .1234567f);
        var rows = new MapFoliageSampleBatch("cover", new(0, 0), MapFoliageStatus.Complete, "far", [sample], new(0, 1, 0, 0, 0, 0, 0, 0));
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [rows]);
        var origin = WorldFrame.Nearest(32000, -32000);
        var focus = new MapFramePoint(origin, new((float)(32000.25 - (double)origin.X * WorldFrame.Grid), -499.5f,
            (float)(-31999.25 - (double)origin.Z * WorldFrame.Grid)));
        var state = new MapFoliageFrameState(Vector2.UnitX, .7f, 1.8f, .35f, 1,
            new(new(32000.5f, -499.25f, -31999.125f), 2, .6f, .4f), default, default, default);
        var recorder = new MapDrawRecorder(); batch.Register(f.Stamp, recorder);
        batch.Record(Frame with { Frame = origin, Focus = focus, FoliageState = state }, recorder);
        var set = recorder.Snapshot(); var draw = Assert.Single(set.Draws);
        Assert.Equal(new MapSubmissionAnchor(32000, -500, -32000), draw.Anchor);
        Assert.Equal(new Vector3(.25f, .5f, .75f), draw.LocalFocus);
        Assert.Equal(new Vector3(.5f, .75f, .875f), draw.Foliage!.Value.State.I0.Position);
        Assert.Equal(Bits([transform]), Bits(Assert.Single(Foliage(set)).WorldTransforms.ToArray()));
        Assert.Equal(transform, Assert.Single(batch.CoverBatches)[0].Transform);
        Assert.Equal(BitConverter.SingleToInt32Bits(.1234567f), BitConverter.SingleToInt32Bits(Assert.Single(Foliage(set)).Ranks.Span[0]));
    }

    [Fact]
    public void FrozenDefaultsAndInputLease_SurviveCallerEditsAndRejectGenerationMutation()
    {
        using var f = new Fixture();
        var options = Options(); options.Interactors = [new(new(4, 2, 1), 3, .6f, .4f)];
        var extents = Extents(f.Closure, .35f);
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), extents);
        options.WindStrength = .1f; options.Interactors = []; extents.Clear();
        var ids = f.Closure.Assets.Select(a => a.Id).ToArray();
        var proofs = MapRenderExtentValidator.Validate(f.Closure, ids, new(new())).Proofs!;
        using var input = MapPreparedInputLease.LoadSelected(f.Closure, ids, Array.Empty<string>(), proofs, new(new())).Input!;
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, input, policy, f.Routing);
        long retained = batch.InputRetainedBytes; input.Dispose();
        Assert.True(retained > 0); Assert.Equal(retained, batch.InputRetainedBytes);
        var recorder = new MapDrawRecorder();
        Assert.Throws<InvalidOperationException>(() => batch.Record(Frame, recorder));
        Assert.Throws<MapDocumentException>(() => batch.Register(f.Stamp with { ScopeDigest = "other" }, recorder));
        batch.Register(f.Stamp, recorder); batch.Record(Frame, recorder);
        var set = recorder.Snapshot(); Assert.NotEmpty(set.Draws);
        foreach (var draw in set.Draws)
        {
            var actual = draw.Foliage!.Value.State;
            Assert.Equal(.7f, actual.WindStrength); Assert.Equal(1, actual.InteractorCount);
            Assert.Equal(new Vector3((float)(4 - draw.Anchor.X), (float)(2 - draw.Anchor.Y), (float)(1 - draw.Anchor.Z)), actual.I0.Position);
            Assert.Equal(3, actual.I0.Radius); Assert.Equal(.6f, actual.I0.Strength); Assert.Equal(.4f, actual.I0.Agitation);
        }
        Assert.Throws<InvalidOperationException>(() => batch.Register(f.Stamp with { Generation = 2 }, recorder));
        Assert.NotEqual(batch.UploadKey(f.Stamp), batch.UploadKey(f.Stamp with { Generation = 2 }));
        batch.Dispose(); Assert.Throws<ObjectDisposedException>(() => batch.Record(Frame, recorder));
    }

    [Fact]
    public void FiveStableProfiles_ReachRegisteredModelsAndMovingShadowChannels()
    {
        using var f = new Fixture();
        var rows = new MapFoliageSampleBatch("cover", new(0, 0), MapFoliageStatus.Complete, "five-models",
            new[] { "C", "A", "B", "D", "E" }.Select((id, i) => Sample(id, i, Matrix4x4.CreateTranslation(i, 0, 0), .1f)).ToArray(),
            new(0, 5, 0, 0, 0, 0, 0, 0));
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [rows]);
        var set = Record(batch, f.Stamp); var resource = Assert.Single(Foliage(set));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, resource.ModelIndices.ToArray());
        Assert.Equal(new[] { "C", "A", "B", "D", "E" }, resource.Models.Select(m => m.ModelId));
        Assert.Equal(Profiles, resource.Models.Select(m => m.Motion));
        Assert.Equal(new[] { true, true, false, false, true }, resource.Models.Select(m => m.CastShadows));
        Assert.Equal(MapDrawChannels.Colour | MapDrawChannels.Shadow, Assert.Single(set.Draws).Channels);
        var noShadows = Options(); noShadows.ModelCastsShadows = new Dictionary<string, bool>();
        var quiet = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(noShadows), Extents(f.Closure, .35f));
        using var unshadowed = NativeMapFoliage.Assemble(f.Scene, f.Assets, quiet, f.Routing, [rows]);
        var other = Record(unshadowed, f.Stamp with { Generation = 2 });
        Assert.Equal(MapDrawChannels.Colour, Assert.Single(other.Draws).Channels);
        Assert.All(Assert.Single(Foliage(other)).Models, model => Assert.False(model.CastShadows));
        Assert.NotEqual(batch.RenderDigest, unshadowed.RenderDigest);
    }

    [Fact]
    public void SourceUnits_ApplyOnceToModelPartsAndBoundsWithoutChangingMatrices()
    {
        using var f = new Fixture(units: .01f);
        var source = NativeRenderAssetFixtures.FoliageSource();
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, [Samples(source)]);
        var set = Record(batch, f.Stamp); var resource = Assert.Single(Foliage(set));
        Assert.Equal(Bits(source.WorldTransforms.ToArray()), Bits(resource.WorldTransforms.ToArray()));
        var modelA = resource.Models[0];
        var part = Assert.IsType<MapRigidResource>(set.Resources.Single(r => r.Reference == modelA.Parts[0]).Payload);
        Assert.Equal(.01f, part.Mesh.Vertices.Max(v => v.Position.X));
        Assert.Equal(.01f, part.Mesh.Vertices.Max(v => v.Position.Y));
        Assert.Equal(.65, batch.Bounds!.Value.MinX, 5); Assert.Equal(1.36, batch.Bounds.Value.MaxX, 5);
        Assert.Equal(-.36, batch.Bounds.Value.MinY, 5); Assert.Equal(.36, batch.Bounds.Value.MaxY, 5);
    }

    [Fact]
    public void ExplicitPlants_DoNotEnterTheGeneratedDensityPolicy()
    {
        using var f = new Fixture(plants: true);
        var options = Options(); options.QualityDensity = 0; options.DistantDensity = 0;
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), Extents(f.Closure, .35f));
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, policy, f.Routing);
        var set = Record(batch, f.Stamp);
        var plant = Assert.Single(set.Draws, d => d.Resource.Key.OwnerId == "plant");
        Assert.Equal(1, plant.Foliage!.Value.Options.QualityDensity);
        Assert.Equal(1, plant.Foliage.Value.Options.DistantDensity);
        Assert.Equal(100, plant.Foliage.Value.Options.DrawRadius);
        Assert.Equal(.7f, plant.Foliage.Value.State.WindStrength);
        Assert.All(set.Draws.Where(d => d.Resource.Key.OwnerId != "plant"), draw =>
        {
            Assert.Equal(0, draw.Foliage!.Value.Options.QualityDensity);
            Assert.Equal(0, draw.Foliage.Value.Options.DistantDensity);
        });
        Assert.Equal(new[] { "plant" }, batch.ExplicitIds);
    }

    [Fact]
    public void ScopedFoliage_OnlySelectedModelsRequireDecodedAssets()
    {
        using var f = new Fixture(distantLayer: true);
        Assert.Equal(new[] { "A", "B" }, f.Assets.ById.Keys);
        Assert.DoesNotContain("E", f.Policy.Extents.Keys);
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        Assert.NotEmpty(batch.GeneratedIdentities);
        var set = Record(batch, f.Stamp);
        Assert.All(Foliage(set).SelectMany(r => r.Models), model => Assert.Contains(model.ModelId, new[] { "A", "B" }));
        Assert.DoesNotContain(batch.GeneratedIdentities, id => id.Address.LayerId == "remote");
    }

    [Fact]
    public void PlacementOnlyScope_DoesNotGenerateDensity()
    {
        using var f = new Fixture(plants: true);
        var source = f.Scene.Source;
        var scope = f.Scene.Scope with { Channels = MapSceneChannels.Placements | MapSceneChannels.Annotations };
        var acquired = source.Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope);
        var scene = acquired.Scene!;
        var captured = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        var routing = MapPlacementRouting.Create(scene, f.PlacementPolicy, new HashSet<string> { "A" }).Routing!;
        using var batch = NativeMapFoliage.Prepare(scene, captured.Context!, f.Assets, f.Policy, routing);
        Assert.Empty(batch.GeneratedIdentities);
        Assert.Empty(batch.GenerationKeys);
        Assert.Equal(new[] { "plant" }, batch.ExplicitIds);
        var set = Record(batch, f.Stamp with { ScopeDigest = scope.Digest });
        Assert.Equal("plant", Assert.Single(set.Draws).Resource.Key.OwnerId);
        Assert.Equal(0, MapFoliageRenderCache.For(captured.Context!).GenerationCalls);
    }

    [Theory]
    [InlineData(32000.0005)]
    [InlineData(32000.9995)]
    [InlineData(-32000.0005)]
    [InlineData(-31999.9995)]
    public void ExplicitFractionalPose_SubtractsPreciseAnchorBeforeRounding(double x)
    {
        var pose = new MapEffectiveTransform(x, -499.0005, -31999.0005, 0, 1);
        using var f = new Fixture(plants: true, plantPose: pose);
        using var batch = NativeMapFoliage.Prepare(f.Scene, f.Context, f.Assets, f.Policy, f.Routing);
        var recorder = new MapDrawRecorder(); batch.Register(f.Stamp, recorder);
        var frame = WorldFrame.Nearest((float)x, (float)pose.Z);
        batch.Record(Frame with
        {
            Frame = frame,
            Focus = new(frame,
            new((float)(x - (double)frame.X * WorldFrame.Grid), (float)pose.Y, (float)(pose.Z - (double)frame.Z * WorldFrame.Grid)))
        }, recorder);
        var set = recorder.Snapshot(); var draw = Assert.Single(set.Draws);
        var registered = Assert.Single(set.Resources, r => r.Payload is MapFoliageResource);
        var resource = Assert.IsType<MapFoliageResource>(registered.Payload);
        Assert.Equal(MapFoliageTransformSpace.AnchorLocal, resource.TransformSpace);
        Assert.Throws<InvalidOperationException>(() => resource.WorldTransforms);
        var row = Assert.Single(resource.AnchorLocalTransforms.ToArray());
        var local = row.Translation;
        var anchor = new MapSubmissionAnchor((long)Math.Floor(x), (long)Math.Floor(pose.Y), (long)Math.Floor(pose.Z));
        Assert.InRange(Math.Abs((resource.Anchor.X + (double)local.X) - x), 0, .0001);
        Assert.InRange(Math.Abs((resource.Anchor.Y + (double)local.Y) - pose.Y), 0, .0001);
        Assert.InRange(Math.Abs((resource.Anchor.Z + (double)local.Z) - pose.Z), 0, .0001);
        Assert.Equal(anchor, resource.Anchor); Assert.Equal(anchor, draw.Anchor);
        Assert.InRange(Math.Abs(draw.LocalFocus.X - (x - anchor.X)), 0, .0001);
        var extent = f.Policy.Extents["A"];
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var box = extent.BoundsInSourceUnits;
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? box.Min.X : box.Max.X,
                (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z) * extent.SourceUnitsToMetres, row);
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        double p = extent.MotionPaddingMetres + .0001;
        Assert.Equal(anchor.X + (double)minimum.X - p, registered.Bounds.MinX, 8);
        Assert.Equal(anchor.Y + (double)minimum.Y - p, registered.Bounds.MinY, 8);
        Assert.Equal(anchor.Z + (double)minimum.Z - p, registered.Bounds.MinZ, 8);
        Assert.Equal(anchor.X + (double)maximum.X + p, registered.Bounds.MaxX, 8);
        Assert.Equal(anchor.Y + (double)maximum.Y + p, registered.Bounds.MaxY, 8);
        Assert.Equal(anchor.Z + (double)maximum.Z + p, registered.Bounds.MaxZ, 8);
        foreach (var model in resource.Models)
            foreach (var part in model.Parts)
                foreach (var vertex in Assert.IsType<MapRigidResource>(set.Resources.Single(r => r.Reference == part).Payload).Mesh.Vertices)
                {
                    var submitted = Vector3.Transform(vertex.Position, row);
                    Assert.InRange(anchor.X + (double)submitted.X, registered.Bounds.MinX, registered.Bounds.MaxX);
                    Assert.InRange(anchor.Y + (double)submitted.Y, registered.Bounds.MinY, registered.Bounds.MaxY);
                    Assert.InRange(anchor.Z + (double)submitted.Z, registered.Bounds.MinZ, registered.Bounds.MaxZ);
                }
        Assert.Equal(registered.Bounds, batch.Bounds);
    }

    static MapDrawSet Record(MapNativeFoliageBatch batch, MapResourceStamp stamp)
    {
        var recorder = new MapDrawRecorder(); batch.Register(stamp, recorder); batch.Record(Frame, recorder);
        return recorder.Snapshot();
    }
    static MapFoliageResource[] Foliage(MapDrawSet set) => set.Resources.Select(r => r.Payload).OfType<MapFoliageResource>().ToArray();
    static int[] Bits(Matrix4x4[] matrices) => matrices.SelectMany(m => new[] { m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }).Select(BitConverter.SingleToInt32Bits).ToArray();
    static MapFoliageSample Sample(string model, int i, Matrix4x4 transform, float rank) => new(
        new(new("cover", null, i, 0), 1, new("legacy-tile", 1, 0)), null, null, model, [], 10,
        transform.Translation, Quaternion.Identity, 1, transform, rank);
    static MapFoliageSampleBatch Samples(SceneFoliageSource source) => new("cover", new(0, 0), MapFoliageStatus.Complete,
        "fixed-distribution", source.WorldTransforms.ToArray().Select((m, i) => Sample(source.Models[source.ModelIndices.Span[i]].ModelId,
            i, m, source.Ranks.Span[i])).ToArray(), new(0, 4, 0, 0, 0, 0, 0, 0));
    static GroundCoverRenderOptions Options() => new()
    {
        UseGpuBatches = true,
        FadeMode = GroundCoverFadeMode.HeightScale,
        DrawRadius = 4,
        DensityRadius = 2,
        FadeBandWidth = 1,
        QualityDensity = 1,
        DistantDensity = .35f,
        InstanceFadeBandWidth = .5f,
        WindFadeBladePixels = 2,
        WindStrength = .7f,
        MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["A"] = Profiles[1], ["B"] = Profiles[2], ["C"] = Profiles[0], ["D"] = Profiles[3], ["E"] = Profiles[4] },
        ModelCastsShadows = new Dictionary<string, bool> { ["A"] = true, ["B"] = false, ["C"] = true, ["D"] = false, ["E"] = true }
    };
    static Dictionary<string, MapFoliageModelExtent> Extents(MapAssetClosure closure, float padding) => closure.Assets.ToDictionary(a => a.Id,
        a => new MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, padding), StringComparer.Ordinal);

    sealed class Fixture : IDisposable
    {
        readonly NativeSceneFixture _fixture;
        internal MapAssetClosure Closure { get; }
        internal MapScopedScene Scene { get; }
        internal MapWindowFoliageContext Context { get; }
        internal MapRenderAssets Assets { get; }
        internal NativeFoliagePolicy Policy { get; }
        internal MapPlacementPolicy PlacementPolicy { get; }
        internal MapPlacementRouting Routing { get; }
        internal MapResourceStamp Stamp { get; }
        internal Fixture(bool plants = false, float units = 1, bool distantLayer = false, MapEffectiveTransform? plantPose = null, MapAssetClosure? closure = null)
        {
            Closure = closure ?? NativeRenderAssetFixtures.Create(units == 1 ? "foliage" : "foliage-centimetres");
            using var empty = NativeSceneFixtures.Create("empty");
            var doc = empty.Document;
            doc.NativeAssets = Closure.Roots.ToList();
            var patch = new MapSurfacePatch
            {
                Key = new("floor", 0, 0),
                Width = 64,
                Depth = 4,
                Heights = Enumerable.Repeat(100, 65 * 5).ToArray(),
                Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0,
                    MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe), 64 * 4).ToArray(),
                Presence = Enumerable.Repeat(ulong.MaxValue, 4).ToArray()
            };
            var floor = new MapSurfaceRef("floor", MapLatticeFrame.ImportedMetreCentimetre with { RowDirection = MapRowDirection.PositiveZ }, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
            doc.Surfaces.Patches.Add(patch.Key, patch);
            doc.Surfaces.Refs.Add(floor with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(floor,
                [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
            });
            var chunks = new[] { new MapFoliageChunk(new(0, 0), Density()), new MapFoliageChunk(new(1, 0), Density()) };
            doc.Foliage = new(1, [new(1, "cover", new(MapSupportBindingKind.Surface, "floor", null, null, null, null),
                0, 0, 1, 64, 4, MapRowDirection.PositiveZ, 17, 1, .8f, 1.4f, -.01f,
                [new("A", 1), new("B", 1)], [10], [new(10, "paint", MapFoliageMaterialKind.Ordinary)], new("legacy-tile", 1, 0),
                false, false, 0, 0, [new("floor", 0)], [], [], [], chunks)]);
            if (distantLayer)
            {
                var layer = doc.Foliage.Layers[0];
                var remote = new MapFoliageLayer(1, "remote", layer.Support, 4096, 0, 1, 64, 4, layer.RowDirection,
                    layer.Seed, layer.Spacing, layer.ScaleMin, layer.ScaleMax, layer.RootOffset, [new("E", 1)],
                    layer.AllowedVisibleMaterials, layer.Materials, layer.Policy, false, false, 0, 0,
                    layer.CompatibilityOrdinals, [], [], [], chunks);
                doc.Foliage = new(1, [layer, remote]);
            }
            if (plants)
            {
                doc.Placements.Add(new() { Id = "plant", AssetId = "A", Kind = "plant", NumericId = 1, X = 3, Y = 0 });
                doc.Placements.Add(new() { Id = "static", AssetId = "B", Kind = "control", NumericId = 2, X = 3, Y = 0 });
                doc.Placements.Add(new() { Id = "clustered", AssetId = "C", Kind = "control", NumericId = 3, X = 10, Y = 0 });
                doc.NumericIdHighWaterMark = 3;
            }
            if (plantPose is { } precise)
            {
                var plant = doc.Placements.Single(p => p.Id == "plant");
                plant.X = (float)precise.X; plant.Y = (float)precise.Y; plant.Z = (float)precise.Z;
                doc.NativeResourceTransforms = [new(MapPrefabChildKind.Placement, "plant", precise, new string('a', 64))];
                doc.Bounds = new() { MinX = -32064, MinZ = -32064, MaxX = 32064, MaxZ = 32064 };
                doc.PlayableBounds = doc.Bounds;
            }
            _fixture = new(doc, Closure);
            var source = NativeSceneFixtures.Open(_fixture, false);
            var scope = new MapSceneScope(new(WorldFrame.Origin, new(-2, -2), new(66, 6), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new()), new(-1, -2, -1, 64, 4, 4), MapSceneChannels.All, .35f);
            if (plantPose is { } focus)
            {
                var frame = WorldFrame.Nearest((float)focus.X, (float)focus.Z);
                double fx = (double)frame.X * WorldFrame.Grid, fz = (double)frame.Z * WorldFrame.Grid;
                scope = new(new(frame, new((float)(focus.X - fx - 2), (float)(focus.Z - fz - 2)),
                    new((float)(focus.X - fx + 2), (float)(focus.Z - fz + 2)), null, null, Enum.GetValues<MapSurfaceRole>(), null, new()),
                    new(focus.X - 2, focus.Y - 2, focus.Z - 2, focus.X + 2, focus.Y + 2, focus.Z + 2), MapSceneChannels.All, .35f);
            }
            var acquired = source.Acquire(scope); Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings)); Scene = acquired.Scene!;
            var captured = MapFoliageGenerationContext.CaptureWindow(source, scope); Assert.Equal(MapFoliageStatus.Complete, captured.Status); Context = captured.Context!;
            var ids = distantLayer ? new[] { "A", "B" } : Closure.Assets.Select(a => a.Id).ToArray();
            var proofs = MapRenderExtentValidator.Validate(Closure, ids, new(new())); Assert.Equal(MapRenderAssetStatus.Complete, proofs.Status);
            var loaded = MapRenderAssets.LoadSelected(Closure, ids, proofs.Proofs!, new(new())); Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status); Assets = loaded.Assets!;
            Policy = new(MapFrozenFoliageOptions.Capture(Options()), Extents(Closure, .35f).Where(p => ids.Contains(p.Key)).ToDictionary());
            PlacementPolicy = new([new("control", new HashSet<string> { "B" }, [3], 2, 100, 0, 0, 0, true),
                new("cluster", new HashSet<string> { "C" }, [3], 2, 100, 30, 2, 0, true)], 100);
            Routing = MapPlacementRouting.Create(Scene, PlacementPolicy, new HashSet<string> { "A" }).Routing!;
            Stamp = new(Scene.Witness.ClosureHash, Scene.Witness.SourceGeneration, Scene.Witness.ScopeDigest, Routing.PolicyDigest, 1);
        }
        static byte[] Density()
        {
            var bytes = new byte[1024]; Array.Fill(bytes, (byte)255, 0, 128); return bytes;
        }
        public void Dispose() { Assets.Dispose(); _fixture.Dispose(); }
    }

}
