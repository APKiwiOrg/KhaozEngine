using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// Builds a render-free <see cref="WorldColliders"/> set from deterministic scatter placements plus an
    /// explicit obstacle/building list. For each <see cref="PropPlacement"/> it looks up the prop's
    /// <see cref="ColliderShape"/> by id (falling back to <c>defaultShape</c>, or skipping the placement when
    /// neither resolves) and places it at the instance's (x, z), scaled by <see cref="PropPlacement.Scale"/> and
    /// rotated by <see cref="PropPlacement.Yaw"/>. Because the scatter is coordinate-hash deterministic and
    /// per-area, the colliders line up exactly with the rendered props and a tiled build equals a whole-area
    /// build (streaming-consistent).
    /// </summary>
    public static class PropColliders
    {
        /// <summary>Place a collider per scatter instance (by id via <paramref name="shapeForId"/>, else
        /// <paramref name="defaultShape"/>, else skip), append the hand-placed <paramref name="obstacles"/>, and
        /// return the broad-phased set.</summary>
        public static WorldColliders FromScatter(
            IReadOnlyList<PropPlacement> placements,
            Func<string, ColliderShape?> shapeForId,
            ColliderShape? defaultShape = null,
            IEnumerable<WorldCollider>? obstacles = null,
            float cellSize = 8f)
            => FromScatter(placements, shapeForId, topForId: null, defaultShape, obstacles, cellSize);

        /// <summary>As <see cref="FromScatter(IReadOnlyList{PropPlacement},Func{string,ColliderShape?},ColliderShape?,IEnumerable{WorldCollider},float)"/>,
        /// but stamps each placed collider's <see cref="WorldCollider.Top"/> from <paramref name="topForId"/> (the
        /// prop's solid top world Y, for height-aware blocking - a walkable-solid's baked surface top; +inf for a
        /// thin blocker). A null <paramref name="topForId"/> leaves every collider always-blocking.</summary>
        public static WorldColliders FromScatter(
            IReadOnlyList<PropPlacement> placements,
            Func<string, ColliderShape?> shapeForId,
            Func<string, float>? topForId,
            ColliderShape? defaultShape = null,
            IEnumerable<WorldCollider>? obstacles = null,
            float cellSize = 8f)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (shapeForId == null) throw new ArgumentNullException(nameof(shapeForId));

            var list = new List<WorldCollider>(placements.Count);
            foreach (PropPlacement p in placements)
            {
                ColliderShape? shape = shapeForId(p.Id) ?? defaultShape;
                if (shape is ColliderShape s)
                {
                    float top = topForId?.Invoke(p.Id) ?? float.PositiveInfinity;
                    list.Add(s.Place(new Vector2(p.X, p.Z), p.Scale, p.Yaw, top));
                }
            }
            if (obstacles != null)
                list.AddRange(obstacles);

            return new WorldColliders(list, cellSize);
        }
    }
}
