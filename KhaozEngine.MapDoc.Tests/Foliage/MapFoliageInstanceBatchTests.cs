using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageInstanceBatchTests
{
    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("spaces")]
    [InlineData("slope")]
    [InlineData("far-positive")]
    [InlineData("far-negative")]
    public void CompactGeneration_PreservesEveryOriginalBitAndIdentityInScanOrder(string fixture)
    {
        var context = Context(fixture);
        var layer = context.Foliage!.Layers[fixture == "spaces" ? 1 : 0];
        var chunk = layer.ChunkDigests.First().Key;
        var full = MapFoliageDistribution.Generate(context, layer.Id, chunk);
        Assert.Equal(MapFoliageStatus.Complete, full.Status);
        Assert.NotEmpty(full.Samples);
        Same(full, MapFoliageDistribution.GenerateInstances(context, layer.Id, chunk));
    }

    [Fact]
    public void WindowedCompactGeneration_UsesOnlyItsAdmittedReadClosure()
    {
        var fixture = FoliageComparisonFixtures.TwoSamplesPerChunk();
        using var saved = WindowFoliageFixtures.Scene(fixture);
        var source = NativeSceneFixtures.Open(saved, false);
        var layer = fixture.Document.Foliage!.Layers[0];
        var result = MapFoliageGenerationContext.CaptureWindow(source, WindowFoliageFixtures.Scope(layer, new(0, 0)));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var window = result.Context!;
        Same(MapFoliageDistribution.Generate(window, layer.Id, new(0, 0)),
            MapFoliageDistribution.GenerateInstances(window, layer.Id, new(0, 0)));
        var refused = MapFoliageDistribution.GenerateInstances(window, layer.Id, new(20, 20));
        Assert.Equal(MapFoliageStatus.Unavailable, refused.Status);
        Assert.Equal(0, refused.Count);
        Assert.Empty(refused.DependencyDigest);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("candidates")]
    [InlineData("samples")]
    [InlineData("preparation")]
    [InlineData("faces")]
    public void CompactGeneration_PreservesTypedCountAndPreparationRefusals(string refusal)
    {
        var context = Context("sparse");
        string layer = refusal == "unknown" ? "missing" : "cover-a";
        var limits = refusal switch
        {
            "candidates" => new MapFoliageLimits(MaxCandidates: 0),
            "samples" => new MapFoliageLimits(MaxSamples: 1),
            "preparation" => new MapFoliageLimits(MaxPrepareScopes: 0),
            "faces" => new MapFoliageLimits(MaxFaces: 0),
            _ => new MapFoliageLimits(),
        };
        var full = MapFoliageDistribution.Generate(context, layer, default, limits);
        var compact = MapFoliageDistribution.GenerateInstances(context, layer, default, limits);
        Assert.NotEqual(MapFoliageStatus.Complete, full.Status);
        Assert.Equal(full.Status, compact.Status);
        Assert.Equal(0, compact.Count);
        Assert.Empty(compact.DependencyDigest);
    }

    [Fact]
    public void CompactGeneration_DoesNotChangeSubsequentFullTraceOrObservers()
    {
        var context = Context("spaces");
        var layer = context.Foliage!.Layers[1];
        var chunk = layer.ChunkDigests.First().Key;
        var beforeRows = new List<MapFoliageDecision>();
        var before = MapFoliageDistribution.Generate(context, layer.Id, chunk, observe: beforeRows.Add);
        var compact = MapFoliageDistribution.GenerateInstances(context, layer.Id, chunk);
        Assert.Equal(MapFoliageStatus.Complete, compact.Status);
        var afterRows = new List<MapFoliageDecision>();
        var after = MapFoliageDistribution.Generate(context, layer.Id, chunk, observe: afterRows.Add);
        Assert.Equal(before.Work, after.Work);
        Assert.Equal(before.Samples, after.Samples);
        Assert.Equal(beforeRows, afterRows);
        Assert.Contains(after.Samples, sample => sample.Face is not null && sample.OccupiedSpaceId is not null);
    }

    [Fact]
    public void InstanceColumns_DetachConstructorInputsAndEveryPublicBuffer()
    {
        float minusZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        var original = Matrix4x4.CreateTranslation(100.25f, 2, 3); original.M12 = minusZero;
        Matrix4x4[] matrices = [original]; Vector3[] positions = [new(17, minusZero, 31)];
        float[] ranks = [.25f]; int[] models = [0]; string[] ids = ["A"];
        var columns = new GroundCoverInstanceColumns(matrices, positions, ranks, models, ids);
        matrices[0] = default; positions[0] = default; ranks[0] = 1; models[0] = -1; ids[0] = "changed";
        Assert.Equal(1, columns.Count);
        Assert.Equal(FarFoliageGenerationFixtures.MatrixBits(original), FarFoliageGenerationFixtures.MatrixBits(columns[0].Transform));
        Assert.Equal(new[] { Bits(17), Bits(minusZero), Bits(31) }, PositionBits(columns[0].Position));
        Assert.Equal(.25f, columns[0].ThinningRank);
        Assert.Equal("A", columns[0].ModelId);
        Assert.True(MemoryMarshal.TryGetArray(columns.Transforms, out var matrixSnapshot));
        matrixSnapshot.Array![matrixSnapshot.Offset] = default;
        Assert.True(MemoryMarshal.TryGetArray(columns.Positions, out var positionSnapshot));
        positionSnapshot.Array![positionSnapshot.Offset] = default;
        Assert.True(MemoryMarshal.TryGetArray(columns.Ranks, out var rankSnapshot));
        rankSnapshot.Array![rankSnapshot.Offset] = 1;
        Assert.True(MemoryMarshal.TryGetArray(columns.ModelIndices, out var indexSnapshot));
        indexSnapshot.Array![indexSnapshot.Offset] = -1;
        Assert.Equal(FarFoliageGenerationFixtures.MatrixBits(original), FarFoliageGenerationFixtures.MatrixBits(columns[0].Transform));
        Assert.Equal(new[] { Bits(17), Bits(minusZero), Bits(31) }, PositionBits(columns[0].Position));
        Assert.Equal(.25f, columns[0].ThinningRank);
        Assert.Equal(0, columns.ModelIndex(0));
    }

    [Theory]
    [InlineData(-1, .5f)]
    [InlineData(1, .5f)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    [InlineData(0, float.NaN)]
    public void InstanceColumns_RefuseInvalidBindingsAndRanks(int model, float rank) =>
        Assert.Throws<ArgumentException>(() => new GroundCoverInstanceColumns(new[] { Matrix4x4.Identity }, new[] { Vector3.Zero }, new[] { rank }, new[] { model }, ["A"]));

    [Fact]
    public void InstanceColumns_RefuseMismatchedLengthsAndDuplicateModelNames()
    {
        Assert.Throws<ArgumentException>(() => new GroundCoverInstanceColumns(new[] { Matrix4x4.Identity }, Array.Empty<Vector3>(), new[] { .5f }, new[] { 0 }, ["A"]));
        Assert.Throws<ArgumentException>(() => new GroundCoverInstanceColumns(new[] { Matrix4x4.Identity }, new[] { Vector3.Zero }, new[] { .5f }, new[] { 0 }, ["A", "A"]));
    }

    [Fact]
    public void Builder_FreezesExactColumnsAndFirstOccurrenceTablesOnlyOnce()
    {
        using var builder = new MapFoliageInstanceBuilder("invented", new("legacy-tile", 1, 0), default, new());
        for (int i = 0; i < 3; i++)
        {
            string model = i == 1 ? "B" : new string('A', 1), support = i == 1 ? "second" : new string("first".ToCharArray());
            Assert.True(builder.TryPrepare(support, model));
            builder.AppendPrepared(i, -i, new(model, new(i, 2, 3), Matrix4x4.CreateTranslation(10 + i, 20, 30), .25f));
        }
        var work = new MapFoliageWork(3, 3, 0, 0, 0, 0, 0, 0);
        var batch = builder.Freeze("invented-digest", work);
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.Equal(new[] { "A", "B" }, batch.Columns.ModelIds);
        Assert.Equal(new[] { 0, 1, 0 }, batch.Columns.ModelIndices.ToArray());
        Assert.Equal(new[] { "first", "second", "first" }, Enumerable.Range(0, 3).Select(i => batch.Key(i).Address.SupportOwnerId));
        Assert.Equal(new[] { 2, 3, 3, 3, 3 }, Arrays(batch.Columns));
        Assert.Equal(new[] { 2, 3, 3, 3 }, Arrays(batch));
        // Seven row arrays plus two tables 560, frozen controls 248, policy and distinct retained text 296.
        Assert.Equal(1104, batch.Work.RetainedBytes);
        Assert.Throws<InvalidOperationException>(() => builder.TryPrepare("first", "A"));
        Assert.Throws<InvalidOperationException>(() => builder.Freeze("again", work));
        builder.Dispose();
        Assert.Equal(3, batch.Count);
        Assert.Equal(new Vector3(2, 2, 3), batch.Columns[2].Position);
    }

    static int[] Arrays(object value) => value.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Select(field => field.GetValue(value)).OfType<Array>().Select(array => array.Length).Order().ToArray();

    [Fact]
    public void Builder_PreservesModelAndSupportIndexesAboveThe16BitRange()
    {
        using var builder = new MapFoliageInstanceBuilder("invented", new("legacy-tile", 1, 0), default, new());
        for (int i = 0; i <= 65536; i++)
        {
            string model = "model/" + i, support = "support/" + i;
            Assert.True(builder.TryPrepare(support, model));
            builder.AppendPrepared(i, -i, new(model, new(i, 0, -i), Matrix4x4.Identity, .25f));
        }
        var batch = builder.Freeze("invented-digest", new(65537, 65537, 0, 0, 0, 0, 0, 0));
        Assert.Equal(65537, batch.Count);
        Assert.Equal(65536, batch.Columns.ModelIndex(65536));
        Assert.Equal("model/65536", batch.Columns[65536].ModelId);
        Assert.Equal(new MapFoliageAddress("invented", "support/65536", 65536, -65536), batch.Key(65536).Address);
    }

    static void Same(MapFoliageSampleBatch full, MapFoliageInstanceBatch compact)
    {
        Assert.Equal(full.Status, compact.Status);
        Assert.Equal(full.LayerId, compact.LayerId); Assert.Equal(full.Chunk, compact.Chunk);
        Assert.Equal(full.DependencyDigest, compact.DependencyDigest);
        Assert.Equal(full.Samples.Count, compact.Count);
        Assert.Equal(full.Work with { RetainedBytes = 0 }, compact.Work with { RetainedBytes = 0 });
        Assert.Equal(full.Samples.Select(s => s.AssetId).Distinct(StringComparer.Ordinal), compact.Columns.ModelIds);
        for (int i = 0; i < full.Samples.Count; i++)
        {
            var sample = full.Samples[i]; var instance = compact.Columns[i];
            Assert.Equal(sample.Key, compact.Key(i));
            Assert.Equal(sample.AssetId, instance.ModelId);
            Assert.Equal(PositionBits(sample.Position), PositionBits(instance.Position));
            Assert.Equal(FarFoliageGenerationFixtures.MatrixBits(sample.Transform), FarFoliageGenerationFixtures.MatrixBits(instance.Transform));
            Assert.Equal(Bits(sample.ThinningRank), Bits(instance.ThinningRank));
        }
    }
    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    static int[] PositionBits(Vector3 value) => [Bits(value.X), Bits(value.Y), Bits(value.Z)];
    static MapFoliageGenerationContext Context(string fixture)
    {
        if (fixture.StartsWith("far-", StringComparison.Ordinal))
        {
            int x = fixture == "far-positive" ? 32000 : -32000;
            var far = FarFoliageGenerationFixtures.Create(x, -x);
            return MapFoliageGenerationContext.Capture(far.Document, far.Assets).Context!;
        }
        return FoliageFixtures.Context(fixture switch
        {
            "dense" => FoliageComparisonFixtures.Flat(spacing: 2),
            "sparse" => FoliageComparisonFixtures.TwoSamplesPerChunk(),
            "spaces" => FoliageFixtures.Stacked(),
            "slope" => FoliageFixtures.SlopedBorder(),
            _ => throw new ArgumentException("Unknown invented fixture", nameof(fixture)),
        });
    }
}
