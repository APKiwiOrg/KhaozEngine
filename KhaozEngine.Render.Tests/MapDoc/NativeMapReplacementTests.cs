using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapReplacementTests
{
    [Fact]
    public void PolicyOnlyReplacement_KeepsTheCapturedContextCharged()
    {
        using var f = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        var state = f.State(ResidencyFixture.Window());
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var context = residency.AcceptedPlans.Single().FoliageContext;
        long cpu = residency.Lifetime.CpuBytes;
        residency.RequestReplacement(state with { Policy = ResidencyFixture.Policy(200) });
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Same(context, residency.AcceptedPlans.Single().FoliageContext);
        Assert.Equal(cpu, residency.Lifetime.CpuBytes);
    }

    [Fact]
    public void DensityContextCharge_SurvivesReplacementAndEndsWithTheLastReader()
    {
        using var f = ResidencyFixture.Foliage();
        var state = f.FoliageState(); var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        using var original = residency.AcceptedPlans.Single().RetainContext()!;
        long bytes = original.CpuBytes;
        Assert.True(bytes > 1024);
        residency.RequestReplacement(state with { Policy = ResidencyFixture.Policy(200, state.Policy.Foliage) });
        ResidencyFixture.Complete(residency, scheduler);
        using var current = residency.AcceptedPlans.Single().RetainContext()!;
        Assert.Same(original.Context, current.Context); Assert.Equal(bytes, current.CpuBytes);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        long withReaders = residency.Lifetime.CpuBytes;
        original.Dispose(); Assert.Equal(withReaders, residency.Lifetime.CpuBytes);
        current.Dispose(); Assert.Equal(withReaders - bytes, residency.Lifetime.CpuBytes);
    }

    [Fact]
    public void EqualContentDistinctClosure_IsRejectedBeforeAnyWorkerOrDecode()
    {
        using var f = ResidencyFixture.Placement();
        var other = NativeRenderAssetFixtures.Create();
        Assert.NotSame(f.Closure, other); Assert.Equal(f.Closure.Hash, other.Hash);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        var state = f.State(ResidencyFixture.Window());
        using var residency = new MapRenderResidency(state, device, new(MaxResidentCpuBytes: 20_000_000), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        long cpu = residency.Lifetime.CpuBytes; int uploads = device.Begins;
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var rejected = residency.RequestReplacement(state with { Closure = other });
        Assert.Equal(MapSceneStatus.Invalid, rejected.Status);
        scheduler.RunAll();
        Assert.Equal(cpu, residency.Lifetime.CpuBytes);
        Assert.Equal(uploads, device.Begins); Assert.Equal(accepted, rejected.AcceptedWitness);
        Assert.Null(rejected.PendingWitness);
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("closure", false)]
    [InlineData("scope", false)]
    [InlineData("policy", false)]
    [InlineData("source", true)]
    [InlineData("closure", true)]
    [InlineData("scope", true)]
    [InlineData("policy", true)]
    public void ReplacementFence_RejectsEveryGenerationBeforeAnyUpload(string dimension, bool uploaded)
    {
        using var f = ResidencyFixture.Placement();
        using var other = ResidencyFixture.Placement(NativeRenderAssetFixtures.Create("resident"));
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device,
            new(MaxAppliesPerUpdate: 32), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var accepted = residency.Update().Transition.AcceptedWitness;
        var old = ResidencyFixture.Draw(residency).Draws.ToArray();
        f.Document.Placements[0].X = .125f;
        var first = residency.RequestReplacement(f.State(ResidencyFixture.Window()));
        Assert.NotEqual(accepted, first.PendingWitness);
        if (uploaded) { scheduler.RunAll(); residency.Update(); }
        int began = device.Begins, published = device.Publications;
        MapRenderSourceState next;
        switch (dimension)
        {
            case "source": f.Document.Placements[0].X += .01f; next = f.State(ResidencyFixture.Window()); break;
            case "closure": next = other.State(ResidencyFixture.Window()); break;
            case "scope": next = f.State(ResidencyFixture.Window(-3, 3)); break;
            default: next = f.State(ResidencyFixture.Window()) with { Policy = ResidencyFixture.Policy(200) }; break;
        }
        residency.RequestReplacement(next);
        scheduler.RunAll();
        Assert.Equal(began, device.Begins); Assert.Equal(published, device.Publications);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.Equal(old, ResidencyFixture.Draw(residency).Draws);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.NotEqual(accepted, residency.Update().Transition.AcceptedWitness);
        Assert.Equal(next.Closure.Hash, residency.Update().Transition.AcceptedWitness!.ClosureHash);
    }

    [Fact]
    public void GeneratedFoliage_StableReplacementReusesDistributionAndLayerInvalidationRebuilds()
    {
        var closure = NativeRenderAssetFixtures.Create("foliage");
        using var fixture = NativeSceneFixtures.Create("terrain");
        var doc = fixture.Document; doc.NativeAssets = closure.Roots.ToList();
        var patch = new MapSurfacePatch
        {
            Key = new("ground", 0, 0),
            Width = 64,
            Depth = 4,
            Heights = Enumerable.Repeat(100, 65 * 5).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe), 256).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, 4).ToArray()
        };
        doc.Surfaces.Patches[patch.Key] = patch;
        var floor = doc.Surfaces.Refs[0] with { Frame = MapLatticeFrame.ImportedMetreCentimetre with { RowDirection = MapRowDirection.PositiveZ } };
        doc.Surfaces.Refs[0] = floor with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(floor,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        };
        var density = new byte[1024]; Array.Fill(density, (byte)255, 0, 128);
        doc.Foliage = new(1, [new(1, "cover", new(MapSupportBindingKind.Surface, "ground", null, null, null, null),
            0, 0, 1, 64, 4, MapRowDirection.PositiveZ, 17, 1, .8f, 1.4f, -.01f,
            [new("A", 1), new("B", 1)], [10], [new(10, "paint", MapFoliageMaterialKind.Ordinary)], new("legacy-tile", 1, 0),
            false, false, 0, 0, [new("ground", 0)], [], [], [], [new(new(0, 0), density)])]);
        using var f = new ResidencyFixture(doc, closure);
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale }), closure.Assets.ToDictionary(a => a.Id,
            a => new MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, 0)));
        var state = f.State(ResidencyFixture.Window() with { Channels = MapSceneChannels.Foliage }) with
        { Policy = ResidencyFixture.Policy(foliage: policy) };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var before = residency.AcceptedPlans.Single().Inventory.Foliage.GeneratedIdentities.ToArray();
        var generationKeys = residency.AcceptedPlans.Single().Inventory.Foliage.GenerationKeys.ToArray();
        Assert.NotEmpty(before);
        var cache = MapFoliageRenderCache.For(residency.AcceptedPlans.Single().FoliageContext);
        long generated = cache.GenerationCalls;
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["A"] = new(.2f, .1f, .3f, .04f) },
            ModelCastsShadows = new Dictionary<string, bool> { ["A"] = false }
        };
        var newPolicy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), policy.Extents);
        residency.RequestReplacement(state with { Policy = ResidencyFixture.Policy(200, newPolicy) });
        scheduler.RunAll();
        Assert.Equal(before, residency.AcceptedPlans.Single().Inventory.Foliage.GeneratedIdentities);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(generated, cache.GenerationCalls);
        Assert.Equal(generationKeys, residency.AcceptedPlans.Single().Inventory.Foliage.GenerationKeys);
        residency.Invalidate(state, new(null, null, [], [], [], [], MapNativeInvalidation.Foliage)
        { FoliageLayers = [new("cover", null, null)] });
        ResidencyFixture.Complete(residency, scheduler);
        Assert.True(cache.GenerationCalls > generated);
        Assert.Equal(before, residency.AcceptedPlans.Single().Inventory.Foliage.GeneratedIdentities);
    }

    [Fact]
    public void InvalidReplacement_PreservesAcceptedDrawAndResetsDesiredState()
    {
        using var f = ResidencyFixture.Placement(); using var other = ResidencyFixture.Placement(NativeRenderAssetFixtures.Create("resident"));
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var accepted = residency.DesiredState; var witness = residency.Update().Transition.AcceptedWitness;
        int begins = device.Begins;
        var transition = residency.RequestReplacement(accepted with { Closure = other.Closure });
        Assert.Equal(MapSceneStatus.Invalid, transition.Status);
        Assert.Same(accepted, residency.DesiredState);
        Assert.Equal(witness, transition.AcceptedWitness); Assert.Null(transition.PendingWitness);
        Assert.Equal(begins, device.Begins); Assert.NotEmpty(ResidencyFixture.Draw(residency).Draws);
    }

    [Fact]
    public void StableFoliageEdits_KeepAcceptedBindingUntilAtomicReplacement()
    {
        var closure = NativeRenderAssetFixtures.Create("foliage");
        using var f = ResidencyFixture.Placement(closure);
        f.Document.Placements = [new() { Id = "plant", AssetId = "A", Kind = "plant", Y = 0, NumericId = 1 }];
        NativeFoliagePolicy Foliage(bool changed) => new(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["A"] = changed ? FoliageMotionProfile.Blade : new(.3f, .15f, .4f, .035f) },
            ModelCastsShadows = new Dictionary<string, bool> { ["A"] = !changed }
        }), closure.Assets.ToDictionary(a => a.Id, a => new KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent(a.Id,
            a.RenderBounds, a.SourceUnitsToMetres, 0)));
        MapRenderSourceState State(bool changed) => f.State(ResidencyFixture.Window()) with
        { Policy = ResidencyFixture.Policy(foliage: Foliage(changed), animated: new HashSet<string> { "A", "B" }) };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(State(false), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var before = ResidencyFixture.Draw(residency);
        var transforms = FoliageResource(before).AnchorLocalTransforms.ToArray();
        var witness = residency.Update().Transition.AcceptedWitness;
        var transition = residency.RequestReplacement(State(true));
        Assert.NotEqual(witness, transition.PendingWitness);
        Assert.Equal(before.Draws, ResidencyFixture.Draw(residency).Draws);
        scheduler.RunAll(); residency.Update();
        Assert.Equal(witness, residency.CurrentTransition.AcceptedWitness);
        ResidencyFixture.Complete(residency, scheduler);
        var changed = FoliageResource(ResidencyFixture.Draw(residency));
        Assert.Equal(FoliageMotionProfile.Blade, changed.Models[0].Motion); Assert.False(changed.Models[0].CastShadows);
        Assert.Equal(transforms, changed.AnchorLocalTransforms.ToArray());
        f.Document.Placements[0].AssetId = "B";
        residency.RequestReplacement(State(true));
        Assert.Equal("A", FoliageResource(ResidencyFixture.Draw(residency)).Models[0].ModelId);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal("B", FoliageResource(ResidencyFixture.Draw(residency)).Models[0].ModelId);
    }
    static MapFoliageResource FoliageResource(MapDrawSet set) => Assert.IsType<MapFoliageResource>(Assert.Single(set.Resources, r => r.Payload is MapFoliageResource).Payload);
}
