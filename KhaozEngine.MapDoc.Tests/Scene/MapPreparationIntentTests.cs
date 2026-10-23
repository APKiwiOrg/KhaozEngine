using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationIntentTests
{
    [Fact]
    public void EffectiveInventory_IncludesEveryCapturedIntentContainer()
    {
        var fixture = Prefabs.PrefabFixtures.Create("all-resources");
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document),
            fixture.Assets, MapDocumentSurfaceSource.Capture(fixture.Document));
        var intent = product.CapturedBuildingIntent;
        Assert.NotEmpty(intent.RoofLinks); Assert.NotEmpty(intent.RoofGroups); Assert.NotEmpty(intent.Storeys);
        // Enumerate owned containers and rows independently of all production receipts.
        long containers = 128;
        foreach (var link in intent.RoofLinks)
            containers += IntPtr.Size + 64 + 32 + (IntPtr.Size + 32L) * (link.Rooms.Count + link.Groups.Count);
        foreach (var group in intent.RoofGroups)
            containers += IntPtr.Size + 32 + (IntPtr.Size + 32L) * group.Rooms.Count;
        foreach (var roof in intent.Roofs) { GC.KeepAlive(roof); containers += IntPtr.Size + 32; }
        foreach (var storey in intent.Storeys) { GC.KeepAlive(storey); containers += IntPtr.Size + 32; }
        foreach (var light in intent.Lights)
            containers += IntPtr.Size + 128 + (light.Owner is null ? 0 : 32) + (light.ShadowExclusion is null ? 0 : 64);
        foreach (var marker in intent.Markers)
            containers += IntPtr.Size + 128 + IntPtr.Size * marker.Tags.Count + (marker.SupportBinding is null ? 0 : 64);
        var inventory = new MapSceneInventory();
        long other = inventory.Context(product.CapturedContext) + inventory.Water(product.CapturedWater) + 256L +
            128L * product.Placements.Count + 96L * product.PlacementSupports.Count +
            product.PaintRegions.Values.Sum(p => 128L + p.Regions.Sum(r => 128L + 48L * r.Polygon.Count)) +
            128L * (product.EffectiveLights.Count + product.EffectiveMarkers.Count + product.MarkerSupports.Count);
        Assert.Equal(other + containers, new MapSceneInventory().Product(product));
    }

    [Fact]
    public void IntentClone_RefusesBeforeAllocatingItsOwnedGraph()
    {
        var intent = new MapBuildingIntent(1, [],
            [new("rooms", [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "room")])], [], []);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        using var active = admission.Enter();
        Assert.Throws<MapSceneCapacityException>(() => intent.Clone());
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
