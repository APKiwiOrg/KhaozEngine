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
    /// How the key light behaves at night (below the sun's horizon). Selects the night track on
    /// <see cref="SunCycleSettings.NightKey"/>. <see cref="AntiSolarMoon"/> is the DEFAULT so existing scenes are
    /// byte-stable (the historical virtual-moon behaviour is unchanged out of the box).
    /// </summary>
    public enum NightKeyMode
    {
        /// <summary>The legacy virtual moon: below the horizon the key light comes from a point exactly OPPOSITE the
        /// sun (<c>-sunToward</c> flips to <c>+sunToward</c>), dipped to black across the crossing so the 180-degree
        /// azimuth flip is hidden. The DEFAULT: today's behaviour, byte-stable. The disc is hidden at night.</summary>
        AntiSolarMoon,

        /// <summary>Keyless nights: below the horizon the key color is BLACK and the light direction stays the sun's
        /// TRUE travel direction (<c>-sunToward</c>, no anti-solar flip), so the direction is continuous through the
        /// crossing. Above the horizon this is identical to <see cref="AntiSolarMoon"/> (the same day/dusk key with the
        /// horizon dip). For games that want night lit by ambient + fill only, with no cast key at night.</summary>
        None,

        /// <summary>A real, decoupled moon track: a second body running the same arc math as the sun with its own hour
        /// offset (<see cref="SunCycleSettings.MoonHourOffset"/>, default 12h = opposition), its own declination
        /// (<see cref="SunCycleSettings.MoonDeclinationDegrees"/>), its own key color
        /// (<see cref="SunCycleSettings.MoonKeyColor"/>) and its own horizon dip
        /// (<see cref="SunCycleSettings.MoonHorizonKeyDipDegrees"/>). The key is the sun while the sun is up, else the
        /// moon while the moon is up, else black; each body fades to black at its OWN crossing, and the direction is
        /// continuous within each body. The disc follows the
        /// active body (sun when up, else moon when up) and can show a decorative moon that casts no key. Kills the
        /// pre-dawn 180-degree flip: below the horizon the sun is simply never the key.
        /// <para>The source switch is only through BLACK when the two crossings COINCIDE, which is a property of the
        /// configuration rather than of this mode. The default 12h <see cref="SunCycleSettings.MoonHourOffset"/>
        /// lines them up only while both bodies are up for about 12 hours: the equator, or a zero declination.
        /// At the shipped defaults (latitude 35, declination 15) the day is longer than that, so the moon is already
        /// about 17 degrees up when the sun sets, well clear of its 2-degree
        /// <see cref="SunCycleSettings.MoonHorizonKeyDipDegrees"/> band. The key then jumps from black to the moon's
        /// full strength in one frame and swings more than 90 degrees in azimuth: a visible dusk/dawn pop, not a
        /// handover. Until the cross-fade lands (issue #223), either keep the day near 12 hours, or give the moon a
        /// black <see cref="SunCycleSettings.MoonKeyColor"/> so no key ever hands over (the decorative-moon setup).
        /// Widening the dip hides the pop but eats a large shadowless band at a long-day latitude.</para></summary>
        Moon,
    }

    /// <summary>
    /// Which celestial body owns the key light and the single disc slot this frame (see
    /// <see cref="SunCycleState.ActiveSource"/>). <see cref="Sun"/> while the sun is above the horizon;
    /// <see cref="Moon"/> only under <see cref="NightKeyMode.Moon"/> while the sun is down and the moon is up;
    /// <see cref="None"/> otherwise (a keyless/discless stretch of night). The key COLOR can still be black while a
    /// body is the source (a decorative moon casts no key), so read <see cref="SunCycleState.LightColor"/> for the
    /// key strength and this for the active body.
    /// </summary>
    public enum KeyLightSource
    {
        /// <summary>No body owns the slot: the disc is hidden and the key is black (ambient/fill only).</summary>
        None,
        /// <summary>The sun owns the key + disc (it is above the horizon).</summary>
        Sun,
        /// <summary>The moon owns the key + disc (<see cref="NightKeyMode.Moon"/>, sun down, moon up).</summary>
        Moon,
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

        /// <summary>Elevation in degrees over which the sun disc color fades in from the horizon. The moon disc reuses this width against the moon's elevation.</summary>
        public float SunDiscFadeElevationDegrees { get; set; } = 4f;

        /// <summary>Which night track the key light follows below the sun's horizon. Default
        /// <see cref="NightKeyMode.AntiSolarMoon"/> (the legacy virtual moon), so existing scenes are byte-stable.</summary>
        public NightKeyMode NightKey { get; set; } = NightKeyMode.AntiSolarMoon;

        /// <summary>Moon time offset from the sun, in HOURS on a 24-hour day (the moon runs the same arc math as the
        /// sun, shifted by this much). Default <c>12</c> = opposition (the moon rises as the sun sets), which lines
        /// the two crossings up so the sun-to-moon handover happens through black ONLY at a day length near 12 hours
        /// (see <see cref="NightKeyMode.Moon"/> for what happens away from it). Only used under
        /// <see cref="NightKeyMode.Moon"/>.</summary>
        public float MoonHourOffset { get; set; } = 12f;

        /// <summary>The moon's own declination in degrees (its arc's seasonal tilt), independent of
        /// <see cref="SolarDeclinationDegrees"/> so the moon track is genuinely decoupled from the sun's. Default
        /// <c>15</c>. Only used under <see cref="NightKeyMode.Moon"/>.</summary>
        public float MoonDeclinationDegrees { get; set; } = 15f;

        /// <summary>The moon's key light color (magnitude = intensity), independent of the disc color so a game can
        /// have a decorative moon that casts nothing (set this to black, keep <see cref="MoonDiscColor"/> bright).
        /// Default a dim cool moonlight. Only used under <see cref="NightKeyMode.Moon"/>.</summary>
        public Color MoonKeyColor { get; set; } = new Color(0.16f, 0.22f, 0.38f, 1f);

        /// <summary>The moon disc + halo color (the single disc slot, when the moon owns it), independent of
        /// <see cref="MoonKeyColor"/>. Default a pale silver. Only used under <see cref="NightKeyMode.Moon"/>.</summary>
        public Color MoonDiscColor { get; set; } = new Color(0.85f, 0.88f, 0.95f, 1f);

        /// <summary>Half-width in degrees of the MOON's key-light dip at its own horizon crossing, so the moon key
        /// fades to black as the moon sets/rises (the sun's counterpart is <see cref="HorizonKeyDipDegrees"/>).
        /// Default <c>2</c>. Only used under <see cref="NightKeyMode.Moon"/>.</summary>
        public float MoonHorizonKeyDipDegrees { get; set; } = 2f;

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
        /// <summary>Creates a state from its evaluated components. The moon/source fields
        /// (<paramref name="moonElevationDegrees"/>, <paramref name="moonDirection"/>, <paramref name="activeSource"/>,
        /// <paramref name="discDirectionOverride"/>) are optional additive fields for the real-moon night track; the
        /// legacy modes leave them at their defaults.</summary>
        public SunCycleState(
            Vector3 lightDirection,
            float sunElevationDegrees,
            Color horizonColor,
            Color zenithColor,
            Color sunColor,
            bool sunEnabled,
            Color lightColor,
            Color ambientColor,
            Color fillLightColor,
            float moonElevationDegrees = 0f,
            Vector3 moonDirection = default,
            KeyLightSource activeSource = KeyLightSource.Sun,
            Vector3? discDirectionOverride = null)
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
            MoonElevationDegrees = moonElevationDegrees;
            MoonDirection = moonDirection;
            ActiveSource = activeSource;
            DiscDirectionOverride = discDirectionOverride;
        }

        /// <summary>Direction the key light travels, following <see cref="PixelPostProcessSettings.LightDirection"/>
        /// semantics (from the light toward the scene). Under <see cref="NightKeyMode.AntiSolarMoon"/> it flips to the
        /// anti-solar point below the horizon; under <see cref="NightKeyMode.None"/> it stays the sun's true direction
        /// (the key just goes black); under <see cref="NightKeyMode.Moon"/> it is the moon's travel direction while the
        /// moon owns the key. Continuous within each source (any switch happens through a black key).</summary>
        public Vector3 LightDirection { get; }

        /// <summary>Sun elevation above the horizon in degrees. Negative below the horizon.</summary>
        public float SunElevationDegrees { get; }

        /// <summary>Sky gradient color at the horizon.</summary>
        public Color HorizonColor { get; }

        /// <summary>Sky gradient color at the zenith.</summary>
        public Color ZenithColor { get; }

        /// <summary>The single disc slot's color (disc + halo), faded to black as the active body drops to the horizon.
        /// Carries the sun's disc color while the sun is up, else the moon's disc color while the moon owns the slot
        /// (<see cref="NightKeyMode.Moon"/>). Independent of <see cref="LightColor"/>, so a decorative moon can show a
        /// bright disc while casting a black key.</summary>
        public Color SunColor { get; }

        /// <summary>Whether the disc should be drawn. True while a body owns the slot (the sun above the horizon, or
        /// the moon under <see cref="NightKeyMode.Moon"/>); false in a keyless/discless stretch of night.</summary>
        public bool SunEnabled { get; }

        /// <summary>Key light color. Dipped to black across a body's own horizon crossing to hide the direction change,
        /// and black outright during a keyless night (<see cref="NightKeyMode.None"/> below the horizon, or
        /// <see cref="NightKeyMode.Moon"/> with no body up / a decorative black-key moon).</summary>
        public Color LightColor { get; }

        /// <summary>Ambient light color, the playable floor that keeps night from going pitch black.</summary>
        public Color AmbientColor { get; }

        /// <summary>Fill light color.</summary>
        public Color FillLightColor { get; }

        /// <summary>Moon elevation above the horizon in degrees (negative below). Always evaluated (the moon arc runs
        /// in every mode); only DRIVES the key/disc under <see cref="NightKeyMode.Moon"/>.</summary>
        public float MoonElevationDegrees { get; }

        /// <summary>Direction the MOON's light travels (from the moon toward the scene), following the same semantics
        /// as <see cref="LightDirection"/>. Always evaluated; equals <see cref="LightDirection"/> while the moon owns
        /// the key.</summary>
        public Vector3 MoonDirection { get; }

        /// <summary>Which body owns the key + disc slot this frame. Drives the disc handover and tells a custom sink
        /// whether it is looking at a sun-lit, moon-lit, or keyless frame.</summary>
        public KeyLightSource ActiveSource { get; }

        /// <summary>Direction TO the active disc body when the disc is NOT the sun (i.e. the moon owns the slot):
        /// the world-space direction the sky's sun-disc override should point at. <c>null</c> when the sun owns the
        /// slot (the disc derives from the key light) or nothing does. <see cref="SunCycle.Apply"/> writes it straight
        /// to <see cref="SkySettings.SunDirectionOverride"/>.</summary>
        public Vector3? DiscDirectionOverride { get; }
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
        private static readonly Color Black = new(0f, 0f, 0f, 1f);

        /// <summary>Evaluates the lighting state for a time of day (0 is midnight, 0.5 is solar noon, any float wraps).</summary>
        public static SunCycleState Evaluate(float timeOfDay, SunCycleSettings settings)
        {
            // Sun arc (the historical computation, now shared with the moon via SolarDirection).
            Vector3 sunToward = SolarDirection(
                timeOfDay, settings.LatitudeDegrees, settings.SolarDeclinationDegrees, settings.HeadingDegrees, out float elDeg);

            // Moon arc: the same math, offset in time and with its own declination. Always evaluated so the state's
            // moon fields are populated in every mode; only the Moon night track consumes it for the key/disc.
            Vector3 moonToward = SolarDirection(
                timeOfDay + settings.MoonHourOffset / 24f, settings.LatitudeDegrees, settings.MoonDeclinationDegrees,
                settings.HeadingDegrees, out float moonElDeg);
            Vector3 moonLightDir = -moonToward;

            // Elevation-keyed palette blend (identical in every mode: this is the SKY/ambient/fill, driven by the sun).
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
            Color baseKey = Color.Lerp(from.LightColor, to.LightColor, s);
            Color baseSun = Color.Lerp(from.SunColor, to.SunColor, s);

            // The sun's own key dip + disc fade at its horizon crossing.
            float sunKeyDip = MathUtil.SmoothStep(
                0f, MathF.Max(1e-3f, settings.HorizonKeyDipDegrees), MathF.Abs(elDeg));
            float sunDiscFade = MathUtil.SmoothStep(
                0f, MathF.Max(1e-3f, settings.SunDiscFadeElevationDegrees), elDeg);

            // The sun-owned disc (shared by AntiSolarMoon and None; the sun's disc is hidden below the horizon).
            Color sunDisc = baseSun.ScaleRgb(sunDiscFade);
            bool sunUp = elDeg > 0f;

            Vector3 lightDir;
            Color key;
            Color disc;
            bool discEnabled;
            Vector3? discOverride;
            KeyLightSource source;

            switch (settings.NightKey)
            {
                case NightKeyMode.None:
                    // Keyless nights: direction stays the sun's TRUE travel dir (no anti-solar flip); the key just
                    // goes black below the horizon. Above the horizon this is identical to the legacy path.
                    lightDir = -sunToward;
                    key = sunUp ? baseKey.ScaleRgb(sunKeyDip) : Black;
                    disc = sunDisc;
                    discEnabled = sunUp;
                    discOverride = null;
                    source = sunUp ? KeyLightSource.Sun : KeyLightSource.None;
                    break;

                case NightKeyMode.Moon:
                    if (sunUp)
                    {
                        // Sun owns the key + disc.
                        lightDir = -sunToward;
                        key = baseKey.ScaleRgb(sunKeyDip);
                        disc = sunDisc;
                        discEnabled = true;
                        discOverride = null;
                        source = KeyLightSource.Sun;
                    }
                    else if (moonElDeg > 0f)
                    {
                        // Moon owns the key + disc: its own key color/dip and its own disc color/fade, disc pointed at
                        // the moon. The key can be black (decorative moon) while the disc stays visible.
                        float moonKeyDip = MathUtil.SmoothStep(
                            0f, MathF.Max(1e-3f, settings.MoonHorizonKeyDipDegrees), MathF.Abs(moonElDeg));
                        float moonDiscFade = MathUtil.SmoothStep(
                            0f, MathF.Max(1e-3f, settings.SunDiscFadeElevationDegrees), moonElDeg);
                        lightDir = moonLightDir;
                        key = settings.MoonKeyColor.ScaleRgb(moonKeyDip);
                        disc = settings.MoonDiscColor.ScaleRgb(moonDiscFade);
                        discEnabled = true;
                        discOverride = moonToward;   // direction TO the moon (the moon light travels -moonToward)
                        source = KeyLightSource.Moon;
                    }
                    else
                    {
                        // Neither body up: keyless, discless. Hold the sun's true direction for continuity (the key is
                        // black, so a switch through this state is invisible).
                        lightDir = -sunToward;
                        key = Black;
                        disc = Black;
                        discEnabled = false;
                        discOverride = null;
                        source = KeyLightSource.None;
                    }
                    break;

                default: // NightKeyMode.AntiSolarMoon - the legacy virtual moon (byte-identical to the historical path).
                    lightDir = sunUp ? -sunToward : sunToward;
                    key = baseKey.ScaleRgb(sunKeyDip);
                    disc = sunDisc;
                    discEnabled = sunUp;
                    discOverride = null;
                    source = sunUp ? KeyLightSource.Sun : KeyLightSource.None;
                    break;
            }

            return new SunCycleState(
                lightDir, elDeg, horizon, zenith, disc, discEnabled, key, ambient, fill,
                moonElDeg, moonLightDir, source, discOverride);
        }

        /// <summary>
        /// The unit direction TOWARD a body on the analytic sun arc for a normalized <paramref name="timeOfDay"/>
        /// (0 = midnight, 0.5 = solar noon, any value wraps), given the observer <paramref name="latitudeDegrees"/>,
        /// the body's <paramref name="declinationDegrees"/>, and the path <paramref name="headingDegrees"/>. Both the
        /// sun and the (offset, own-declination) moon run through this, so their arcs are identical geometry. Y-up
        /// world, north is -Z, east is +X; <paramref name="elevationDegrees"/> is the body's elevation above the
        /// horizon (negative below).
        /// </summary>
        public static Vector3 SolarDirection(
            float timeOfDay, float latitudeDegrees, float declinationDegrees, float headingDegrees, out float elevationDegrees)
        {
            float t = timeOfDay - MathF.Floor(timeOfDay);
            float h = (t - 0.5f) * MathF.Tau;
            float lat = latitudeDegrees * Deg2Rad;
            float dec = declinationDegrees * Deg2Rad;
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
            az += headingDegrees * Deg2Rad;

            // Y-up world, north is -Z, east is +X.
            elevationDegrees = el * Rad2Deg;
            return new Vector3(MathF.Sin(az) * cosEl, sinEl, -MathF.Cos(az) * cosEl);
        }

        /// <summary>
        /// Writes a state to the scene's lighting and sky settings. Touches exactly the key light
        /// direction and color, ambient, fill color, sky gradient, sun disc color, sun disc
        /// visibility, and the sky's sun-direction override (pointed at the moon when the moon owns
        /// the disc, cleared to null when the sun does). Leaves Sky.Enabled, the anchor, halo shape,
        /// radius, and the fill direction to the caller.
        /// </summary>
        public static void Apply(in SunCycleState state, PixelPostProcessSettings post)
        {
            post.LightDirection = state.LightDirection;
            post.LightColor = state.LightColor;
            post.AmbientColor = state.AmbientColor;
            post.FillLightColor = state.FillLightColor;
            post.Sky.HorizonColor = state.HorizonColor;
            post.Sky.ZenithColor = state.ZenithColor;
            post.Sky.SunColor = state.SunColor;
            post.Sky.SunEnabled = state.SunEnabled;
            post.Sky.SunDirectionOverride = state.DiscDirectionOverride;
        }
    }
}
