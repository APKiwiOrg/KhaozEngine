using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.TileWorld.TileWalkSurfaceTestData;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Pins that the height query allocates nothing, since a presenter asks it once per drawn body and a marker
/// overlay once per outline point, every frame.</summary>
[Collection("AllocSensitive")]  // its zero-alloc assertion must not run alongside the GC-churning parallel tests
public class TileWalkSurfacesAllocTests
{
    [Fact]
    public void The_height_query_allocates_nothing()
    {
        TileWorldDocument doc = BridgeWorld();
        doc.AddObject("platform", 21, 21, 0, 0);
        doc.AddObject("tree", 5, 5, 0, 0);
        TileWorldCatalogs catalogs = Catalogs();
        // The first walk of a dictionary's value collection allocates the collection object it then caches.
        TileWalkSurfaces.TryHeightAt(doc, catalogs, CentreX, CentreZ, 0, out _);

        AllocAssert.NoPerCallAllocation("TileWalkSurfaces.TryHeightAt", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                Assert.True(TileWalkSurfaces.TryHeightAt(doc, catalogs, CentreX, CentreZ, 0, out _));
                Assert.True(TileWalkSurfaces.TryHeightAt(doc, catalogs, 19.5f, CentreZ, 0, out _));
                Assert.False(TileWalkSurfaces.TryHeightAt(doc, catalogs, 5.5f, -5.5f, 0, out _));
            }
        });
    }
}
