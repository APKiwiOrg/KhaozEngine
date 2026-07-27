using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Ready-made environment (sky + lighting) bundles for <see cref="PixelPostProcessSettings"/>, and the
    /// azimuth/elevation sun-angle helper a map-editor slider pair can drive. Each <see cref="EnvironmentPresetKind"/>
    /// sets the sky palette (horizon/zenith/sun colors), <see cref="SkySettings.Enabled"/>,
    /// <see cref="PixelPostProcessSettings.Starfield"/>, <see cref="PixelPostProcessSettings.BackgroundColor"/>, and
    /// the five lighting fields (key light direction/color, ambient, fill direction/color) as one coherent bundle,
    /// so a settings menu can offer a single dropdown instead of exposing every knob.
    /// <para>
    /// Unlike <see cref="SunCycle"/> (a continuous day/night arc driven by a caller-owned clock), these are fixed
    /// snapshots meant for a one-shot "pick a look" menu. A game that wants a live cycle should reach for
    /// <see cref="SunCycle"/> instead, the two are not meant to be mixed for the same scene.
    /// </para>
    /// </summary>
    public static class EnvironmentPresets
    {
        /// <summary>Applies <paramref name="kind"/>'s bundle to <paramref name="post"/>, overwriting the sky
        /// palette, background mode, and lighting fields it owns.</summary>
        public static void Apply(EnvironmentPresetKind kind, PixelPostProcessSettings post)
        {
            if (post is null) throw new ArgumentNullException(nameof(post));
            switch (kind)
            {
                case EnvironmentPresetKind.Day: ApplyDay(post); break;
                case EnvironmentPresetKind.Sunset: ApplySunset(post); break;
                case EnvironmentPresetKind.Night: ApplyNight(post); break;
                case EnvironmentPresetKind.Starfield: ApplyStarfield(post); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown environment preset.");
            }
        }

        static void ApplyDay(PixelPostProcessSettings post)
        {
            // Close to SkySettings' own defaults - the recommended map-editor default, and the look the existing
            // water body colors were tuned against.
            post.Sky.Enabled = true;
            post.Starfield = false;
            post.Sky.HorizonColor = new Color(0.62f, 0.70f, 0.80f, 1f);
            post.Sky.ZenithColor = new Color(0.22f, 0.42f, 0.72f, 1f);
            post.Sky.SunColor = new Color(1f, 0.96f, 0.85f, 1f);
            post.Sky.SunEnabled = true;
            post.BackgroundColor = post.Sky.ZenithColor;

            post.LightDirection = SunLightDirection(azimuthDegrees: 135f, elevationDegrees: 55f);
            post.LightColor = new Color(1f, 0.95f, 0.86f, 1f);
            post.AmbientColor = new Color(0.16f, 0.19f, 0.30f, 1f);
            post.FillLightDirection = Vector3.Normalize(new Vector3(0.6f, -0.3f, 0.5f));
            post.FillLightColor = new Color(0.20f, 0.24f, 0.34f, 1f);
        }

        static void ApplySunset(PixelPostProcessSettings post)
        {
            post.Sky.Enabled = true;
            post.Starfield = false;
            post.Sky.HorizonColor = new Color(0.95f, 0.55f, 0.35f, 1f);
            post.Sky.ZenithColor = new Color(0.16f, 0.14f, 0.32f, 1f);
            post.Sky.SunColor = new Color(1f, 0.55f, 0.30f, 1f);
            post.Sky.SunEnabled = true;
            post.BackgroundColor = post.Sky.ZenithColor;

            post.LightDirection = SunLightDirection(azimuthDegrees: 250f, elevationDegrees: 8f);
            post.LightColor = new Color(1f, 0.55f, 0.32f, 1f);
            post.AmbientColor = new Color(0.20f, 0.14f, 0.20f, 1f);
            post.FillLightDirection = Vector3.Normalize(new Vector3(-0.5f, -0.2f, -0.55f));
            post.FillLightColor = new Color(0.22f, 0.16f, 0.26f, 1f);
        }

        static void ApplyNight(PixelPostProcessSettings post)
        {
            post.Sky.Enabled = true;
            post.Starfield = false;
            post.Sky.HorizonColor = new Color(0.03f, 0.05f, 0.10f, 1f);
            post.Sky.ZenithColor = new Color(0.01f, 0.02f, 0.05f, 1f);
            post.Sky.SunColor = new Color(0.5f, 0.55f, 0.65f, 1f);
            post.Sky.SunEnabled = false;   // no sun disc at night
            post.BackgroundColor = post.Sky.ZenithColor;

            post.LightDirection = SunLightDirection(azimuthDegrees: 300f, elevationDegrees: 45f);
            post.LightColor = new Color(0.12f, 0.16f, 0.26f, 1f);
            post.AmbientColor = new Color(0.05f, 0.07f, 0.12f, 1f);
            post.FillLightDirection = Vector3.Normalize(new Vector3(-0.4f, -0.2f, 0.6f));
            post.FillLightColor = new Color(0.06f, 0.08f, 0.14f, 1f);
        }

        static void ApplyStarfield(PixelPostProcessSettings post)
        {
            post.Sky.Enabled = false;
            post.Starfield = true;
            post.BackgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);

            // WaterRenderer.PackUbo reads SkySettings.HorizonColor/ZenithColor UNCONDITIONALLY - the water surface
            // reflects the sky palette whether or not the sky pass itself is enabled (see the doc on
            // WaterRenderer.PackUbo, around KhaozEngine.Render3D/Rendering/WaterRenderer.cs:303-308). Leaving
            // SkySettings at its bright day-sky defaults here would paint a jagged bright horizon into reflective
            // water against this near-black background, so the palette is pulled down to match BackgroundColor:
            // that is the fix for the seam.
            post.Sky.HorizonColor = post.BackgroundColor;
            post.Sky.ZenithColor = post.BackgroundColor;
            post.Sky.SunEnabled = false;   // no sun in a starfield background

            post.LightDirection = SunLightDirection(azimuthDegrees: 135f, elevationDegrees: 55f);
            post.LightColor = new Color(1f, 0.95f, 0.86f, 1f);
            post.AmbientColor = new Color(0.16f, 0.19f, 0.30f, 1f);
            post.FillLightDirection = Vector3.Normalize(new Vector3(0.6f, -0.3f, 0.5f));
            post.FillLightColor = new Color(0.20f, 0.24f, 0.34f, 1f);
        }

        /// <summary>
        /// The key-light TRAVEL direction (normalized) for a sun at the given compass azimuth and elevation, for
        /// driving <see cref="PixelPostProcessSettings.LightDirection"/> from a map-editor slider pair.
        /// <paramref name="azimuthDegrees"/> is measured clockwise from north (0 = north, 90 = east), and
        /// <paramref name="elevationDegrees"/> is the sun's angle above the horizon (90 = straight overhead): the
        /// same Y-up, north = -Z, east = +X convention <see cref="SunCycle.SolarDirection"/> uses for its own arc,
        /// so an azimuth/elevation pair means the same thing whichever helper computes it.
        /// <para>
        /// A directional light TRAVELS from the sun toward the scene, so for an elevation above the horizon this
        /// returns a downward-pointing (negative Y) vector: the opposite of the direction TO the sun. This matches
        /// <see cref="SkySettings.ResolveSunDirection"/>'s sign convention exactly (it derives the direction TO the
        /// sun as <c>-normalize(lightDirection)</c>), so feeding this method's result through
        /// <see cref="SkySettings.ResolveSunDirection"/> recovers the same direction TO the sun the angles describe.
        /// </para>
        /// </summary>
        public static Vector3 SunLightDirection(float azimuthDegrees, float elevationDegrees)
        {
            float az = azimuthDegrees * MathF.PI / 180f;
            float el = elevationDegrees * MathF.PI / 180f;
            float cosEl = MathF.Cos(el);
            float sinEl = MathF.Sin(el);

            // Direction TOWARD the sun, in the Y-up / north = -Z / east = +X convention SunCycle.SolarDirection
            // shares. Already unit length: x^2 + z^2 + y^2 = cosEl^2 + sinEl^2 = 1.
            var toward = new Vector3(MathF.Sin(az) * cosEl, sinEl, -MathF.Cos(az) * cosEl);

            // The light travels the opposite way: from the sun toward the scene.
            return -toward;
        }
    }
}
