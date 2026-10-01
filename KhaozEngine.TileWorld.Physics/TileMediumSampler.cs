using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The water medium of a tile world on plane 0, from the bodies <see cref="TileWaterBodies.Collect"/> finds in each
/// region loaded at build. A point is in water when it is over a body and the feet are below that body's surface,
/// <see cref="TileWaterBody.SurfaceY"/>, so a body standing on a deck above a river stays dry. The in-water medium
/// carries the surface height and leaves the wade speed to the movement tuning.
/// <para>Every water tile's surface is collected once into a per-tile table, so a call is one lookup with no search
/// and no allocation. The table is a snapshot of the document it was built from, so a document edited after
/// <see cref="TileWorldColliders.Build"/> needs a rebuild.</para>
/// <para>The table is read-only after construction, so several threads may call the sampler at once as long as
/// nothing edits the document meanwhile.</para>
/// </summary>
public sealed class TileMediumSampler
{
    // The one plane this round describes.
    const int Plane = 0;

    readonly float _tileSize;
    readonly Dictionary<(int X, int Z), float> _surfaceByTile = new();

    internal TileMediumSampler(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord[] regions)
    {
        _tileSize = document.TileSize;
        foreach (RegionCoord region in regions)
            foreach (TileWaterBody body in TileWaterBodies.Collect(document, catalogs, region, Plane))
                foreach (TileRect rect in body.Rects)
                    for (int z = rect.Z; z < rect.Z1; z++)
                        for (int x = rect.X; x < rect.X1; x++)
                            _surfaceByTile[(x, z)] = body.SurfaceY;
        MediumDelegate = MediumAt;
    }

    /// <summary><see cref="MediumAt"/> as the medium delegate the movement step takes, created once.</summary>
    public Func<float, float, float, MovementMedium> MediumDelegate { get; }

    /// <summary>The medium at a world point for feet at a height: in water at the body's surface when the point is
    /// over a water tile and <paramref name="feetY"/> is below that surface, otherwise
    /// <see cref="MovementMedium.Dry"/>.</summary>
    /// <param name="worldX">World x in metres.</param>
    /// <param name="worldZ">World z in metres, which runs against tile z.</param>
    /// <param name="feetY">The height of the feet in metres.</param>
    public MovementMedium MediumAt(float worldX, float worldZ, float feetY)
    {
        int x = (int)MathF.Floor(TileWorldSpace.TileX(worldX, _tileSize));
        int z = (int)MathF.Floor(TileWorldSpace.TileZ(worldZ, _tileSize));
        return _surfaceByTile.TryGetValue((x, z), out float surfaceY) && feetY < surfaceY
            ? new MovementMedium(surfaceY, inWater: true)
            : MovementMedium.Dry;
    }
}
