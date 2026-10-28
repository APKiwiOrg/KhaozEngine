using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapRigidFrame(Color Tint, Material Material, float Dissolve, float EdgeWidth,
    Color EdgeColour, bool InvertShadowDissolve, float DissolveComplement, MotionKey Motion);
public readonly record struct MapLightFrame(Color Colour, float Radius, float Intensity, LightShadow Shadow, string? FixtureOwnerId);
public readonly record struct MapWaterFrame(MapFrozenWaterLook Look);
public readonly record struct MapFoliageFrame(MapFoliageFrameOptions Options, MapFoliageFrameState State, double TimeSeconds, int WindSeed);

/// <summary>All per-plane overrides, copied by value from the caller's mutable look.</summary>
public sealed record MapFrozenWaterLook
{
    // Keep the internal renderer copy out of record equality. Weak ownership follows the frozen prepared policy.
    static readonly ConditionalWeakTable<MapFrozenWaterLook, WaterLook> PreparedLooks = new();
    internal WaterLook PreparedLook => PreparedLooks.GetValue(this, static frozen => frozen.ToWaterLook());
    public WaterWaveSource? WaveSource { get; }
    public Color? DeepColor { get; }
    public Color? ShallowColor { get; }
    public Color? AbsorptionPerMetre { get; }
    public bool? UnderwaterSunlight { get; }
    public float? UnderwaterPenumbraPerMetre { get; }
    public bool? WaterColumnLight { get; }
    public Color? WaterColor { get; }
    public float? Clarity { get; }
    public float? ShallowDepth { get; }
    public float? Opacity { get; }
    public float? SwellAmplitude { get; }
    public float? SwellWavelength { get; }
    public float? SwellDirectionDegrees { get; }
    public float? SwellSpreadDegrees { get; }
    public float? SwellSteepness { get; }
    public float? SwellSpeed { get; }
    public float? SwellSeed { get; }
    public int? SwellComponents { get; }
    public float? ShoalingStrength { get; }
    public float? SurfStrength { get; }
    public float? WaveScale { get; }
    public float? WaveSpeed { get; }
    public float? NormalStrength { get; }
    public float? WaveWarpStrength { get; }
    public int? RippleComponents { get; }
    public float? RippleLacunarity { get; }
    public float? RippleGain { get; }
    public float? RippleSeed { get; }
    public float? VarianceToRoughness { get; }
    public float? DetailFadeDistance { get; }
    public float? DistantDetailScale { get; }
    public float? GlintStrength { get; }
    public float? GlintRoughness { get; }
    public float? GlintDistantRoughness { get; }
    public float? GlintExponent { get; }
    public Color? FoamColor { get; }
    public float? FoamStrength { get; }
    public float? FoamCrestCoverage { get; }
    public float? FoamShoreWidth { get; }
    public float? FoamPatternScale { get; }
    public float? ShoreFadeDistance { get; }
    MapFrozenWaterLook(WaterLook look)
    {
        WaveSource = look.WaveSource;
        DeepColor = look.DeepColor;
        ShallowColor = look.ShallowColor;
        AbsorptionPerMetre = look.AbsorptionPerMetre;
        UnderwaterSunlight = look.UnderwaterSunlight;
        UnderwaterPenumbraPerMetre = look.UnderwaterPenumbraPerMetre;
        WaterColumnLight = look.WaterColumnLight;
        WaterColor = look.WaterColor;
        Clarity = look.Clarity;
        ShallowDepth = look.ShallowDepth;
        Opacity = look.Opacity;
        SwellAmplitude = look.SwellAmplitude;
        SwellWavelength = look.SwellWavelength;
        SwellDirectionDegrees = look.SwellDirectionDegrees;
        SwellSpreadDegrees = look.SwellSpreadDegrees;
        SwellSteepness = look.SwellSteepness;
        SwellSpeed = look.SwellSpeed;
        SwellSeed = look.SwellSeed;
        SwellComponents = look.SwellComponents;
        ShoalingStrength = look.ShoalingStrength;
        SurfStrength = look.SurfStrength;
        WaveScale = look.WaveScale;
        WaveSpeed = look.WaveSpeed;
        NormalStrength = look.NormalStrength;
        WaveWarpStrength = look.WaveWarpStrength;
        RippleComponents = look.RippleComponents;
        RippleLacunarity = look.RippleLacunarity;
        RippleGain = look.RippleGain;
        RippleSeed = look.RippleSeed;
        VarianceToRoughness = look.VarianceToRoughness;
        DetailFadeDistance = look.DetailFadeDistance;
        DistantDetailScale = look.DistantDetailScale;
        GlintStrength = look.GlintStrength;
        GlintRoughness = look.GlintRoughness;
        GlintDistantRoughness = look.GlintDistantRoughness;
        GlintExponent = look.GlintExponent;
        FoamColor = look.FoamColor;
        FoamStrength = look.FoamStrength;
        FoamCrestCoverage = look.FoamCrestCoverage;
        FoamShoreWidth = look.FoamShoreWidth;
        FoamPatternScale = look.FoamPatternScale;
        ShoreFadeDistance = look.ShoreFadeDistance;
        PreparedLooks.Add(this, ToWaterLook());
    }
    public static MapFrozenWaterLook Capture(WaterLook look)
    { ArgumentNullException.ThrowIfNull(look); return new(look); }
    public WaterLook ToWaterLook() => new()
    {
        WaveSource = WaveSource,
        DeepColor = DeepColor,
        ShallowColor = ShallowColor,
        AbsorptionPerMetre = AbsorptionPerMetre,
        UnderwaterSunlight = UnderwaterSunlight,
        UnderwaterPenumbraPerMetre = UnderwaterPenumbraPerMetre,
        WaterColumnLight = WaterColumnLight,
        WaterColor = WaterColor,
        Clarity = Clarity,
        ShallowDepth = ShallowDepth,
        Opacity = Opacity,
        SwellAmplitude = SwellAmplitude,
        SwellWavelength = SwellWavelength,
        SwellDirectionDegrees = SwellDirectionDegrees,
        SwellSpreadDegrees = SwellSpreadDegrees,
        SwellSteepness = SwellSteepness,
        SwellSpeed = SwellSpeed,
        SwellSeed = SwellSeed,
        SwellComponents = SwellComponents,
        ShoalingStrength = ShoalingStrength,
        SurfStrength = SurfStrength,
        WaveScale = WaveScale,
        WaveSpeed = WaveSpeed,
        NormalStrength = NormalStrength,
        WaveWarpStrength = WaveWarpStrength,
        RippleComponents = RippleComponents,
        RippleLacunarity = RippleLacunarity,
        RippleGain = RippleGain,
        RippleSeed = RippleSeed,
        VarianceToRoughness = VarianceToRoughness,
        DetailFadeDistance = DetailFadeDistance,
        DistantDetailScale = DistantDetailScale,
        GlintStrength = GlintStrength,
        GlintRoughness = GlintRoughness,
        GlintDistantRoughness = GlintDistantRoughness,
        GlintExponent = GlintExponent,
        FoamColor = FoamColor,
        FoamStrength = FoamStrength,
        FoamCrestCoverage = FoamCrestCoverage,
        FoamShoreWidth = FoamShoreWidth,
        FoamPatternScale = FoamPatternScale,
        ShoreFadeDistance = ShoreFadeDistance,
    };
}

/// <summary>The caller's complete policy. Stable model values are resolved into registration by the producer.</summary>
public sealed class MapFrozenFoliageOptions
{
    public float DrawRadius { get; }
    public float? DensityRadius { get; }
    public float FadeBandWidth { get; }
    public float QualityDensity { get; }
    public float DistantDensity { get; }
    public bool CastsShadows { get; }
    public IReadOnlyDictionary<string, FoliageMotionProfile> MotionProfiles { get; }
    public IReadOnlyDictionary<string, bool> ModelCastsShadows { get; }
    public float InstanceFadeBandWidth { get; }
    public GroundCoverFadeMode FadeMode { get; }
    public bool UseGpuBatches { get; }
    public Vector2 WindDirection { get; }
    public float WindStrength { get; }
    public float WindSpeed { get; }
    public float WindSpatialFrequency { get; }
    public float WindFadeBladePixels { get; }
    public IReadOnlyList<FoliageInteractor> Interactors { get; }

    MapFrozenFoliageOptions(GroundCoverRenderOptions options)
    {
        DrawRadius = options.DrawRadius; DensityRadius = options.DensityRadius; FadeBandWidth = options.FadeBandWidth;
        QualityDensity = options.QualityDensity; DistantDensity = options.DistantDensity; CastsShadows = options.CastsShadows;
        MotionProfiles = new ReadOnlyDictionary<string, FoliageMotionProfile>(new Dictionary<string, FoliageMotionProfile>(options.MotionProfiles, StringComparer.Ordinal));
        ModelCastsShadows = new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool>(options.ModelCastsShadows, StringComparer.Ordinal));
        InstanceFadeBandWidth = options.InstanceFadeBandWidth; FadeMode = options.FadeMode; UseGpuBatches = options.UseGpuBatches;
        WindDirection = options.WindDirection; WindStrength = options.WindStrength; WindSpeed = options.WindSpeed;
        WindSpatialFrequency = options.WindSpatialFrequency; WindFadeBladePixels = options.WindFadeBladePixels;
        ArgumentNullException.ThrowIfNull(options.Interactors);
        if (options.Interactors.Count > 4) throw new ArgumentOutOfRangeException(nameof(options), "At most four foliage interactors are supported.");
        var interactors = options.Interactors.ToArray();
        foreach (var interactor in interactors) interactor.Validate();
        Interactors = Array.AsReadOnly(interactors);
    }
    public static MapFrozenFoliageOptions Capture(GroundCoverRenderOptions options)
    { ArgumentNullException.ThrowIfNull(options); return new(options); }
}

/// <summary>Only dynamic draw policy. Profiles and casting belong to model registration.</summary>
public readonly record struct MapFoliageFrameOptions(float DrawRadius, float? DensityRadius, float FadeBandWidth,
    float QualityDensity, float DistantDensity, float InstanceFadeBandWidth, GroundCoverFadeMode FadeMode,
    float WindFadeBladePixels)
{
    public static MapFoliageFrameOptions From(MapFrozenFoliageOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new(policy.DrawRadius, policy.DensityRadius, policy.FadeBandWidth, policy.QualityDensity,
            policy.DistantDensity, policy.InstanceFadeBandWidth, policy.FadeMode, policy.WindFadeBladePixels);
    }
}

public readonly record struct MapFoliageFrameState
{
    readonly int _interactorCount;
    public Vector2 WindDirection { get; init; }
    public float WindStrength { get; init; }
    public float WindSpeed { get; init; }
    public float WindSpatialFrequency { get; init; }
    public int InteractorCount
    {
        get => _interactorCount;
        init => _interactorCount = value is >= 0 and <= 4 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public FoliageInteractor I0 { get; init; }
    public FoliageInteractor I1 { get; init; }
    public FoliageInteractor I2 { get; init; }
    public FoliageInteractor I3 { get; init; }
    public MapFoliageFrameState(Vector2 windDirection, float windStrength, float windSpeed, float windSpatialFrequency,
        int interactorCount, FoliageInteractor i0, FoliageInteractor i1, FoliageInteractor i2, FoliageInteractor i3)
    {
        WindDirection = windDirection; WindStrength = windStrength; WindSpeed = windSpeed; WindSpatialFrequency = windSpatialFrequency;
        InteractorCount = interactorCount; I0 = i0; I1 = i1; I2 = i2; I3 = i3;
        if (interactorCount > 0) i0.Validate();
        if (interactorCount > 1) i1.Validate();
        if (interactorCount > 2) i2.Validate();
        if (interactorCount > 3) i3.Validate();
    }
    public static MapFoliageFrameState From(MapFrozenFoliageOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        FoliageInteractor At(int i) => i < policy.Interactors.Count ? policy.Interactors[i] : default;
        return new(policy.WindDirection, policy.WindStrength, policy.WindSpeed, policy.WindSpatialFrequency,
            policy.Interactors.Count, At(0), At(1), At(2), At(3));
    }
}
