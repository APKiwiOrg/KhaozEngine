using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneCaptureGrowthTests
{
    [Fact]
    public void CaptureJoins_VisitRootMetadataLinearly()
    {
        var small = CaptureVisits(16);
        var large = CaptureVisits(32);
        Assert.InRange(large.Transforms, 1, 2 * small.Transforms + 8);
        Assert.InRange(large.Attachments, 1, 2 * small.Attachments + 8);
        Assert.InRange(large.Lights, 1, 2 * small.Lights + 8);
    }

    static (long Transforms, long Attachments, long Lights) CaptureVisits(int count)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var document = fixture.Document;
        var transforms = new List<MapStandaloneResourceTransform>();
        var attachments = new List<MapPrefabAttachmentTransfer>();
        var lights = new List<MapLocalLightDoc>();
        var markers = new List<MapLocalMarkerDoc>();
        for (int i = 0; i < count; i++)
        {
            string placement = "placed-" + i, light = "light-" + i, marker = "marker-" + i, surface = "surface-" + i;
            document.Placements.Add(new()
            {
                Id = placement,
                Kind = "cosmetic",
                AssetId = "branch",
                X = i * 64,
                Y = 1,
                NumericId = ++document.NumericIdHighWaterMark
            });
            document.Surfaces.Refs.Add(PrefabFixtures.Surface(surface, MapSurfaceRole.SupportFloor));
            var patch = PrefabFixtures.Patch(surface, 2, 2, 0);
            document.Surfaces.Patches.Add(patch.Key, patch);
            attachments.Add(new(placement, "historic-resource", new string('a', 64), MapPrefabAttachmentKind.Light, [], true));
            lights.Add(new(1, light, new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, placement), Vector3.Zero, Vector3.One, 1, 1, true));
            markers.Add(MarkerFixtures.Marker(marker, i, 0, new(0, null)));
            foreach (var (kind, id) in new[] { (MapPrefabChildKind.Surface, surface), (MapPrefabChildKind.Placement, placement),
                (MapPrefabChildKind.Light, light), (MapPrefabChildKind.Marker, marker) })
                transforms.Add(new(kind, id, new(i, 0, 0, 0, 1), new string('a', 64)));
        }
        document.NativeResourceTransforms = transforms;
        document.Prefabs = new(1, 1, [], []) { Attachments = attachments };
        document.BuildingIntent = new(1, [], [], lights, markers);
        using var source = MapDocumentSource.FromDocument(document);
        var countedTransforms = new CountedList<MapStandaloneResourceTransform>(source.Manifest.NativeResourceTransforms!);
        var countedAttachments = new CountedList<MapPrefabAttachmentTransfer>(source.Manifest.Prefabs!.Attachments);
        var countedLights = new CountedList<MapLocalLightDoc>(source.Manifest.BuildingIntent!.Lights);
        source.Manifest.NativeResourceTransforms = countedTransforms;
        source.Manifest.Prefabs = new(1, 1, [], []) { Attachments = countedAttachments };
        source.Manifest.BuildingIntent = new(1, [], [], countedLights, markers);
        var result = MapSceneSource.Capture(source, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, result.Status);
        Assert.Equal(count * 4, result.Source!.Data.Metadata.Count);
        return (countedTransforms.Visits, countedAttachments.Visits, countedLights.Visits);
    }

    sealed class CountedList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        internal long Visits { get; private set; }
        public int Count => values.Count;
        public T this[int index] { get { Visits++; return values[index]; } }
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
