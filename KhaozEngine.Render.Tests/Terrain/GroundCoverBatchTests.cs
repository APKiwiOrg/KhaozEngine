using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class GroundCoverBatchTests
{
    static GroundCoverInstance[] Field() => Enumerable.Range(0, 4096).Select(i =>
    {
        var position = new Vector3(i % 64, i % 3, i / 64);
        return new GroundCoverInstance("grass", position, Matrix4x4.CreateTranslation(position), i % 100 / 100f);
    }).ToArray();

    [Fact]
    public void SnapshotDoesNotChangeWhenItsSourceIsEdited()
    {
        GroundCoverInstance[] source = Field();
        var batch = new GroundCoverBatch(source);
        GroundCoverInstance first = batch[0];
        source[0] = default;
        Assert.Equal(first, batch[0]);
        Assert.Equal(source.Length, batch.Count);
    }

    [Theory]
    [InlineData(0, 0, 8)]
    [InlineData(32, 32, 12)]
    [InlineData(-12, 28, 16)]
    [InlineData(60, 80, 24)]
    public void BoundedQueueMatchesTheUnbatchedPlacementsAndOrder(float x, float z, float radius)
    {
        GroundCoverInstance[] source = Field();
        var batch = new GroundCoverBatch(source);
        var rawQueue = new SceneInstances();
        var boundedQueue = new SceneInstances();
        var parts = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["grass"] = [new MeshHandle(1)] };
        var options = new GroundCoverRenderOptions { DrawRadius = radius, FadeBandWidth = 6f };
        var focus = new Vector3(x, 0, z);

        int raw = GroundCoverRenderer.Queue(rawQueue, source, parts, focus, options);
        int bounded = GroundCoverRenderer.Queue(boundedQueue, batch, parts, focus, options);
        Assert.Equal(raw, bounded);
        Assert.Equal(rawQueue.Items, boundedQueue.Items);
    }

    [Fact]
    public void DistantRangesAreSkippedWithoutWalkingEachPlacement()
    {
        var batch = new GroundCoverBatch(Field());
        Assert.InRange(batch.SkipOutside(0, new Vector3(32, 0, 60), 8 * 8), 3000, batch.Count - 1);
        Assert.Equal(batch.Count, batch.SkipOutside(0, new Vector3(-200, 0, -200), 8 * 8));
    }
}
