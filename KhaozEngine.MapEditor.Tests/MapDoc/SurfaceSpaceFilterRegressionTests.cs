using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceSpaceFilterRegressionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void PartialParentAdditionCannotHideLoadedOrCarriedPatchesFromSpaceQueries(int slot)
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
            var anchor = new MapPatchKey("ridge", 0, 0);
            doc.Surfaces.Patches[anchor].Records.Add(new MapSpaceDoc("yard", MapSpaceKind.Exterior,
                null, null, Array.Empty<string>(), Array.Empty<MapBoundaryRef>(),
                Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
            var remote = new MapPatchKey("ground", 300, 0);
            doc.Surfaces.Patches.Add(remote, SurfaceStorageFixtures.FlatPatch(remote, 1000));
            MapDocumentFile.SaveTiled(doc, dir);
            var expected = new MapPatchKey("ground", slot, 0);
            float x = slot == 0 ? 61 : 19201;
            var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(x, 1), new Vector2(x + 1, 2),
                9, 11, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
            MapPatchFindResult before = MapStoredSurfaceSource.Open(dir).FindPatches(scope);
            Assert.Equal(MapFindStatus.Complete, before.Status);
            Assert.Equal(new[] { expected }, before.Patches.Select(p => p.Key));
            byte[] payload = SurfaceStorageFixtures.PayloadBytes(dir, expected);

            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            Assert.Equal(slot == 0, window.Surfaces.Patches.ContainsKey(expected));
            Assert.True(window.Surfaces.Patches.ContainsKey(anchor));
            int ground = window.Surfaces.Refs.FindIndex(s => s.Id == "ground");
            Assert.True(ground >= 0);
            window.Surfaces.Refs[ground] = window.Surfaces.Refs[ground] with
            {
                IndoorSpan = new("ground-span", new("yard", anchor), 0, 100, Array.Empty<string>()),
            };
            MapDocumentFile.SaveTiled(window, dir);
            MapPatchFindResult after = MapStoredSurfaceSource.Open(dir).FindPatches(scope with { SpaceIds = new[] { "yard" } });
            Assert.Equal(MapFindStatus.Complete, after.Status);
            Assert.Equal(new[] { expected }, after.Patches.Select(p => p.Key));
            Assert.Equal(payload, SurfaceStorageFixtures.PayloadBytes(dir, expected));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingSpanMembershipKeepsOnlyIntrinsicRecordCandidates(bool declaredByRecord)
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
            var anchor = new MapPatchKey("ridge", 0, 0);
            var target = new MapPatchKey("ground", 0, 0);
            doc.Surfaces.Patches[anchor].Records.Add(new MapSpaceDoc("yard", MapSpaceKind.Exterior,
                null, null, Array.Empty<string>(), Array.Empty<MapBoundaryRef>(),
                Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
            if (declaredByRecord)
                doc.Surfaces.Patches[target].Records.Add(new MapSpaceFootprint("yard-floor", new("yard", anchor),
                    target, new[] { 60 }, new(MapBoundKind.SupportFloor, "ground", null),
                    new(MapBoundKind.OpenTop, null, null)));
            int ground = doc.Surfaces.Refs.FindIndex(s => s.Id == "ground");
            doc.Surfaces.Refs[ground] = doc.Surfaces.Refs[ground] with
            {
                IndoorSpan = new("ground-span", new("yard", anchor), 0, 100, Array.Empty<string>()),
            };
            MapDocumentFile.SaveTiled(doc, dir);
            var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(61, 1), new Vector2(62, 2),
                9, 11, new[] { MapSurfaceRole.SupportFloor }, new[] { "yard" }, new MapQueryLimits());
            Assert.Equal(target, Assert.Single(MapStoredSurfaceSource.Open(dir).FindPatches(scope).Patches).Key);
            byte[] payload = SurfaceStorageFixtures.PayloadBytes(dir, target);

            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            ground = window.Surfaces.Refs.FindIndex(s => s.Id == "ground");
            window.Surfaces.Refs[ground] = window.Surfaces.Refs[ground] with { IndoorSpan = null };
            MapDocumentFile.SaveTiled(window, dir);
            MapPatchFindResult result = MapStoredSurfaceSource.Open(dir).FindPatches(scope);
            Assert.Equal(MapFindStatus.Complete, result.Status);
            Assert.Equal(declaredByRecord ? new[] { target } : Array.Empty<MapPatchKey>(), result.Patches.Select(p => p.Key));
            Assert.Equal(payload, SurfaceStorageFixtures.PayloadBytes(dir, target));
        });
    }
}
