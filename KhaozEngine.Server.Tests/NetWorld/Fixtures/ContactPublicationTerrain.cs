using KhaozEngine.Physics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

// The fixture owns the physics world, which releases this installation with its statics.
internal sealed class ContactPublicationTerrain
{
    internal TileWorldColliders Colliders { get; }
    internal TileColliderRegistration Installed { get; }

    internal ContactPublicationTerrain(IPhysicsWorld world, float height)
    {
        var document = new TileWorldDocument { Id = "contact-publication" };
        for (int rz = -1; rz <= 0; rz++)
            for (int rx = -1; rx <= 0; rx++)
            {
                var region = new RegionCoord(rx, rz);
                document.GetOrCreateRegion(region);
                for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                    for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                    {
                        document.SetUnderlay(x, z, 0, 1);
                        document.SetSettings(x, z, 0, TileSettings.NoDraw);
                    }
            }
        for (int z = -3; z < 3; z++)
            for (int x = 0; x < 9; x++) document.SetSettings(x, z, 0, TileSettings.None);
        for (int z = -3; z <= 3; z++)
            for (int x = 0; x <= 9; x++)
                document.SetCornerHeightCm(x, z, 0, checked((short)(100 * (x <= 4 ? height : 0))));
        var catalogs = TileWorldCatalogs.LoadJson("{\"materials\":[],\"archetypes\":[]}", "contact-publication");
        Colliders = TileWorldColliders.Build(document, catalogs, "world");
        Installed = Colliders.AddTo(world);
    }

}
