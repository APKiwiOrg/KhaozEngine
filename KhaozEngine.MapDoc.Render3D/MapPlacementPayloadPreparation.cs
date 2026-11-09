using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Before-allocation admission for detached placement resource copies.</summary>
internal static class MapPlacementPayloadPreparation
{
    internal static MapRigidResource Copy(GltfMesh mesh, GltfMaterialMaps maps, MapPreparationScope? productOwner = null)
    {
        var admission = MapSceneAdmission.Current;
        using var preparation = admission?.BeginPreparation();
        admission?.ReserveBytes(checked(512L + 4 * MapNativeInventory.Mesh(mesh, maps)));
        var payload = new MapRigidResource(mesh, maps);
        var ownership = preparation?.Complete(checked(256L + MapNativeInventory.Mesh(payload.BorrowMesh, payload.BorrowMaps)),
            publishToParent: productOwner is null);
        if (productOwner is not null && ownership is not null) productOwner.Track(ownership);
        return payload;
    }
}
