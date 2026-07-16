using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Opt-in procedural sky: a vertical horizon-&gt;zenith gradient with an optional sun disc + halo, rendered as a
    /// fullscreen background pass behind all geometry (only where no mesh was drawn). Reachable as
    /// <see cref="PixelPostProcessSettings.Sky"/>; defaults to <see cref="Enabled"/> == false so existing scenes are
    /// byte-stable (the current clear colour + starfield background is untouched) until a game opts in. Follows the
    /// <see cref="ShadowSettings"/> precedent: a plain settings bag with sensible defaults; the sky math is pure and
    /// headless-tested (<c>SkyMath</c>).
    /// <para>
    /// The sun direction defaults to the key light (<see cref="PixelPostProcessSettings.LightDirection"/>) so the sky
    /// and the scene lighting agree automatically: the sun sits where the light comes from and shadows fall away from
    /// it. Override <see cref="SunDirectionOverride"/> to point the disc somewhere else. Meant for the semi-realistic
    /// outdoor look (pair with <see cref="PixelPostProcessSettings.UseSmoothPreset"/> + shadows); a full
    /// cubemap/skybox is out of scope.
    /// </para>
    /// </summary>
    public sealed class SkySettings
    {
        /// <summary>Draw the procedural sky. Default <c>false</c> (no background pass, no cost, existing goldens
        /// byte-stable - the blit's clear colour / starfield still fills the background). Set <c>true</c> to render
        /// the gradient (and, if <see cref="SunEnabled"/>, the sun) behind the scene.</summary>
        public bool Enabled = false;

        /// <summary>How the sun disc is placed on screen. Default <see cref="SunAnchor.World"/>: the disc is anchored
        /// to the world-space sun direction with a true point-at-infinity projection, so orbiting the camera keeps it
        /// fixed over the world features the light agrees with, and it vanishes when the sun is behind the camera. Set
        /// <see cref="SunAnchor.StylizedBackdrop"/> for the legacy camera-relative placement (a decorative backdrop
        /// that also works under the orthographic iso camera, where the world projection degenerates). See
        /// <see cref="SunAnchor"/>.</summary>
        public SunAnchor Anchor = SunAnchor.World;

        /// <summary>Gradient colour at the horizon (where the view ray is level). Default a warm pale band.</summary>
        public Color HorizonColor = new(0.62f, 0.70f, 0.80f, 1f);

        /// <summary>Gradient colour at the zenith (straight up). Default a deeper blue.</summary>
        public Color ZenithColor = new(0.22f, 0.42f, 0.72f, 1f);

        /// <summary>Draw the sun disc + halo (on top of the gradient). Default <c>true</c> so an enabled sky reads as
        /// an outdoor daytime sky out of the box; set <c>false</c> for a plain gradient (overcast) sky.</summary>
        public bool SunEnabled = true;

        /// <summary>Sun disc + halo colour. Default a bright warm white.</summary>
        public Color SunColor = new(1f, 0.96f, 0.85f, 1f);

        /// <summary>Screen-space radius of the solid sun disc, in NDC-y units (the vertical half-screen is 1.0, so
        /// <c>0.05</c> is 5% of the half-height). The sky is drawn in screen space (so it reads under the
        /// orthographic iso camera too, where all view rays are parallel), and the disc is placed at the screen
        /// projection of the sun direction. Default <c>0.05</c>.</summary>
        public float SunRadius = 0.05f;

        /// <summary>Peak intensity of the soft glow ringing the disc (0 = disc only, no halo). Default <c>0.5</c>.
        /// Blends toward <see cref="SunColor"/> around the disc, fading over <see cref="HaloFalloff"/>.</summary>
        public float HaloStrength = 0.5f;

        /// <summary>Screen-space width of the halo falloff, in NDC-y units (larger = broader, softer glow). Default
        /// <c>0.18</c>. Also feathers the disc edge (a quarter of this) so the disc anti-aliases.</summary>
        public float HaloFalloff = 0.18f;

        /// <summary>Explicit direction TO the sun in world space (does not need to be normalized). When set (non-null),
        /// the disc points here instead of at the key light. Default <c>null</c> = derive from
        /// <see cref="PixelPostProcessSettings.LightDirection"/> so the sky and lighting agree automatically.</summary>
        public Vector3? SunDirectionOverride = null;

        /// <summary>The direction TO the sun this frame: <see cref="SunDirectionOverride"/> if set (normalized), else
        /// derived from the key-light travel direction (<c>-normalize(lightDirection)</c>). Used by the renderer to
        /// build the sky UBO. A degenerate input falls back to straight up.</summary>
        public Vector3 ResolveSunDirection(Vector3 lightDirection)
        {
            if (SunDirectionOverride is { } o)
                return o.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(o);
            return SkyMath.SunDirectionFromLight(lightDirection);
        }
    }
}
