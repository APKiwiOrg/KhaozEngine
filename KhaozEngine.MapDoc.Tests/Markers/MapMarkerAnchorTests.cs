using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerAnchorTests
{
    /// <summary>Task 7 Step 3. Every instance of a prefab with many Surface-bound marker children is admitted by the
    /// document and prefab codecs, whose ceilings count each file once. The per-instance reference edges exceed the
    /// aggregate expansion ceiling, so anchor reading refuses through the shared preflight before it materializes any
    /// candidate. Two instances of the same prefab read.</summary>
    [Fact]
    public void AnchorRead_ChargesAggregatePreflightBeforeMaterialization()
    {
        const int markers = 512;
        var limits = new MapPrefabExpansionLimits();
        MarkerFixture fixture = ManyBoundMarkers(markers, markers);
        Assert.True(fixture.Document.Prefabs!.Instances.Count <= limits.MaxInstances);
        Assert.True(fixture.Document.Prefabs.Instances.Sum(i => (long)i.Bindings.Entries.Count) <= limits.MaxBindings);
        MapDocument reloaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));

        var error = Assert.Throws<MapDocumentException>(() => MapMarkerAnchors.Read(reloaded, fixture.Assets));
        Assert.Equal("Prefab expansion preflight limit exceeded.", error.Message);

        MarkerFixture control = ManyBoundMarkers(2, markers);
        Assert.Equal(2 * markers, MapMarkerAnchors.Read(control.Document, control.Assets).Count(a => a.Id.Contains("/bound-", System.StringComparison.Ordinal)));
    }

    // The released room prefab with markers Surface-bound to its local floor, placed once through the released binding
    // planner and then copied under distinct instance and world IDs.
    static MarkerFixture ManyBoundMarkers(int instances, int markers)
    {
        MapPrefabMarkerChild template = MarkerFixtures.SurfaceChild();
        var children = Enumerable.Range(0, markers).Select(i => (MapPrefabChild)(template with
        {
            Key = new(MapPrefabChildKind.Marker, "bound-" + i),
            Value = template.Value with { Id = "bound-" + i, Name = "bound-" + i + "-name" },
        })).ToArray();
        MarkerFixture placed = MarkerFixtures.Placed(MarkerFixtures.TransferredFrame, children);
        MapPrefabInstance instance = placed.Document.Prefabs!.Instances.Single();
        var copies = Enumerable.Range(0, instances).Select(n => instance with
        {
            Id = "copy-" + n,
            Bindings = new(instance.Bindings.Entries.Select(b => b with { WorldId = "copy-" + n + "/" + b.Child.Key, NumericId = null }).ToArray()),
        }).ToArray();
        placed.Document.Prefabs = new(1, 1, copies, []);
        return placed;
    }
}
