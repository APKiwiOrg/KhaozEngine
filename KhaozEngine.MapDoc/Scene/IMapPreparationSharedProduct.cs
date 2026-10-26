namespace KhaozEngine.MapDoc.Scene;

/// <summary>A complete immutable product whose physical inventory has an explicit lease in every consuming view.</summary>
internal interface IMapPreparationSharedProduct
{
    IMapPreparationSharedProduct For(MapSceneAdmission admission);
    MapPreparationInventory Acquire(MapPreparationReservation destination);
}
