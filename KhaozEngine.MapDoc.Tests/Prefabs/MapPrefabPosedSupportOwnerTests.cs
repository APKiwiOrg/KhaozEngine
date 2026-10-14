using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

// F-R8. Explicit records anchored on a posed owner's implicit support floor declare the owner pose. Unpack enumerates
// those implicit children under the same owner pose as expansion, so the valid prefab unpacks losslessly.
public sealed class MapPrefabPosedSupportOwnerTests
{
    [Theory]
    [InlineData(1.5f, -1f, 0f, 1f)]
    [InlineData(0f, 0f, 0.4f, 1f)]
    [InlineData(0f, 0f, 0f, 1.3f)]
    [InlineData(1.5f, -1f, 0.4f, 1.3f)]
    public void PosedOwnerRecords_UnpackReloadKeepIdentityGeometryAndOneProducer(float x, float z, float yaw, float scale)
    {
        var f = PrefabFixtures.SpaceBoundChild(10, deckPose: new(new(x, 2, z), yaw, scale));
        var before = PrefabFixtures.Effective(f);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        Assert.True(unpack.Prefabs!.Instances.Single().Unpacked);
        foreach (var document in new[] { unpack, MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpack)) })
        {
            var after = MapPrefabResolver.Expand(document, f.Assets);
            // One producer: the deck floor, its embedded support record and the explicit space records occur once.
            string[] Floors(MapEffectiveResources r) => r.Surfaces.Refs.Select(s => s.Id).Where(id => id.Contains("deck", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            string[] Records(MapEffectiveResources r) => r.Surfaces.Patches.Values.SelectMany(p => p.Records).Select(c => c.Id)
                .Where(id => id.Contains("deck", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(Floors(before), Floors(after));
            Assert.Single(Floors(after));
            Assert.Equal(Records(before), Records(after));
            Assert.Equal(Records(after).Length, Records(after).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(Records(after), id => id.EndsWith("deck-space", StringComparison.Ordinal));
            Assert.Contains(Records(after), id => id.EndsWith("deck-domain", StringComparison.Ordinal));
            // Geometry: the floor and its records keep their exact posed transforms and frozen vertices.
            string floor = Floors(after).Single();
            foreach (var id in Records(after).Select(r => MapEffectiveContext.Key(MapPrefabChildKind.Record, r)).Append(MapEffectiveContext.Key(MapPrefabChildKind.Surface, floor)))
                Assert.Equal(before.Transforms[id], after.Transforms[id]);
            var key = new MapPatchKey(floor, 0, 0);
            Assert.Equal(MapEffectiveGeometry.Patch(before.CompleteView(), key).ExactVertices, MapEffectiveGeometry.Patch(after.CompleteView(), key).ExactVertices);
            var seated = Assert.Single(before.PlacementSupports);
            var support = Assert.Single(after.PlacementSupports);
            Assert.Equal((seated.Support.Face, seated.Support.WorldY), (support.Support.Face, support.Support.WorldY));
            Assert.Equal(floor, support.Support.Face!.Value.OwnerId);
            // Identity: loaded and stored authored identities agree for the unpacked document.
            Assert.Equal(MapAuthoredIdentityV2.Compute(document, f.Assets, PrefabFixtures.Options()),
                MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(document), f.Assets, PrefabFixtures.Options()));
        }
        Assert.Equal(10 + 1.2f * 2, Assert.Single(before.PlacementSupports).Support.WorldY, 5);
    }
}
