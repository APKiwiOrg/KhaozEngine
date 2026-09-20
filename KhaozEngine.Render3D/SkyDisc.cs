using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One celestial disc in the procedural sky beyond the primary sun: a moon, a second sun, a ringed neighbour.
    /// Add them to <see cref="SkySettings.ExtraDiscs"/>. The primary disc stays on <see cref="SkySettings"/> itself
    /// (<see cref="SkySettings.SunColor"/> and friends) because it is the one that follows the key light by default.
    /// An extra disc always says where it is. Every disc is drawn the same way by the sky pass and reflected the
    /// same way by the water, and under <see cref="SkyHorizon.World"/> every disc sets through the horizon.
    /// </summary>
    public struct SkyDisc
    {
        /// <summary>World-space direction TO the body. Does not need to be normalized. A zero vector reads as
        /// straight up.</summary>
        public Vector3 Direction;

        /// <summary>Disc + halo colour. Alpha is the opacity, exactly as <see cref="SkySettings.SunColor"/>: fade a
        /// body by lowering alpha, never by darkening the RGB. A disc at alpha 0 is skipped.</summary>
        public Color Color;

        /// <summary>Radius of the solid disc, in the units of <see cref="SkySettings.SunRadius"/>.</summary>
        public float Radius;

        /// <summary>Peak intensity of the halo (0 = disc only), as <see cref="SkySettings.HaloStrength"/>.</summary>
        public float HaloStrength;

        /// <summary>Width of the halo falloff, as <see cref="SkySettings.HaloFalloff"/>. Also feathers the disc
        /// edge.</summary>
        public float HaloFalloff;
    }
}
