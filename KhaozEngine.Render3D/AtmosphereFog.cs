using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>CPU reference for analytic local fog, using absolute altitude and exponential densities.
/// Rayleigh, Mie extinction and optional grey haze contribute. Ozone is excluded from this approximation.</summary>
public static class AtmosphereFog
{
    /// <summary>Integrates exp(-height / scaleHeightKm) along a segment with linearly varying altitude.
    /// Underground endpoints clamp to zero before interpolation. Positive infinite distance is supported.
    /// Descending segments use the lower endpoint so no exponential has a positive argument.</summary>
    public static double OpticalDepthTerm(double distanceKm, double startHeightKm, double endHeightKm,
        double scaleHeightKm)
    {
        ValidateDistance(distanceKm, nameof(distanceKm));
        ValidateHeight(startHeightKm, nameof(startHeightKm));
        ValidateHeight(endHeightKm, nameof(endHeightKm));
        if (!double.IsFinite(scaleHeightKm) || scaleHeightKm <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleHeightKm), "Fog scale height must be finite and positive.");
        if (distanceKm == 0) return 0;
        // Every exponential density is positive at finite height, even if exp would underflow.
        if (double.IsPositiveInfinity(distanceKm)) return double.PositiveInfinity;

        double start = Math.Max(startHeightKm, 0);
        double end = Math.Max(endHeightKm, 0);
        double heightChange = (end - start) / scaleHeightKm;
        double lowerHeight = heightChange < 0 ? end : start;
        double lowerEndpointDensity = Math.Exp(-lowerHeight / scaleHeightKm);
        return distanceKm * lowerEndpointDensity * ExponentialSegmentAverage(Math.Abs(heightChange));
    }

    /// <summary>RGB segment transmission. The aerial onset shapes the distance, then the aerial scale maps
    /// it and the clamped endpoint heights into kilometres. Heights are not shaped. Disabled fog, zero scale and
    /// zero distance transmit exactly one.</summary>
    public static Vector3 Transmittance(AtmosphereSettings settings, float distanceWorld,
        float startAbsoluteHeightWorld, float endAbsoluteHeightWorld)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ValidateDistance(distanceWorld, nameof(distanceWorld));
        ValidateHeight(startAbsoluteHeightWorld, nameof(startAbsoluteHeightWorld));
        ValidateHeight(endAbsoluteHeightWorld, nameof(endAbsoluteHeightWorld));
        if (!settings.Enabled || settings.AerialDistanceScale == 0 || distanceWorld == 0) return Vector3.One;

        double kilometresPerWorldUnit = (double)settings.AerialDistanceScale / settings.WorldUnitsPerKilometre;
        double distanceKm = OnsetDistance(distanceWorld, settings.AerialOnsetDistance) * kilometresPerWorldUnit;
        double startHeightKm = Math.Max((double)startAbsoluteHeightWorld, 0) * kilometresPerWorldUnit;
        double endHeightKm = Math.Max((double)endAbsoluteHeightWorld, 0) * kilometresPerWorldUnit;
        var rayleigh = settings.RayleighScatteringPerKm;
        double rayleighDepth = rayleigh == Vector3.Zero ? 0
            : OpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, settings.RayleighScaleHeightKm);
        double mieDepth = settings.MieExtinctionPerKm == 0 ? 0
            : OpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, settings.MieScaleHeightKm);
        double hazeDepth = settings.HazeDensityPerKm == 0 ? 0
            : OpticalDepthTerm(distanceKm, startHeightKm, endHeightKm, settings.HazeScaleHeightKm);
        double greyDepth = CoefficientOpticalDepth(settings.MieExtinctionPerKm, mieDepth)
            + CoefficientOpticalDepth(settings.HazeDensityPerKm, hazeDepth);
        return new Vector3(
            (float)Math.Exp(-(CoefficientOpticalDepth(rayleigh.X, rayleighDepth) + greyDepth)),
            (float)Math.Exp(-(CoefficientOpticalDepth(rayleigh.Y, rayleighDepth) + greyDepth)),
            (float)Math.Exp(-(CoefficientOpticalDepth(rayleigh.Z, rayleighDepth) + greyDepth)));
    }

    /// <summary>Optical distance for <see cref="AtmosphereSettings.AerialOnsetDistance"/>: d * d / (d + s).
    /// It has zero value and slope at the camera, never exceeds d and tends to d - s, so distant rays keep
    /// their fog. A zero onset and an infinite distance return the distance unchanged.</summary>
    internal static double OnsetDistance(double distanceWorld, double onsetWorld)
        => onsetWorld == 0 || double.IsPositiveInfinity(distanceWorld) ? distanceWorld
            : distanceWorld * (distanceWorld / (distanceWorld + onsetWorld));

    /// <summary>Blends surface colour toward fog colour. Additive effects only lose transmitted energy.</summary>
    public static Vector3 Compose(Vector3 colour, Vector3 transmittance, Vector3 haze, bool additive = false)
        => additive ? colour * transmittance : colour * transmittance + haze * (Vector3.One - transmittance);

    /// <summary>Fogs premultiplied RGB without changing coverage. Additivity removes only the haze term.</summary>
    public static Vector3 ComposePremultiplied(Vector3 rgb, float coverage, Vector3 transmittance,
        Vector3 haze, float additivity)
        => rgb * transmittance + coverage * (1 - additivity) * haze * (Vector3.One - transmittance);

    /// <summary>Fogs display-space RGB toward the tonemapped haze, leaving the input colour unmapped.
    /// With HDR disabled the haze clamps to the display range. Additive effects only attenuate.</summary>
    public static Vector3 ComposeDisplay(Vector3 rgb, Vector3 transmittance, Vector3 haze, HdrSettings hdr,
        bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        if (additive) return rgb * transmittance;
        if (transmittance == Vector3.One) return rgb;
        Vector3 displayHaze;
        if (hdr.Enabled)
        {
            var mapped = TonemapMath.Map(haze.X, haze.Y, haze.Z, MathF.Max(hdr.Exposure, 0),
                (int)hdr.Operator, Math.Clamp(hdr.ChromaPreservation, 0, 1));
            displayHaze = new Vector3(mapped.R, mapped.G, mapped.B);
        }
        else displayHaze = Vector3.Clamp(haze, Vector3.Zero, Vector3.One);
        return Compose(rgb, transmittance, displayHaze);
    }

    /// <summary>Shades a camera-relative segment with absolute heights recovered from renderOrigin.Y.
    /// Displacement remains relative. Sky colour uses the unscaled physical camera altitude and view
    /// UV joined smoothly to the sky-side geometric horizon, preserving azimuth or using +X at the nadir.
    /// Below the horizon the sample is rescaled to the view's true scattering angle.</summary>
    public static Vector3 Shade(AtmosphereSettings settings, AtmosphereModel model, in AtmosphereFrame frame,
        Vector3 colour, Vector3 cameraRelative, Vector3 fragmentRelative, Vector3 renderOrigin, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return colour;
        ArgumentNullException.ThrowIfNull(model);
        ValidatePosition(cameraRelative, nameof(cameraRelative));
        ValidatePosition(fragmentRelative, nameof(fragmentRelative));
        ValidatePosition(renderOrigin, nameof(renderOrigin));
        float cameraAbsoluteHeight = cameraRelative.Y + renderOrigin.Y;
        float fragmentAbsoluteHeight = fragmentRelative.Y + renderOrigin.Y;
        var view = SegmentView(cameraRelative, fragmentRelative, out float distanceWorld);
        var transmittance = Transmittance(settings, distanceWorld, cameraAbsoluteHeight, fragmentAbsoluteHeight);
        if (additive) return Compose(colour, transmittance, Vector3.Zero, additive: true);
        if (transmittance == Vector3.One) return colour;

        float cameraHeightKm = Math.Max(settings.HeightKm(cameraAbsoluteHeight), 0);
        double radius = settings.PlanetRadiusKm + (double)cameraHeightKm;
        var skyUv = AtmosphereFogSampling.SkyUv(view, radius, settings.PlanetRadiusKm);
        // The geometric horizon never rises above zero elevation, so upward views skip the source integrals.
        var ratio = view.Y >= 0 ? Vector3.One : AtmosphereFogScattering.Evaluate(settings, model, frame, cameraHeightKm)
            .Ratio(view, radius, settings.PlanetRadiusKm);
        var haze = model.SkyViewValue(cameraHeightKm, skyUv, frame, ratio);
        return Compose(colour, transmittance, haze);
    }

    // (1 - exp(-x)) / x has the equal-height limit one. x is non-negative after endpoint reversal.
    static double ExponentialSegmentAverage(double heightChange)
        => heightChange < 1e-4 ? 1 - heightChange / 2 + heightChange * heightChange / 6
            : (1 - Math.Exp(-heightChange)) / heightChange;

    // In vacuum, a zero coefficient contributes zero even when the segment integral is infinite.
    static double CoefficientOpticalDepth(float coefficient, double depth)
        => coefficient == 0 ? 0 : coefficient * depth;

    static Vector3 SegmentView(Vector3 cameraRelative, Vector3 fragmentRelative, out float distanceWorld)
    {
        double x = (double)fragmentRelative.X - cameraRelative.X;
        double y = (double)fragmentRelative.Y - cameraRelative.Y;
        double z = (double)fragmentRelative.Z - cameraRelative.Z;
        double length = Math.Sqrt(x * x + y * y + z * z);
        distanceWorld = (float)length;
        return length == 0 ? Vector3.UnitX : new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
    }

    static void ValidateDistance(double distance, string name)
    {
        if (double.IsNaN(distance) || distance < 0)
            throw new ArgumentOutOfRangeException(name, "Fog distance must be non-negative and cannot be NaN.");
    }

    static void ValidateHeight(double height, string name)
    {
        if (!double.IsFinite(height))
            throw new ArgumentOutOfRangeException(name, "Fog altitude must be finite.");
    }

    static void ValidatePosition(Vector3 position, string name)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentException("Fog position must be finite.", name);
    }
}
