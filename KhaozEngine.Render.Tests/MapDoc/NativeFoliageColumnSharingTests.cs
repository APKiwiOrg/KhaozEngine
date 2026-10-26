using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void Columns_UploadInputsDetachAfterReservationInsteadOfBorrowingCanonicalArrays()
    {
        using var host = new NativeUploadTestScene();
        var matrices = new Matrix4x4[0]; var positions = new Vector3[0]; var ranks = new float[0]; var indices = new int[0];
        var source = new EmptyColumnSource(GroundCoverInstanceColumns.TakeOwnership(matrices, positions, ranks, indices, []));
        var payload = MapFoliageResource.FromResident(default, source, []);
        var stamp = new MapResourceStamp("closure", "source", "scope", "policy", 1);
        var resource = new MapPreparedResource(new(new(MapRenderKind.Foliage, "empty", "instances", 0), stamp), default, payload, "digest");
        var data = new MapScene3DSink(host.Scene).UploadInput(resource).Foliage!;
        Assert.True(MemoryMarshal.TryGetArray(data.Transforms, out var matrixInput));
        Assert.True(MemoryMarshal.TryGetArray(data.Ranks, out var rankInput));
        Assert.True(MemoryMarshal.TryGetArray(data.ModelIndices, out var indexInput));
        Assert.NotSame(matrices, matrixInput.Array);
        Assert.NotSame(ranks, rankInput.Array);
        Assert.NotSame(indices, indexInput.Array);
    }
    sealed class EmptyColumnSource(GroundCoverInstanceColumns columns) : IGroundCoverInstanceSource
    {
        public int Count => columns.Count;
        public GroundCoverInstanceColumns Columns => columns;
    }
    [Fact]
    public void Columns_BoundsAndDependencyDigestMatchTheFullEightCornerHandoff()
    {
        using var fixture = new Fixture();
        var layer = ((IMapFoliageInputs)fixture.Context).Foliage!.Layers[0];
        var chunk = layer.ChunkDigests.First().Key;
        var full = MapFoliageDistribution.Generate(fixture.Context, layer.Id, chunk);
        var columns = MapFoliageDistribution.GenerateInstances(fixture.Context, layer.Id, chunk);
        var extents = fixture.Policy.Extents.Values.ToArray();
        var before = MapFoliageRenderHandoff.Create(full, extents);
        var after = MapFoliageRenderHandoff.Create(columns, extents);
        Assert.Equal(MapFoliageStatus.Complete, before.Status);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(before.DependencyDigest, after.DependencyDigest);
    }

    [Fact]
    public void Columns_ResidentCoverIdentityAndRegistrationShareOneSource()
    {
        using var fixture = new Fixture();
        using var batch = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var group = ColumnGroups(batch).First(g => !g.Explicit);
        var source = ColumnSource(group.Cover);
        Assert.NotNull(source);
        var recorder = new MapDrawRecorder();
        batch.RegisterResident(fixture.Stamp, recorder);
        var payload = Assert.IsType<MapFoliageResource>(recorder.Snapshot().Resources.Single(r => r.Reference.Key.OwnerId == group.OwnerId && r.Payload is MapFoliageResource).Payload);
        Assert.Same(source, ColumnSource(payload));
        Assert.Contains(ColumnFields(batch).OfType<Array>().SelectMany(a => a.Cast<object>()), value => ReferenceEquals(value, source));
        Assert.True(MemoryMarshal.TryGetArray(payload.BorrowWorldTransforms, out var matrices));
        Assert.True(MemoryMarshal.TryGetArray(payload.BorrowRanks, out var ranks));
        Assert.True(MemoryMarshal.TryGetArray(payload.BorrowModelIndices, out var indices));
        Assert.Same(Assert.Single(ColumnFields(source.Columns).OfType<Matrix4x4[]>()), matrices.Array);
        Assert.Same(Assert.Single(ColumnFields(source.Columns).OfType<float[]>()), ranks.Array);
        Assert.Same(Assert.Single(ColumnFields(source.Columns).OfType<int[]>()), indices.Array);
        AssertNoColumnCopies(group.Cover);
        AssertNoColumnCopies(payload);
    }

    [Fact]
    public void Columns_PublicSnapshotsRemainDetachedAndSurviveBatchDisposal()
    {
        using var fixture = new Fixture();
        using var batch = NativeMapFoliage.Prepare(fixture.Scene, fixture.Context, fixture.Assets, fixture.Policy, fixture.Routing);
        var group = ColumnGroups(batch).First(g => !g.Explicit);
        var source = ColumnSource(group.Cover);
        Assert.NotNull(source);
        var cover = batch.CoverBatches[Array.IndexOf(ColumnGroups(batch), group)];
        var identities = batch.GeneratedIdentities;
        var before = cover.ToArray();
        var publicRecorder = new MapDrawRecorder(); batch.Register(fixture.Stamp, publicRecorder);
        var snapshot = Assert.IsType<MapFoliageResource>(publicRecorder.Snapshot().Resources.Single(r => r.Reference.Key.OwnerId == group.OwnerId && r.Payload is MapFoliageResource).Payload);
        Assert.Null(ColumnSource(cover)); Assert.Null(ColumnSource(snapshot));
        Assert.True(MemoryMarshal.TryGetArray(snapshot.BorrowWorldTransforms, out var detached));
        Assert.NotSame(Assert.Single(ColumnFields(source.Columns).OfType<Matrix4x4[]>()), detached.Array);
        var matrixBits = Bits(snapshot.WorldTransforms.ToArray());
        var keys = identities.ToArray();
        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => batch.GeneratedIdentities);
        Assert.Throws<ObjectDisposedException>(() => batch.CoverBatches);
        Assert.Throws<ObjectDisposedException>(() => batch.Register(fixture.Stamp, publicRecorder));
        Assert.Throws<ObjectDisposedException>(() => batch.Record(Frame, publicRecorder));
        Assert.Equal(before, cover);
        Assert.Equal(keys, identities);
        Assert.Equal(matrixBits, Bits(snapshot.WorldTransforms.ToArray()));
    }

    [Fact]
    public void Columns_ImportedNonuniformKeysRemainLossless()
    {
        using var fixture = new Fixture();
        var original = Samples(NativeRenderAssetFixtures.FoliageSource());
        var samples = original.Samples.Select((sample, i) => new MapFoliageSample(
            new(new("row/" + i, "support/" + i, i - 2, 3 - i), i + 1, new("policy/" + i, i + 2, i * .125f)),
            sample.OccupiedSpaceId, sample.Face, sample.AssetId, sample.MaterialResourceIds, sample.VisibleMaterial,
            sample.Position, sample.Orientation, sample.Scale, sample.Transform, sample.ThinningRank)).ToArray();
        var imported = new MapFoliageSampleBatch(original.LayerId, original.Chunk, original.Status, original.DependencyDigest, samples, original.Work);
        using var batch = NativeMapFoliage.Assemble(fixture.Scene, fixture.Assets, fixture.Policy, fixture.Routing, [imported]);
        Assert.NotNull(ColumnSource(Assert.Single(ColumnGroups(batch)).Cover));
        Assert.Equal(samples.Select(s => s.Key), batch.GeneratedIdentities);
        var recorder = new MapDrawRecorder(); batch.RegisterResident(fixture.Stamp, recorder);
        var payload = Assert.Single(recorder.Snapshot().Resources, r => r.Payload is MapFoliageResource).Payload;
        Assert.NotNull(ColumnSource(payload));
        Assert.Equal(Bits(samples.Select(s => s.Transform).ToArray()), Bits(((MapFoliageResource)payload).WorldTransforms.ToArray()));
    }

    static MapNativeFoliageGroup[] ColumnGroups(MapNativeFoliageBatch batch) =>
        (MapNativeFoliageGroup[])typeof(MapNativeFoliageBatch).GetField("_groups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
    static IEnumerable<object?> ColumnFields(object value) => value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.GetValue(value));
    static IGroundCoverInstanceSource? ColumnSource(object value) => ColumnFields(value).OfType<IGroundCoverInstanceSource>().SingleOrDefault();
    static void AssertNoColumnCopies(object view) => Assert.DoesNotContain(ColumnFields(view).OfType<Array>(), array => array.Length != 0 &&
        array is Matrix4x4[] or Vector3[] or float[] or int[] or GroundCoverInstance[]);
}
