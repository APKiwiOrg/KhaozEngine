using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    internal static IEditorCommand AddMarkerPlacementCommand() => new AddPlacementCommand(new MapPlacement
    {
        Id = "water-marker",
        Kind = "prop",
        AssetId = "mesh-only",
        X = 11f,
        Y = 2f,
        Z = -2f,
    });
}
