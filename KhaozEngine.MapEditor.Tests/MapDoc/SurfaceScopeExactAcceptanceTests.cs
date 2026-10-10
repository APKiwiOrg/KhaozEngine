using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceScopeExactAcceptanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LocalAcquisition_UsesExactThirdCellsAndNegativeRowsAtANonOriginFrame(
        bool stored, bool fractionalRectangle)
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocument doc = ThirdCellWorld();
            IMapSurfaceSource source;
            if (stored)
            {
                MapDocumentFile.SaveTiled(doc, dir);
                source = MapStoredSurfaceSource.Open(dir);
            }
            else source = MapDocumentSurfaceSource.Capture(doc);

            Vector2 min = fractionalRectangle ? new(0.34375f, 0.34375f) : Vector2.Zero;
            Vector2 max = fractionalRectangle ? new(0.375f, 0.375f) : Vector2.Zero;
            var scope = new MapSurfaceScope(new WorldFrame(30000, -30000), min, max,
                null, null, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
            MapPatchFindResult result = source.FindPatches(scope);

            // Anchor metres are (3840000,-3840000). Multiplying by 3 gives B = 11520000.
            // Point expansion is [B-1,B+1) in both cell axes, crossing four slots.
            // Fractional bounds are 11/32 through 3/8 m. X cells expand to [B,B+3),
            // while NegativeZ reverses the interval and expands it to [B-3,B).
            MapPatchKey[] expected = fractionalRectangle
                ? new[] { new MapPatchKey("ground", 180000, 179999) }
                : new[]
                {
                    new MapPatchKey("ground", 179999, 179999),
                    new MapPatchKey("ground", 180000, 179999),
                    new MapPatchKey("ground", 179999, 180000),
                    new MapPatchKey("ground", 180000, 180000),
                };
            Assert.Equal(MapFindStatus.Complete, result.Status);
            Assert.Equal(expected, result.Patches.Select(p => p.Key));
            Assert.All(result.Patches, p => Assert.Equal(MapPatchStatus.Present, p.Status));
            Assert.Empty(result.Unavailable);
            Assert.Equal(source.SnapshotId, result.SnapshotId);
            Assert.Equal(scope.Digest, result.Scope.Digest);
            if (stored) Assert.InRange(result.PagesRead, 1, scope.Limits.MaxPageReads);
            else Assert.Equal(0, result.PagesRead);
        });
    }

    static MapDocument ThirdCellWorld()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        doc.Bounds = new() { MinX = -4000000f, MinZ = -4000000f, MaxX = 4000000f, MaxZ = 4000000f };
        doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with
        {
            Frame = new(new(1, 3), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
        };
        // Five fixed 3 by 3 patches. The negative-slot decoy catches an omitted row reversal.
        Add(new("ground", 179999, 179999), 61, 61);
        Add(new("ground", 180000, 179999), 0, 61);
        Add(new("ground", 179999, 180000), 61, 0);
        Add(new("ground", 180000, 180000), 0, 0);
        Add(new("ground", 180000, -180000), 0, 0);
        return doc;

        void Add(MapPatchKey key, int minX, int minZ) =>
            doc.Surfaces.Patches.Add(key, SurfaceAcceptancePatchFixture.Patch(key, minX, minZ, 3, 3));
    }
}
