using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Terrain
{
    /// <summary>Builds a render-free <see cref="WorldSurfaces"/> from deterministic scatter placements (each
    /// walkable-solid prop's unit <see cref="PropSurface"/> placed at the instance's (x,z)/scale/yaw, base Y from
    /// the placement) plus an explicit obstacle/building list. Mirrors <see cref="PropColliders"/>; streaming-
    /// consistent because it shares the coordinate-hash scatter, so the surfaces line up with the rendered props.</summary>
    public static class PropSurfaces
    {
        /// <summary>Place a <see cref="WorldSurface"/> per scatter instance whose id resolves to a
        /// <see cref="PropSurface"/> (the walkable-solid kinds; ids without one are skipped), append the
        /// hand-placed <paramref name="obstacles"/>, and return the broad-phased set.</summary>
        public static WorldSurfaces FromScatter(
            IReadOnlyList<PropPlacement> placements,
            Func<string, PropSurface?> surfaceForId,
            IEnumerable<WorldSurface>? obstacles = null,
            float cellSize = 8f)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (surfaceForId == null) throw new ArgumentNullException(nameof(surfaceForId));

            var list = new List<WorldSurface>(placements.Count);
            foreach (PropPlacement p in placements)
            {
                PropSurface? s = surfaceForId(p.Id);
                if (s is not null)
                    list.Add(new WorldSurface(s, new Vector2(p.X, p.Z), p.Scale, p.Yaw, p.Y));
            }
            if (obstacles != null) list.AddRange(obstacles);
            return new WorldSurfaces(list, cellSize);
        }
    }
}
