using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabExpansionTests
{
    [Fact]
    public void ResolveAndReload_NeverAllocateOrMutate()
    {
        var f = PrefabFixtures.Create();
        string before = MapDocumentFile.SaveText(f.Document);
        var a = MapPrefabResolver.Expand(f.Document, f.Assets);
        var b = MapPrefabResolver.Expand(MapDocumentFile.LoadText(before), f.Assets);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(PrefabFixtures.Create("missing-binding")));
    }

    [Fact]
    public void LaterCallerMutation_CannotChangeEffectiveView()
    {
        var f = PrefabFixtures.Create("all-resources");
        var r = PrefabFixtures.Effective(f);
        var before = PrefabFixtures.SharedFloorCorner(r);
        f.Document.Surfaces.Patches.Clear();
        f.Document.Prefabs = null;
        r.Surfaces.Patches.Clear();
        r.Water!.Bodies.Clear();
        Assert.Equal(before, PrefabFixtures.SharedFloorCorner(r));
        Assert.Single(r.Water!.Bodies);
    }

    [Fact]
    public void ScopedDigest_FollowsTransformAssetAndBindings()
    {
        var baseline = PrefabFixtures.Effective(PrefabFixtures.Create());
        foreach (string variant in new[] { "transform", "asset-version", "binding", "reservation" })
        {
            var changed = PrefabFixtures.Effective(PrefabFixtures.Create(variant));
            Assert.NotEqual(baseline.Digest, changed.Digest);
            Assert.NotEqual(baseline.CompleteView().Identity.Digest, changed.CompleteView().Identity.Digest);
        }
    }

    [Theory]
    [InlineData("Instances")]
    [InlineData("Children")]
    [InlineData("ReferenceEdges")]
    [InlineData("Patches")]
    [InlineData("Records")]
    [InlineData("GeometryElements")]
    [InlineData("Bindings")]
    [InlineData("PayloadBytes")]
    public void ExpansionPreflight_StopsEveryGuardedOperation(string counter)
    {
        var f = PrefabFixtures.Create("all-resources");
        var work = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        long count = (long)typeof(MapPrefabExpansionWork).GetProperty(counter)!.GetValue(work)!;
        Assert.True(count > 0);
        var stages = new List<string>();
        var limits = Limit(counter, count - 1);
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, limits, new(stages.Add)));
        Assert.Empty(stages);
        MapPrefabResolver.Expand(f.Document, f.Assets, Limit(counter, count), new(stages.Add));
        foreach (string name in new[] { "Copies", "Remaps", "Sorts", "ExpandedGeometry" }) Assert.Contains(name, stages);
    }

    [Fact]
    public void ExpansionPreflight_NegativeLimitsRefuse()
    {
        var f = PrefabFixtures.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, new(MaxChildren: -1)));
    }

    [Theory]
    [InlineData("deck/deck-support/missing")]
    [InlineData("deck/not-advertised/top")]
    [InlineData("deck/glow-light/glow")]
    public void ImplicitTarget_CodecDefersExistenceAndClosureRefusesUnresolvedOrWrongKind(string target)
    {
        var asset = PrefabFixtures.ImplicitReferenceAsset(target);
        byte[] bytes = MapPrefabFile.Write(asset);
        Assert.Equal(target, ((MapPrefabPlacementChild)MapPrefabFile.Read(bytes, "hut").Children.Single(c => c.Key.Key == "low-item")).Value.SupportBinding!.SurfaceId);
        Assert.Contains(target, Assert.Throws<MapDocumentException>(() => PrefabFixtures.LoadAssets(bytes, true)).Message);
    }

    [Theory]
    [InlineData("missing-owner/deck-support/top", MapPrefabChildKind.Surface)]
    [InlineData("deck/deck-support/top", MapPrefabChildKind.Marker)]
    [InlineData("deck//top", MapPrefabChildKind.Surface)]
    [InlineData("deck/deck-support/", MapPrefabChildKind.Surface)]
    public void ImplicitTarget_CodecRefusesWrongKindOwnerOrGrammar(string target, MapPrefabChildKind kind)
    {
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(PrefabFixtures.ImplicitReferenceAsset(target, kind)));
    }

    [Fact]
    public void RetainedPrevious_ImplicitReferenceRequiresClosureOnLoadedAndStoredPaths()
    {
        var f = PrefabFixtures.SupportBoundChild(5);
        var instance = f.Document.Prefabs!.Instances.Single();
        var child = (MapPrefabPlacementChild)f.Assets.GetPrefab("hut").Children.Single(c => c.Key.Key == "low-item");
        var replacement = PrefabFixtures.ImplicitReferenceAsset("deck/deck-support/missing").Children.OfType<MapPrefabPlacementChild>().Single(p => p.Key.Key == "low-item");
        var removed = new MapPrefabRemoveOverride(child.Key, new MapPrefabReplaceOverride(child.Key, replacement));
        instance = instance with { Overrides = new MapPrefabOverride[] { removed }, Bindings = new(instance.Bindings.Entries.Select(b => b.Child == child.Key ? b with { Removed = true } : b).ToArray()) };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Contains("deck/deck-support/missing", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options())).Message);
        var stored = PrefabFixtures.Stored(f.Document);
        Assert.Contains("deck/deck-support/missing", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(stored, f.Assets, PrefabFixtures.Options())).Message);
    }

    [Theory]
    [InlineData("room-domain", "a/room-domain", "a/floor", 0L, MapPatchStatus.Missing)]
    [InlineData("parent-floor", "patch", "exterior", 0L, MapPatchStatus.Unloaded)]
    [InlineData("remote-anchor", "a/room", "a/remote-anchor", 1L, MapPatchStatus.Unloaded)]
    public void ScopedChildren_RequireTheirFullWitness(string fact, string what, string surface, long slotX, MapPatchStatus status)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Seam());
        Assert.Equal(MapAcquireStatus.Complete, PrefabFixtures.AcquireAll(r).Status);
        var missing = PrefabFixtures.AcquireWithout(r, fact);
        Assert.Equal(MapAcquireStatus.Incomplete, missing.Status);
        Assert.False(missing.ReadWitness.Complete);
        Assert.Contains(fact, PrefabFixtures.UnavailableIds(missing));
        Assert.Contains(new MapUnavailable(what, new(surface, slotX, 0), status), missing.Witness.Unavailable);
        Assert.True(PrefabFixtures.UnrelatedFactsRemainReadable(missing));
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(missing).QueryExact(new(new(1, 1), new(1, 1), new(-1, 1))).Status);
    }

    [Fact]
    public void MissingPrefabBytes_RefusesBeforeCaptureAndCapturedBytesAreNeverReread()
    {
        var (roots, source) = PrefabFixtures.CaptureInputs();
        string expected = PrefabFixtures.Create().Assets.GetResource("hut").Reference.Sha256;
        source.MissingPrefab = true;
        var refusal = Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
        Assert.Contains("hut", refusal.Message);
        Assert.Contains(expected, refusal.Message);
        source.MissingPrefab = false;
        var captured = MapAssetClosure.Load(roots, source);
        var f = PrefabFixtures.Create();
        var r = MapPrefabResolver.Expand(f.Document, captured);
        int reads = source.PrefabReads;
        source.MissingPrefab = true;
        Assert.Equal(r.Digest, MapPrefabResolver.Expand(f.Document, captured).Digest);
        Assert.Equal(MapAcquireStatus.Complete, r.Acquire(r.CompleteView().Scope).Status);
        Assert.Equal(reads, source.PrefabReads);
    }

    [Theory]
    [InlineData("Overrides")]
    [InlineData("Transfers")]
    [InlineData("Children")]
    [InlineData("GeometryElements")]
    [InlineData("Bindings")]
    [InlineData("ReferenceEdges")]
    public void RetainedAndPerInstancePreflight_IsBoundedBeforeAllGuardedStages(string counter)
    {
        var f = PrefabFixtures.PreflightRich();
        var work = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        Assert.Equal(4, work.Instances);
        Assert.Equal(8, work.Overrides);
        Assert.Equal(4, work.Transfers);
        Assert.True(work.GeometryElements >= 4 * (4225 + 4096 + 8192));
        long count = (long)typeof(MapPrefabExpansionWork).GetProperty(counter)!.GetValue(work)!;
        var stages = new List<string>();
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, Limit(counter, count - 1), new(stages.Add)));
        Assert.Empty(stages);
        MapPrefabResolver.Expand(f.Document, f.Assets, Limit(counter, count), new(stages.Add));
        Assert.Contains("ExpandedGeometry", stages);
    }

    [Fact]
    public void AssetLocalMaterialization_PreflightSuppressesBeforeCopy()
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var work = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        Assert.Equal(3, work.Children);
        Assert.Equal(1, work.Patches);
        Assert.Equal(0, work.Records);
        Assert.Equal(4, work.Transfers);
        var stages = new List<string>();
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, new(MaxChildren: 2), new(stages.Add)));
        Assert.Empty(stages);
        var r = MapPrefabResolver.Expand(f.Document, f.Assets, new(MaxChildren: 3, MaxPatches: 1, MaxRecords: 0, MaxTransfers: 4), new(stages.Add));
        Assert.Equal(new[] { "captured-floor" }, PrefabFixtures.CapturedSupportOwners(r));
        Assert.Single(r.BuildingIntent.Lights);
    }

    [Fact]
    public void ExpansionPreflight_CheckedProductsAndSumsRefuseOverflow()
    {
        Assert.Equal(long.MaxValue, MapPrefabPreflight.AddProduct(long.MaxValue - 4, 2, 2));
        Assert.Throws<MapDocumentException>(() => MapPrefabPreflight.AddProduct(0, long.MaxValue, 2));
        Assert.Throws<MapDocumentException>(() => MapPrefabPreflight.AddProduct(long.MaxValue, 1, 1));
    }

    [Fact]
    public void StandalonePrecisePlacement_PreservesNumericIdentityAndSupportBinding()
    {
        var f = PrefabFixtures.Create("roomless");
        f.Document.Prefabs = null;
        f.Document.Placements.Add(new MapPlacement { Id = "standalone", Kind = "item", AssetId = "item", NumericId = 101, X = 4.75f, Y = 2.25f, Z = -3, Scale = 1 });
        f.Document.NumericIdHighWaterMark = 101;
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Placement, "standalone", new(4.7500000001234, 2.25, -3, 0, 1), new string('a', 64)) };
        var r = PrefabFixtures.Effective(f);
        Assert.Equal(101, r.Placements.Single().NumericId);
        Assert.Equal(4.7500000001234, r.Transforms["Placement:standalone"].X);
        Assert.Equal(4.75f, r.Placements.Single().Transform.Position.X);
    }

    [Fact]
    public void ImplicitTarget_RecordPoseUsesOwnerAndMaterializedEdgesStaySuppressed()
    {
        var f = PrefabFixtures.AttachedRecord();
        var r = PrefabFixtures.Effective(f);
        var view = r.CompleteView();
        var rail = view.RecordsIn(new(r.Surfaces.Refs.Single(s => s.Id.Contains("deck-support", StringComparison.Ordinal)).Id, 0, 0)).OfType<MapBoundaryChain>().Single(c => c.Id == "a/rail");
        var point = MapBoundaryGeometry.ResolveChain(rail, view).Points.First();
        Assert.Equal(new MapExactPoint(new(4, 1), new(1, 2), new(-4, 1)), point);
        var wrong = f.Assets.GetPrefab("hut");
        wrong = wrong with { Children = wrong.Children.Select(c => c.Key.Key == "rail" ? c with { LocalTransform = new(new(3, 0, -4), 0, 1) } : c).ToArray() };
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(wrong));
        var captured = PrefabFixtures.MaterializedOverrideFixture().Assets.GetPrefab("hut");
        var probe = PrefabFixtures.ImplicitReferenceAsset("deck-owner/deck-support/top").Children.Single(c => c.Key.Key == "low-item");
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(captured with { Children = captured.Children.Append(probe).ToArray() }));
    }

    [Fact]
    public void ScopedAcquisition_ChargesResidentRecordsBeforeCopyAndReturnsNoPartialOwner()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var scope = r.CompleteView().Scope with { Limits = new(MaxRecordReads: 0) };
        var refused = r.Acquire(scope);
        Assert.Equal(MapAcquireStatus.CapacityExceeded, refused.Status);
        Assert.Empty(refused.Witness.Present);
        Assert.Empty(refused.Witness.Records);
        Assert.False(refused.ReadWitness.Complete);
    }

    [Fact]
    public void OwnerAssetOverride_ImplicitReferencesValidateOnLoadedAndStoredPaths()
    {
        var f = PrefabFixtures.SupportBoundChild(5);
        var instance = f.Document.Prefabs!.Instances.Single() with
        {
            Overrides = new MapPrefabOverride[]
            { new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "deck"), null, "item", null) }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        var plan = MapPrefabBindingPlanner.Plan(f.Document, f.Assets.GetPrefab("hut"), f.Assets, "a", instance.Bindings);
        f.Document.Prefabs = new(1, 1, new[] { instance with { Bindings = plan.Bindings } }, []);
        Assert.Contains("deck/deck-support/floor", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options())).Message);
        Assert.Contains("deck/deck-support/floor", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(f.Document), f.Assets, PrefabFixtures.Options())).Message);
    }

    [Fact]
    public void LeafLights_TwoOwnersHaveDistinctBoundEmissions()
    {
        var f = PrefabFixtures.Create("attached");
        var r = PrefabFixtures.Effective(f);
        Assert.Equal(2, r.BuildingIntent.Lights.Count);
        Assert.Equal(2, r.BuildingIntent.Lights.Select(l => l.Id).Distinct().Count());
        Assert.Equal(new[] { new MapExactPoint(new(-2, 1), new(2, 1), new(-2, 1)), new MapExactPoint(new(2, 1), new(2, 1), new(-2, 1)) }, r.LightPoints.Values.OrderBy(p => p.X));
        Assert.Equal(f.Document.Prefabs!.Instances.Single().Bindings.Entries.Count(b => b.Child.Kind == MapPrefabChildKind.Light), r.BuildingIntent.Lights.Count);
    }

    [Theory]
    [InlineData(1e-30f, 1e-30f)]
    [InlineData(1e30f, 1e30f)]
    public void BackendPose_UnrepresentableComposedScaleRefuses(float instanceScale, float childScale)
    {
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(PrefabFixtures.ExtremePose(instanceScale, childScale)));
    }

    [Fact]
    public void CollapsedFrozenSurface_RefusesExpansion()
    {
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(PrefabFixtures.Transformed(0, 1e-8f, System.Numerics.Vector3.Zero)));
    }

    [Fact]
    public void MissingY_ResolvedWorldHeightIsRetainedInPrecisePlacementEntry()
    {
        var document = SupportFixtures.CaveWithPlacements();
        var resolved = KhaozEngine.MapDoc.Support.MapResolverV2.Resolve(document, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(document), SupportFixtures.V2);
        Assert.Equal(4, resolved.Document.EffectiveResources!.Transforms["Placement:p-space"].Y);
        Assert.Equal(5, resolved.Document.EffectiveResources.Transforms["Placement:p-surface"].Y);
    }

    [Fact]
    public void PlacementAssetOverride_PreflightChargesAdditionalAttachments()
    {
        var f = PrefabFixtures.Create("roomless");
        var instance = f.Document.Prefabs!.Instances.Single() with
        {
            Overrides = new MapPrefabOverride[]
            { new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "item"), null, "deck", null) }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        var plan = MapPrefabBindingPlanner.Plan(f.Document, f.Assets.GetPrefab("hut"), f.Assets, "a", instance.Bindings);
        f.Document.Prefabs = new(1, 1, new[] { instance with { Bindings = plan.Bindings } }, []);
        var work = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        Assert.True(work.Children >= 4);
        Assert.True(work.Patches >= 1);
        Assert.True(work.Records >= 1);
        var stages = new List<string>();
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, new(MaxChildren: 3), new(stages.Add)));
        Assert.Empty(stages);
    }

    [Fact]
    public void StandaloneLightAndMarker_UseTheirCapturedPreciseFrames()
    {
        var f = PrefabFixtures.Create("roomless");
        f.Document.Prefabs = null;
        f.Document.BuildingIntent = new(1, [], [], new[] { new MapLocalLightDoc(1, "native-light", null, new(0, 2, 0), System.Numerics.Vector3.One, 1, 3, false) },
            new[] { new MapLocalMarkerDoc(1, "native-marker", "Entry", new(0.5f, 1, -0.5f), 0, "entrance", true, new[] { "first", "second" }) });
        f.Document.NativeResourceTransforms = new[]
        {
            new MapStandaloneResourceTransform(MapPrefabChildKind.Light, "native-light", new(4, 3, -4, 0, 1), new string('a', 64)),
            new MapStandaloneResourceTransform(MapPrefabChildKind.Marker, "native-marker", new(2.5, 3, -1, 0, 1), new string('b', 64)),
        };
        var r = PrefabFixtures.Effective(f);
        Assert.Equal(new MapExactPoint(new(4, 1), new(5, 1), new(-4, 1)), r.LightPoints["native-light"]);
        Assert.Equal(new System.Numerics.Vector3(3, 4, -1.5f), r.BuildingIntent.Markers.Single().Position);
        Assert.Equal(new[] { "first", "second" }, r.BuildingIntent.Markers.Single().Tags);
    }

    static MapPrefabExpansionLimits Limit(string counter, long value) => counter switch
    {
        "Instances" => new(MaxInstances: checked((int)value)),
        "Children" => new(MaxChildren: checked((int)value)),
        "ReferenceEdges" => new(MaxReferenceEdges: checked((int)value)),
        "Patches" => new(MaxPatches: checked((int)value)),
        "Records" => new(MaxRecords: checked((int)value)),
        "GeometryElements" => new(MaxGeometryElements: checked((int)value)),
        "Bindings" => new(MaxBindings: checked((int)value)),
        "PayloadBytes" => new(MaxPayloadBytes: value),
        "Overrides" => new(MaxOverrides: checked((int)value)),
        "Transfers" => new(MaxTransfers: checked((int)value)),
        _ => throw new ArgumentException(counter),
    };
}
