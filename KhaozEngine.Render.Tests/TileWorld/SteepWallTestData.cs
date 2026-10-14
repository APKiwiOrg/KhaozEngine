using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The one-tile cliff the steep ground shading exists for: flat grass to the west, a rock plateau
/// 9 m up to the east, and a single tile of wall between them. The wall tile is Rock, so its two foot corners are
/// shared by two Grass tiles and two Rock tiles and the corner vote breaks that tie toward Grass, the lower id.
/// Without slope shading the wall face therefore blends grass at its foot to rock at its lip, and its top-down
/// texture smears into vertical streaks. Grass carries a steep companion of Rock in the catalog, which is the fix
/// under test. The shape is the one a tester found in Grimhollow's Hollowmere, Rock at 900 cm directly beside Grass
/// at 0 cm.</summary>
public static class SteepWallTestData
{
    /// <summary>Catalog id of the grass foot, the material that carries the steep companion.</summary>
    public const ushort Grass = 1;

    /// <summary>Catalog id of the rock wall and plateau, the companion.</summary>
    public const ushort Rock = 14;

    /// <summary>Tile x of the wall. Tiles west of it are grass at 0 cm, the wall and everything east are rock.</summary>
    public const int WallX = 20;

    /// <summary>Height of the plateau in centimetres. Every corner column from <see cref="WallX"/> + 1 east
    /// stands here, so the wall tile climbs it in one tile.</summary>
    public const short PlateauCm = 900;

    /// <summary>Texels on a side of each generated layer.</summary>
    public const int LayerSize = 8;

    /// <summary>The catalog, in memory. The textures are absolute stand-in paths <see cref="Load"/> answers, so
    /// the set is built through <see cref="TileGroundMaterials.Build"/> and the companion reaches the layers the
    /// way a game's catalog does.</summary>
    public static TileWorldCatalogs Catalogs() => TileWorldCatalogs.LoadJson("""
        { "materials": [
            { "id": 1, "name": "grass", "color": "#4d8a3a", "texture": "/steep-wall/grass.png", "steep": { "material": 14 } },
            { "id": 14, "name": "rock", "color": "#7a7a7a", "texture": "/steep-wall/rock.png" } ] }
        """, "steep-wall");

    /// <summary>The world: one region, grass west of the wall, rock from the wall east, and the plateau.</summary>
    public static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "steep-wall", DisplayName = "Steep wall" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 0; z < TileRegion.Size; z++)
        {
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, x < WallX ? Grass : Rock);
            for (int x = WallX + 1; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, PlateauCm);
        }
        return doc;
    }

    /// <summary>A green checker for grass and a grey one for rock, two texels to a square. A checker is what
    /// makes a streak measurable: sampled top-down, a wall reads one row of it stretched up the whole face.</summary>
    public static ImageRgba Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith("grass.png", StringComparison.Ordinal)
            ? Checker(new Vector3(0.36f, 0.60f, 0.26f), new Vector3(0.17f, 0.34f, 0.14f))
            : Checker(new Vector3(0.66f, 0.64f, 0.62f), new Vector3(0.26f, 0.25f, 0.24f));
    }

    static ImageRgba Checker(Vector3 light, Vector3 dark)
    {
        var pixels = new byte[LayerSize * LayerSize * 4];
        for (int y = 0; y < LayerSize; y++)
            for (int x = 0; x < LayerSize; x++)
            {
                Vector3 c = ((x / 2 + y / 2) & 1) == 0 ? light : dark;
                int at = (y * LayerSize + x) * 4;
                pixels[at] = Channel(c.X);
                pixels[at + 1] = Channel(c.Y);
                pixels[at + 2] = Channel(c.Z);
                pixels[at + 3] = 0xff;
            }
        return new ImageRgba(pixels, LayerSize, LayerSize);
    }

    static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}
