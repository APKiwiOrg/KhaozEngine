using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One lighting anchor for the day/night cycle. Colors are unclamped, magnitude carries intensity.
    /// </summary>
    public sealed class SunCyclePalette
    {
        /// <summary>Sky gradient color at the horizon.</summary>
        public Color HorizonColor { get; set; }

        /// <summary>Sky gradient color at the zenith.</summary>
        public Color ZenithColor { get; set; }

        /// <summary>Sun disc and halo color. Only visible while the sun is above the horizon.</summary>
        public Color SunColor { get; set; }

        /// <summary>Key light color. Its magnitude is the intensity.</summary>
        public Color LightColor { get; set; }

        /// <summary>Ambient light color. This is the floor that keeps night playable.</summary>
        public Color AmbientColor { get; set; }

        /// <summary>Fill light color.</summary>
        public Color FillColor { get; set; }

        /// <summary>Midday anchor matching the engine's default scene look.</summary>
        public static SunCyclePalette DefaultDay() => new()
        {
            HorizonColor = new Color(0.62f, 0.70f, 0.80f, 1f),
            ZenithColor = new Color(0.22f, 0.42f, 0.72f, 1f),
            SunColor = new Color(1f, 0.96f, 0.85f, 1f),
            LightColor = new Color(1f, 0.95f, 0.86f, 1f),
            AmbientColor = new Color(0.16f, 0.19f, 0.30f, 1f),
            FillColor = new Color(0.20f, 0.24f, 0.34f, 1f),
        };

        /// <summary>Warm low-sun anchor blended in across the twilight band.</summary>
        public static SunCyclePalette DefaultDusk() => new()
        {
            HorizonColor = new Color(0.92f, 0.56f, 0.34f, 1f),
            ZenithColor = new Color(0.24f, 0.22f, 0.42f, 1f),
            SunColor = new Color(1f, 0.55f, 0.28f, 1f),
            LightColor = new Color(0.95f, 0.62f, 0.38f, 1f),
            AmbientColor = new Color(0.21f, 0.17f, 0.25f, 1f),
            FillColor = new Color(0.24f, 0.19f, 0.26f, 1f),
        };

        /// <summary>Cool night anchor. Deliberately not pitch black so scenes stay playable.</summary>
        public static SunCyclePalette DefaultNight() => new()
        {
            HorizonColor = new Color(0.05f, 0.07f, 0.12f, 1f),
            ZenithColor = new Color(0.01f, 0.02f, 0.05f, 1f),
            SunColor = new Color(0f, 0f, 0f, 1f),
            LightColor = new Color(0.10f, 0.14f, 0.24f, 1f),
            AmbientColor = new Color(0.07f, 0.09f, 0.15f, 1f),
            FillColor = new Color(0.08f, 0.10f, 0.16f, 1f),
        };
    }

    /// <summary>
    /// Configuration for the day/night cycle mapping. Time of day is a normalized float where
    /// 0 is midnight and 0.5 is solar noon. The caller owns the clock, the engine only maps.
    /// </summary>
    public sealed class SunCycleSettings
    {
        /// <summary>Observer latitude in degrees. Together with the declination it shapes the sun arc.</summary>
        public float LatitudeDegrees { get; set; } = 35f;

        /// <summary>Solar declination in degrees, the seasonal axis tilt. Nonzero keeps the arc off a boring straight overhead pass.</summary>
        public float SolarDeclinationDegrees { get; set; } = 15f;

        /// <summary>Rotates the whole sun path around the vertical axis, in degrees, so games can aim sunrise wherever the level wants it.</summary>
        public float HeadingDegrees { get; set; }

        /// <summary>Sun elevation in degrees at and above which the palette is pure day. The dusk blend runs from the horizon up to here.</summary>
        public float TwilightStartElevationDegrees { get; set; } = 10f;

        /// <summary>Sun elevation in degrees at and below which the palette is pure night. Stored negative. The dusk-to-night blend runs from the horizon down to here.</summary>
        public float NightFullElevationDegrees { get; set; } = -12f;

        /// <summary>Half-width in degrees of the key-light dip that hides the direction flip at the horizon crossing.</summary>
        public float HorizonKeyDipDegrees { get; set; } = 2f;

        /// <summary>Elevation in degrees over which the sun disc color fades in from the horizon.</summary>
        public float SunDiscFadeElevationDegrees { get; set; } = 4f;

        /// <summary>Palette used when the sun is high.</summary>
        public SunCyclePalette DayPalette { get; set; } = SunCyclePalette.DefaultDay();

        /// <summary>Palette used when the sun sits at the horizon.</summary>
        public SunCyclePalette DuskPalette { get; set; } = SunCyclePalette.DefaultDusk();

        /// <summary>Palette used when the sun is far below the horizon.</summary>
        public SunCyclePalette NightPalette { get; set; } = SunCyclePalette.DefaultNight();
    }

    /// <summary>
    /// The lighting state derived from a time of day. A pure value snapshot: feed it to a scene with
    /// <see cref="SunCycle.Apply"/> each frame, or read individual fields to drive custom sinks.
    /// </summary>
    public readonly struct SunCycleState
    {
        /// <summary>Creates a state from its evaluated components.</summary>
        public SunCycleState(
            Vector3 lightDirection,
            float sunElevationDegrees,
            Color horizonColor,
            Color zenithColor,
            Color sunColor,
            bool sunEnabled,
            Color lightColor,
            Color ambientColor,
            Color fillLightColor)
        {
            LightDirection = lightDirection;
            SunElevationDegrees = sunElevationDegrees;
            HorizonColor = horizonColor;
            ZenithColor = zenithColor;
            SunColor = sunColor;
            SunEnabled = sunEnabled;
            LightColor = lightColor;
            AmbientColor = ambientColor;
            FillLightColor = fillLightColor;
        }

        /// <summary>Direction the key light travels, following <see cref="PixelPostProcessSettings.LightDirection"/> semantics (from the sun toward the scene). Below the horizon it comes from a virtual moon placed opposite the sun.</summary>
        public Vector3 LightDirection { get; }

        /// <summary>Sun elevation above the horizon in degrees. Negative below the horizon.</summary>
        public float SunElevationDegrees { get; }

        /// <summary>Sky gradient color at the horizon.</summary>
        public Color HorizonColor { get; }

        /// <summary>Sky gradient color at the zenith.</summary>
        public Color ZenithColor { get; }

        /// <summary>Sun disc and halo color, faded to black as the disc drops to the horizon.</summary>
        public Color SunColor { get; }

        /// <summary>Whether the sun disc should be drawn. False below the horizon.</summary>
        public bool SunEnabled { get; }

        /// <summary>Key light color, dipped to black across the horizon crossing to hide the direction flip.</summary>
        public Color LightColor { get; }

        /// <summary>Ambient light color, the playable floor that keeps night from going pitch black.</summary>
        public Color AmbientColor { get; }

        /// <summary>Fill light color.</summary>
        public Color FillLightColor { get; }
    }

    /// <summary>
    /// Pure mapping from a caller-supplied normalized time of day to scene lighting.
    /// The engine owns no clock. Feed it game time (for an MMO, server-replicated time)
    /// and write the result to a scene with <see cref="Apply"/> each frame.
    /// </summary>
    public static class SunCycle
    {
        private const float Deg2Rad = MathF.PI / 180f;
        private const float Rad2Deg = 180f / MathF.PI;

        /// <summary>Evaluates the lighting state for a time of day (0 is midnight, 0.5 is solar noon, any float wraps).</summary>
        public static SunCycleState Evaluate(float timeOfDay, SunCycleSettings settings)
        {
            float t = timeOfDay - MathF.Floor(timeOfDay);
            float h = (t - 0.5f) * MathF.Tau;
            float lat = settings.LatitudeDegrees * Deg2Rad;
            float dec = settings.SolarDeclinationDegrees * Deg2Rad;
            float sinEl = Math.Clamp(
                MathF.Sin(lat) * MathF.Sin(dec) + MathF.Cos(lat) * MathF.Cos(dec) * MathF.Cos(h),
                -1f, 1f);
            float el = MathF.Asin(sinEl);
            float cosEl = MathF.Cos(el);
            float cosLat = MathF.Cos(lat);

            // Azimuth clockwise from north. Degenerate at the zenith and at the poles,
            // where the hour angle itself is the only meaningful sweep.
            float az;
            if (cosEl < 1e-5f || MathF.Abs(cosLat) < 1e-5f)
            {
                az = h;
            }
            else
            {
                float cosAz = Math.Clamp(
                    (MathF.Sin(dec) - MathF.Sin(lat) * sinEl) / (cosLat * cosEl), -1f, 1f);
                az = MathF.Acos(cosAz);
                if (MathF.Sin(h) > 0f) az = MathF.Tau - az;
            }
            az += settings.HeadingDegrees * Deg2Rad;

            // Y-up world, north is -Z, east is +X.
            var sunToward = new Vector3(MathF.Sin(az) * cosEl, sinEl, -MathF.Cos(az) * cosEl);
            float elDeg = el * Rad2Deg;

            // Below the horizon a virtual moon opposite the sun keeps a downward key light.
            // The key dips to zero at the crossing so the 180 degree azimuth flip is invisible.
            Vector3 lightDir = elDeg > 0f ? -sunToward : sunToward;

            float twilightBand = MathF.Max(1e-3f, settings.TwilightStartElevationDegrees);
            float nightBand = MathF.Max(1e-3f, MathF.Abs(settings.NightFullElevationDegrees));
            SunCyclePalette from = settings.DuskPalette;
            SunCyclePalette to;
            float s;
            if (elDeg >= 0f)
            {
                to = settings.DayPalette;
                s = MathUtil.SmoothStep(0f, twilightBand, elDeg);
            }
            else
            {
                to = settings.NightPalette;
                s = MathUtil.SmoothStep(0f, nightBand, -elDeg);
            }

            Color horizon = Color.Lerp(from.HorizonColor, to.HorizonColor, s);
            Color zenith = Color.Lerp(from.ZenithColor, to.ZenithColor, s);
            Color ambient = Color.Lerp(from.AmbientColor, to.AmbientColor, s);
            Color fill = Color.Lerp(from.FillColor, to.FillColor, s);
            Color key = Color.Lerp(from.LightColor, to.LightColor, s);
            Color sun = Color.Lerp(from.SunColor, to.SunColor, s);

            float keyDip = MathUtil.SmoothStep(
                0f, MathF.Max(1e-3f, settings.HorizonKeyDipDegrees), MathF.Abs(elDeg));
            key = key.ScaleRgb(keyDip);

            bool sunEnabled = elDeg > 0f;
            float discFade = MathUtil.SmoothStep(
                0f, MathF.Max(1e-3f, settings.SunDiscFadeElevationDegrees), elDeg);
            sun = sun.ScaleRgb(discFade);

            return new SunCycleState(lightDir, elDeg, horizon, zenith, sun, sunEnabled, key, ambient, fill);
        }

        /// <summary>
        /// Writes a state to the scene's lighting and sky settings. Touches exactly the key light
        /// direction and color, ambient, fill color, sky gradient, sun disc color, and sun disc
        /// visibility. Leaves Sky.Enabled, the anchor, halo shape, radius, and the fill direction
        /// to the caller.
        /// </summary>
        public static void Apply(in SunCycleState state, PixelPostProcessSettings post)
        {
            throw new NotImplementedException();
        }
    }
}
