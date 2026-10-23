using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneWaterOverrideMetadataTests
{
    [Theory]
    [InlineData(1, 256)]
    [InlineData(4, 64)]
    public void RemovedWaterHistory_ConsumesAggregateMetadataAllowance(int instances, int edge)
    {
        var fixture = PrefabFixtures.Create("pool");
        var original = fixture.Document.Prefabs!.Instances.Single();
        var children = fixture.Assets.GetPrefab("hut").Children;
        var child = children.OfType<MapPrefabWaterChild>().Single();
        fixture.Document.NumericIdHighWaterMark += (instances - 1) * 1000;
        var vertices = Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(i, 0))
            .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(edge, i)))
            .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(edge - i, edge)))
            .Concat(Enumerable.Range(0, edge).Select(i => MapLatticeAddress.Corner(0, edge - i))).ToArray();
        var replacement = child with { Value = child.Value with { Domain = new(child.Value.Domain.Frame, [], [new(vertices)]) } };
        var baseline = Capture(false);
        Assert.True(baseline.Status == MapSceneSourceStatus.Ready, string.Join("\n", baseline.Findings));
        var accepted = Capture(true);
        Assert.True(accepted.Status == MapSceneSourceStatus.Ready, string.Join("\n", accepted.Findings));
        Assert.Equal(baseline.Work.MetadataRows, accepted.Work.MetadataRows);
        Assert.True(accepted.Work.RetainedBytes >= baseline.Work.RetainedBytes + 24576,
            "Removed override history retains at least 24 KiB of water vertices without adding effective water rows.");
        var refused = Capture(true, new(MaxMetadataBytes: baseline.Work.RetainedBytes + 16384));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, refused.Status);
        Assert.Null(refused.Source);
        Assert.Equal(0, refused.Work.RetainedBytes);

        MapSceneSourceResult Capture(bool history, MapSceneLimits? limits = null)
        {
            var owners = Enumerable.Range(0, instances).Select(i => original with
            {
                Id = "water-owner-" + i,
                Bindings = new(original.Bindings.Entries.Select(b => b with
                {
                    WorldId = "water-owner-" + i + "/" + b.Child.Key,
                    NumericId = b.NumericId + i * 1000,
                    Removed = b.Child == child.Key,
                }).ToArray()),
                Overrides = new MapPrefabOverride[] { new MapPrefabRemoveOverride(child.Key,
                    history ? new MapPrefabReplaceOverride(child.Key, replacement) : null) }
                    .Concat(children.OfType<MapPrefabMarkerChild>().Select(m => (MapPrefabOverride)new MapPrefabReplaceOverride(m.Key,
                        m with { Value = m.Value with { Name = m.Value.Name + " " + i } }))).ToArray(),
            }).ToArray();
            fixture.Document.Prefabs = new(1, 1, owners, []);
            using var source = MapDocumentSource.FromDocument(fixture.Document);
            return MapSceneSource.Capture(source, fixture.Assets, limits);
        }
    }
}
