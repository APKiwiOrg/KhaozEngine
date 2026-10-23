using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneWaterMetadataTests
{
    [Theory]
    [InlineData(1, 256)]
    [InlineData(4, 64)]
    public void RetainedWaterVertices_ExceedMetadataAllowance(int bodies, int edge)
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var water = fixture.Document.Water!;
        var original = water.Bodies.Single();
        water.Bodies.Clear();
        for (int body = 0; body < bodies; body++)
        {
            long offset = body * (edge + 1L);
            var vertices = Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(offset + i, 0))
                .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(offset + edge, i)))
                .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(offset + edge - i, edge)))
                .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(offset, edge - i))).ToArray();
            water.Bodies.Add(original with { Id = "water-" + body, Domain = new(original.Domain.Frame, [], [new(vertices)]) });
        }
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var refused = MapSceneSource.Capture(document, fixture.Assets, new(MaxMetadataBytes: 16384));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, refused.Status);
        Assert.Null(refused.Source);
        Assert.Equal(0, refused.Work.RetainedBytes);
        var accepted = MapSceneSource.Capture(document, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, accepted.Status);
        Assert.True(accepted.Work.RetainedBytes >= 24576);
    }
}
