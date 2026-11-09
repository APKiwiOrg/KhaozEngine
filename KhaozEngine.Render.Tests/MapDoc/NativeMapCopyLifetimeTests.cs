using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapCopyLifetimeTests
{
    [Fact]
    public void OrdinaryCopies_ReleaseEachCopyEnvelopeBeforeTheNextPart()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.Placements(closure);
        var prefabs = fixture.Document.Prefabs!;
        var original = prefabs.Instances.Single();
        fixture.Document.Prefabs = new(1, 1, Enumerable.Range(0, 12).Select(i => original with
        {
            Id = "building-" + i,
            Bindings = new(original.Bindings.Entries.Select(b => b with
            {
                WorldId = i == 0 ? b.WorldId : b.WorldId + "-" + i,
                NumericId = b.NumericId is { } number ? number + i * 100 : null,
                Removed = i > 0 && b.Child.Kind == MapPrefabChildKind.Marker,
            }).ToArray()),
            Overrides = i == 0 ? original.Overrides : original.Overrides.Concat(original.Bindings.Entries
                .Where(b => b.Child.Kind == MapPrefabChildKind.Marker)
                .Select(b => (MapPrefabOverride)new MapPrefabRemoveOverride(b.Child, null))).ToArray(),
        }).ToArray(), []);
        fixture.Document.NumericIdHighWaterMark = 2000;
        var scope = NativeMapPlacementTests.Scene(closure).Scope;
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(scope).Scene!;
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapPlacementTests.Policy();
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var baseline = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        long largest = assets.ById.Values.SelectMany(a => a.Lods).SelectMany(l => l.BorrowParts)
            .Max(p => MapNativeInventory.Mesh(p.Mesh, p.Maps));
        var recorder = new MapDrawRecorder();
        baseline.Register(new(scene.Witness.ClosureHash, scene.Witness.SourceGeneration, scene.Scope.Digest, "copy-proof", 1), recorder);
        long finalCopies = 1024L + 192L * recorder.Snapshot().Resources.Count + 256L * scene.Placements.Count;
        long allowance = baseline.RetainedBytes + finalCopies + 4 * largest + 16_384;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        using var active = admission.Enter();
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        Assert.Equal(baseline.RetainedBytes, batch.RetainedBytes);
        Assert.Equal(batch.RetainedBytes, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void GeneratedCopies_ReleaseHandoffAndModelEnvelopesBetweenItems()
    {
        using var f = new Fixture();
        var sample = Samples(NativeRenderAssetFixtures.FoliageSource());
        var samples = Enumerable.Range(0, 64).Select(i => new KhaozEngine.MapDoc.Foliage.MapFoliageSampleBatch(
            sample.LayerId, new(i, 0), sample.Status, sample.DependencyDigest, sample.Samples, sample.Work)).ToArray();
        using var baseline = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, samples);
        long handoff = 512 + 4 * sample.Work.RetainedBytes + 512L * sample.Samples.Count;
        long largest = f.Assets.ById.Values.SelectMany(a => a.Lods).SelectMany(l => l.BorrowParts)
            .Max(p => MapNativeInventory.Mesh(p.Mesh, p.Maps));
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: baseline.RetainedBytes + handoff + 4 * largest + 32_768));
        using var active = admission.Enter();
        using var batch = NativeMapFoliage.Assemble(f.Scene, f.Assets, f.Policy, f.Routing, samples);
        Assert.Equal(baseline.RetainedBytes, batch.RetainedBytes);
        Assert.Equal(batch.RetainedBytes, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
