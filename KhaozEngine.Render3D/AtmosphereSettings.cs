using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Physical atmosphere coefficients and art controls. Distances and coefficients use kilometres.
/// Defaults describe Earth with rendering disabled. Call <see cref="Validate"/> before using edited settings.</summary>
public sealed class AtmosphereSettings
{
    /// <summary>Enables the physical atmosphere. False preserves the existing sky and lighting path.</summary>
    public bool Enabled { get; set; }

    /// <summary>Planet surface radius in kilometres.</summary>
    public float PlanetRadiusKm { get; set; } = 6360f;
    /// <summary>Atmosphere thickness above the planet surface in kilometres.</summary>
    public float AtmosphereHeightKm { get; set; } = 100f;
    /// <summary>RGB Rayleigh scattering coefficients per kilometre at the surface.</summary>
    public Vector3 RayleighScatteringPerKm { get; set; } = new(.005802f, .013558f, .0331f);
    /// <summary>Rayleigh exponential density scale height in kilometres.</summary>
    public float RayleighScaleHeightKm { get; set; } = 8f;
    /// <summary>Mie scattering coefficient per kilometre at the surface.</summary>
    public float MieScatteringPerKm { get; set; } = .003996f;
    /// <summary>Mie extinction coefficient per kilometre, at least the scattering coefficient.</summary>
    public float MieExtinctionPerKm { get; set; } = .004440f;
    /// <summary>Mie exponential density scale height in kilometres.</summary>
    public float MieScaleHeightKm { get; set; } = 1.2f;
    /// <summary>Mie phase anisotropy, strictly between -1 and 1.</summary>
    public float MieAnisotropy { get; set; } = .8f;
    /// <summary>RGB ozone absorption coefficients per kilometre at the ozone layer's peak.</summary>
    public Vector3 OzoneAbsorptionPerKm { get; set; } = new(.000650f, .001881f, .000085f);
    /// <summary>Height of the ozone tent's centre in kilometres above the surface.</summary>
    public float OzoneCenterKm { get; set; } = 25f;
    /// <summary>Full width of the ozone density tent in kilometres. The Earth preset spans 10 to 40 km.</summary>
    public float OzoneWidthKm { get; set; } = 30f;
    /// <summary>Ground reflection fraction from 0 through 1.</summary>
    public float GroundAlbedo { get; set; } = .3f;
    /// <summary>Non-negative solar illuminance in engine Lambert units. The 2.7 art scale exposes
    /// the clear noon sky at exposure 1 with ACES while retaining headroom on white receivers.</summary>
    public float SunIlluminance { get; set; } = 2.7f;
    /// <summary>Non-negative moon illuminance as a fraction of solar illuminance. The 0.04 default
    /// retains a visible moon key without lifting the calibrated night sky into daylight.</summary>
    public float MoonIlluminanceFraction { get; set; } = .04f;
    /// <summary>Solar disc angular radius in radians, greater than zero and at most .1.</summary>
    public float SunAngularRadiusRadians { get; set; } = .004675f;

    /// <summary>World units per kilometre. Defaults to metre-sized units with absolute Y = 0 at the surface.</summary>
    public float WorldUnitsPerKilometre { get; set; } = 1000f;
    /// <summary>Non-negative multiplier on the distance used for aerial attenuation.</summary>
    public float AerialDistanceScale { get; set; } = 20f;
    /// <summary>Non-negative world distance over which aerial attenuation fades in from the camera. The optical
    /// distance d becomes d * d / (d + onset), so near geometry stays clear while distant fog is reduced by at
    /// most the onset. Zero keeps the linear distance.</summary>
    public float AerialOnsetDistance { get; set; }
    /// <summary>Non-negative multiplier on ambient lighting only.</summary>
    public float AmbientScale { get; set; } = 1f;
    /// <summary>Non-negative multiplier on fill lighting only.</summary>
    public float FillScale { get; set; } = 1f;
    /// <summary>Non-negative ambient lighting floor carrying NightTint at every time of day.
    /// The 0.05 default keeps unlit receivers legible at exposure 1 without competing with the moon key.</summary>
    public float NightAmbientFloor { get; set; } = .05f;
    /// <summary>Non-negative sky brightness floor carrying NightTint at every time of day.
    /// The 0.09 default exposes a dark blue sky above display black at exposure 1 with ACES,
    /// including moonless directions.</summary>
    public float NightSkyFloor { get; set; } = .09f;
    /// <summary>Additional non-negative haze density per kilometre. Zero keeps mist opt-in.</summary>
    public float HazeDensityPerKm { get; set; }
    /// <summary>Haze exponential density scale height in kilometres.</summary>
    public float HazeScaleHeightKm { get; set; } = .25f;
    /// <summary>Non-negative RGB tint shared by the daytime sky, fog and lighting.</summary>
    public Vector3 DayTint { get; set; } = Vector3.One;
    /// <summary>Non-negative RGB tint shared by the dusk sky, fog and lighting.</summary>
    public Vector3 DuskTint { get; set; } = Vector3.One;
    /// <summary>Non-negative RGB tint shared by the night sky, fog and lighting. The (0.45,0.65,1)
    /// default gives the calibrated night floor a dark blue hue without a separate sky multiplier.</summary>
    public Vector3 NightTint { get; set; } = new(.45f, .65f, 1f);

    /// <summary>Creates independent Earth settings with physical atmosphere rendering disabled.</summary>
    public static AtmosphereSettings Earth() => new();

    /// <summary>Maps absolute world Y to kilometres above the surface, without curvature or origin offsets.
    /// Negative altitudes remain negative. Density evaluation clamps underground density to surface density.</summary>
    public float HeightKm(float absoluteWorldY) => absoluteWorldY / WorldUnitsPerKilometre;

    /// <summary>Copies every setting by value for a retained cache snapshot. All members are value types.</summary>
    public AtmosphereSettings Clone() => (AtmosphereSettings)MemberwiseClone();

    /// <summary>Compares all current values with a retained <see cref="Clone"/> or another settings bag.
    /// This comparison allocates nothing. A live bag must be copied before retaining its previous values.</summary>
    public bool ValueEquals(AtmosphereSettings? other) => other is not null
        && Enabled == other.Enabled
        && PlanetRadiusKm.Equals(other.PlanetRadiusKm)
        && AtmosphereHeightKm.Equals(other.AtmosphereHeightKm)
        && RayleighScatteringPerKm.Equals(other.RayleighScatteringPerKm)
        && RayleighScaleHeightKm.Equals(other.RayleighScaleHeightKm)
        && MieScatteringPerKm.Equals(other.MieScatteringPerKm)
        && MieExtinctionPerKm.Equals(other.MieExtinctionPerKm)
        && MieScaleHeightKm.Equals(other.MieScaleHeightKm)
        && MieAnisotropy.Equals(other.MieAnisotropy)
        && OzoneAbsorptionPerKm.Equals(other.OzoneAbsorptionPerKm)
        && OzoneCenterKm.Equals(other.OzoneCenterKm)
        && OzoneWidthKm.Equals(other.OzoneWidthKm)
        && GroundAlbedo.Equals(other.GroundAlbedo)
        && SunIlluminance.Equals(other.SunIlluminance)
        && MoonIlluminanceFraction.Equals(other.MoonIlluminanceFraction)
        && SunAngularRadiusRadians.Equals(other.SunAngularRadiusRadians)
        && WorldUnitsPerKilometre.Equals(other.WorldUnitsPerKilometre)
        && AerialDistanceScale.Equals(other.AerialDistanceScale)
        && AerialOnsetDistance.Equals(other.AerialOnsetDistance)
        && AmbientScale.Equals(other.AmbientScale)
        && FillScale.Equals(other.FillScale)
        && NightAmbientFloor.Equals(other.NightAmbientFloor)
        && NightSkyFloor.Equals(other.NightSkyFloor)
        && HazeDensityPerKm.Equals(other.HazeDensityPerKm)
        && HazeScaleHeightKm.Equals(other.HazeScaleHeightKm)
        && DayTint.Equals(other.DayTint)
        && DuskTint.Equals(other.DuskTint)
        && NightTint.Equals(other.NightTint);

    /// <summary>Compares exactly the physical inputs shared by static transport and unit-source lighting
    /// tables. Source brightness, art controls, enablement and world mapping do not affect these tables.</summary>
    internal bool PhysicalValueEquals(AtmosphereSettings? other) => other is not null
        && PlanetRadiusKm.Equals(other.PlanetRadiusKm)
        && AtmosphereHeightKm.Equals(other.AtmosphereHeightKm)
        && RayleighScatteringPerKm.Equals(other.RayleighScatteringPerKm)
        && RayleighScaleHeightKm.Equals(other.RayleighScaleHeightKm)
        && MieScatteringPerKm.Equals(other.MieScatteringPerKm)
        && MieExtinctionPerKm.Equals(other.MieExtinctionPerKm)
        && MieScaleHeightKm.Equals(other.MieScaleHeightKm)
        && MieAnisotropy.Equals(other.MieAnisotropy)
        && OzoneAbsorptionPerKm.Equals(other.OzoneAbsorptionPerKm)
        && OzoneCenterKm.Equals(other.OzoneCenterKm)
        && OzoneWidthKm.Equals(other.OzoneWidthKm)
        && GroundAlbedo.Equals(other.GroundAlbedo);

    /// <summary>Rejects non-finite values, invalid physical lengths, negative coefficients or art controls,
    /// extinction below scattering, and out-of-range anisotropy, albedo or solar radius.</summary>
    public void Validate()
    {
        Positive(PlanetRadiusKm, nameof(PlanetRadiusKm));
        Positive(AtmosphereHeightKm, nameof(AtmosphereHeightKm));
        NonNegative(RayleighScatteringPerKm, nameof(RayleighScatteringPerKm));
        Positive(RayleighScaleHeightKm, nameof(RayleighScaleHeightKm));
        NonNegative(MieScatteringPerKm, nameof(MieScatteringPerKm));
        NonNegative(MieExtinctionPerKm, nameof(MieExtinctionPerKm));
        if (MieExtinctionPerKm < MieScatteringPerKm)
            throw new ArgumentOutOfRangeException(nameof(MieExtinctionPerKm),
                "Mie extinction must be at least the scattering coefficient.");
        Positive(MieScaleHeightKm, nameof(MieScaleHeightKm));
        if (!float.IsFinite(MieAnisotropy) || MieAnisotropy <= -1f || MieAnisotropy >= 1f)
            throw new ArgumentOutOfRangeException(nameof(MieAnisotropy),
                "Mie anisotropy must be finite and strictly between -1 and 1.");
        NonNegative(OzoneAbsorptionPerKm, nameof(OzoneAbsorptionPerKm));
        Positive(OzoneCenterKm, nameof(OzoneCenterKm));
        Positive(OzoneWidthKm, nameof(OzoneWidthKm));
        if (!float.IsFinite(GroundAlbedo) || GroundAlbedo < 0f || GroundAlbedo > 1f)
            throw new ArgumentOutOfRangeException(nameof(GroundAlbedo),
                "Ground albedo must be finite and from 0 through 1.");
        NonNegative(SunIlluminance, nameof(SunIlluminance));
        NonNegative(MoonIlluminanceFraction, nameof(MoonIlluminanceFraction));
        if (!float.IsFinite(SunAngularRadiusRadians) || SunAngularRadiusRadians <= 0f || SunAngularRadiusRadians > .1f)
            throw new ArgumentOutOfRangeException(nameof(SunAngularRadiusRadians),
                "Solar angular radius must be finite, greater than zero and at most .1 radians.");
        Positive(WorldUnitsPerKilometre, nameof(WorldUnitsPerKilometre));
        NonNegative(AerialDistanceScale, nameof(AerialDistanceScale));
        NonNegative(AerialOnsetDistance, nameof(AerialOnsetDistance));
        NonNegative(AmbientScale, nameof(AmbientScale));
        NonNegative(FillScale, nameof(FillScale));
        NonNegative(NightAmbientFloor, nameof(NightAmbientFloor));
        NonNegative(NightSkyFloor, nameof(NightSkyFloor));
        NonNegative(HazeDensityPerKm, nameof(HazeDensityPerKm));
        Positive(HazeScaleHeightKm, nameof(HazeScaleHeightKm));
        NonNegative(DayTint, nameof(DayTint));
        NonNegative(DuskTint, nameof(DuskTint));
        NonNegative(NightTint, nameof(NightTint));
    }

    static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(name, "Atmosphere lengths must be finite and positive.");
    }

    static void NonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(name, "Atmosphere coefficients and art controls must be finite and non-negative.");
    }

    static void NonNegative(Vector3 value, string name)
    {
        NonNegative(value.X, name);
        NonNegative(value.Y, name);
        NonNegative(value.Z, name);
    }
}
