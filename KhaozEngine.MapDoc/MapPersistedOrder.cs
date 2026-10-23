namespace KhaozEngine.MapDoc;

/// <summary>The serialized member order of <see cref="MapDocument"/>, which is declared across several partial
/// files. Without an explicit order, reflection lists members in the order the compiler meets those files, so
/// moving a part to another file reorders saved JSON. Every persisted member takes its position from here through
/// <c>JsonPropertyOrder</c>. The values reproduce the order existing saves already use. Changing them changes
/// saved bytes, so treat any change as a deliberate format change.</summary>
internal static class MapDocumentOrder
{
    internal const int Schema = 1;
    internal const int FormatVersion = 2;
    internal const int Id = 3;
    internal const int DisplayName = 4;
    internal const int Bounds = 5;
    internal const int TileSize = 6;
    internal const int Terrain = 7;
    internal const int ScatterLayers = 8;
    internal const int CompanionLayers = 9;
    internal const int Exclusions = 10;
    internal const int ScatterOverrides = 11;
    internal const int Placements = 12;
    internal const int Spawns = 13;
    internal const int PlayerSpawns = 14;
    internal const int Regions = 15;
    internal const int TerrainOverrides = 16;
    internal const int Prefabs = 17;
    internal const int BuildingIntent = 18;
    internal const int NativeResourceTransforms = 19;
    internal const int SupportRecipe = 20;
    internal const int Surfaces = 21;
    internal const int SurfacePatches = 22;
    internal const int Water = 23;
    internal const int PlayableBounds = 24;
    internal const int NativeAssets = 25;
    internal const int NumericIdHighWaterMark = 26;
    internal const int ResolverIdentity = 27;
    internal const int Foliage = 28;
}

/// <summary>The serialized member order of <see cref="MapPlacement"/>, which is declared across several partial
/// files. Placements are written into tile files and tile content hashes, so their order is part of world
/// identity. The values reproduce the order existing saves already use. Changing them changes saved bytes and
/// hashes, so treat any change as a deliberate format change.</summary>
internal static class MapPlacementOrder
{
    internal const int Id = 1;
    internal const int Kind = 2;
    internal const int X = 3;
    internal const int Z = 4;
    internal const int Y = 5;
    internal const int Yaw = 6;
    internal const int Scale = 7;
    internal const int Tags = 8;
    internal const int SupportBinding = 9;
    internal const int NumericId = 10;
    internal const int AssetId = 11;
    internal const int DisplayName = 12;
}
