using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    Vector3 WindowCameraStart()
    {
        var offset = new Vector3(0, 24, -32);
        if (_window is not MapTileRect window) return offset;

        var center = new MapTileCoord(
            (int)(((long)window.Min.X + window.Max.X) / 2),
            (int)(((long)window.Min.Z + window.Max.Z) / 2));
        MapDocument doc = _document.Doc;
        MapPlayerSpawn? spawn = doc.PlayerSpawns
            .Where(spawn => spawn.Enabled && MapTileGrid.CoordOf(spawn.X, spawn.Z, doc.TileSize) == center)
            .OrderBy(spawn => spawn.Id, StringComparer.Ordinal).FirstOrDefault();
        if (spawn is not null) return new Vector3(spawn.X, 0, spawn.Z) + offset;

        return new Vector3((doc.Bounds.MinX + doc.Bounds.MaxX) * .5f, 0,
            (doc.Bounds.MinZ + doc.Bounds.MaxZ) * .5f) + offset;
    }
}
