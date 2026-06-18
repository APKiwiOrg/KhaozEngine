using System;
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Snaps a world position to the art-pixel grid. Reusable by any camera (a gesture camera could snap
    /// too), not just <see cref="CameraFollow"/>. Snaps camera <i>translation</i> to the grid, which kills
    /// camera-induced sub-pixel shimmer; full pixel-perfect rendering also needs integer zoom + a fixed-
    /// resolution render target, which is the game's render-target responsibility, not this layer's.
    /// </summary>
    public readonly struct PixelSnap
    {
        /// <summary>When false, <see cref="Apply"/> returns the input unchanged.</summary>
        public readonly bool Enabled;

        /// <summary>Grid size in world units; <see cref="Apply"/> rounds each axis to a multiple of this.</summary>
        public readonly float WorldUnitsPerPixel;

        /// <summary>Creates a snap with the given grid size (world units per art pixel).
        /// <see cref="Enabled"/> is set to <c>true</c> only when <paramref name="worldUnitsPerPixel"/> is positive.</summary>
        public PixelSnap(float worldUnitsPerPixel)
        {
            WorldUnitsPerPixel = worldUnitsPerPixel;
            Enabled = worldUnitsPerPixel > 0f;
        }

        /// <summary>Rounds each axis to the nearest multiple of <see cref="WorldUnitsPerPixel"/>.
        /// No-op when disabled or the grid size is non-positive.</summary>
        public Vector2 Apply(Vector2 worldPos)
        {
            if (!Enabled || WorldUnitsPerPixel <= 0f) return worldPos;
            float u = WorldUnitsPerPixel;
            return new Vector2(MathF.Round(worldPos.X / u) * u, MathF.Round(worldPos.Y / u) * u);
        }
    }
}
