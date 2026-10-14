using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class AtmosphereModel
{
    AtmosphereTables? _tables;

    /// <summary>Completed static LUT builds. Construction is lazy. Only accepted physical-settings changes
    /// invalidate the static tables.</summary>
    internal int BuildCount { get; private set; }

    /// <summary>Scattering integrations, including static table construction.</summary>
    internal int ScatteringIntegrationCount { get; private set; }

    internal AtmosphereTables Tables
    {
        get
        {
            if (_tables is not null) return _tables;
            _tables = new AtmosphereTables(this, _settings);
            BuildCount++;
            return _tables;
        }
    }

    /// <summary>Phase-aware single-scattered radiance for unit source illuminance, without the
    /// multi-scattering approximation, art controls or the engine's sky pi conversion. Positions
    /// are planet-centred kilometres. Ground-hit rays include direct Lambertian reflection.</summary>
    public Vector3 SingleScattering(Vector3 positionKm, Vector3 direction, Vector3 lightDirection)
    {
        FinitePosition(positionKm);
        var view = NormalizeDirection(direction);
        var light = NormalizeDirection(lightDirection);
        return IntegrateScattering((positionKm.X, positionKm.Y, positionKm.Z), view, light,
            Tables, 32, isotropic: false, includeMultiScattering: false).Radiance;
    }

    /// <summary>Samples the isotropic multiple-scattering source for unit illuminance at the supplied
    /// altitude and source zenith cosine. Finite coordinates clamp to the LUT's physical domain.</summary>
    public Vector3 MultiScattering(float heightKm, float lightMu)
    {
        if (!float.IsFinite(heightKm) || !float.IsFinite(lightMu))
            throw new ArgumentOutOfRangeException(nameof(heightKm), "Atmosphere LUT coordinates must be finite.");
        return Tables.SampleMulti(heightKm, lightMu);
    }

    /// <summary>Phase-aware single scattering plus isotropic multiple scattering for unit source
    /// illuminance. View and source directions retain their full azimuth. Negative camera altitude
    /// clamps to the surface. No art tint, source brightness or engine pi conversion is applied.</summary>
    public Vector3 SkyRadiance(float cameraHeightKm, Vector3 direction, Vector3 lightDirection)
    {
        if (!float.IsFinite(cameraHeightKm))
            throw new ArgumentOutOfRangeException(nameof(cameraHeightKm), "Atmosphere altitude must be finite.");
        var view = NormalizeDirection(direction);
        var light = NormalizeDirection(lightDirection);
        double radius = _settings.PlanetRadiusKm + Math.Max((double)cameraHeightKm, 0);
        return IntegrateScattering((0, radius, 0), view, light, Tables, 32,
            isotropic: false, includeMultiScattering: true).Radiance;
    }

    /// <summary>Normalized Rayleigh phase per steradian, with both directions pointing toward their endpoints.</summary>
    internal static float RayleighPhase(float cosine)
        => (float)(3 * (1 + (double)cosine * cosine) / (16 * Math.PI));

    /// <summary>Normalized Cornette-Shanks Mie phase per steradian. Positive cosine is forward scattering.</summary>
    internal static float MiePhase(float cosine, float g)
    {
        double asymmetry = g, mu = Math.Clamp((double)cosine, -1, 1);
        double gSquared = asymmetry * asymmetry;
        double denominator = 8 * Math.PI * (2 + gSquared) * Math.Pow(1 + gSquared - 2 * asymmetry * mu, 1.5);
        return (float)(3 * (1 - gSquared) * (1 + mu * mu) / denominator);
    }

    /// <summary>Exact integral of a constant source through one segment, including the zero-extinction limit.</summary>
    internal static Vector3 SegmentIntegral(Vector3 source, Vector3 extinction, float distanceKm)
        => new((float)(source.X * SegmentWeight(extinction.X, distanceKm)),
            (float)(source.Y * SegmentWeight(extinction.Y, distanceKm)),
            (float)(source.Z * SegmentWeight(extinction.Z, distanceKm)));

    static double SegmentWeight(double extinction, double distanceKm)
    {
        double opticalDepth = extinction * distanceKm;
        // This Taylor limit avoids cancellation in 1 - exp(-tau) near vacuum.
        if (opticalDepth < 1e-4)
            return distanceKm * (1 - opticalDepth * .5 + opticalDepth * opticalDepth / 6
                - opticalDepth * opticalDepth * opticalDepth / 24);
        return (1 - Math.Exp(-opticalDepth)) / extinction;
    }

    /// <summary>Shared ray marcher for the sky and MS build. The MS build uses isotropic direct
    /// scattering and a zero recursive source, while Response integrates scattering of a unit
    /// isotropic field. Mirrors IntegrateScatteredLuminance in Hillaire's MIT reference.</summary>
    internal (Vector3 Radiance, Vector3 Response) IntegrateScattering(
        (double X, double Y, double Z) position, (double X, double Y, double Z) view,
        (double X, double Y, double Z) light, AtmosphereTables tables, int steps,
        bool isotropic, bool includeMultiScattering, bool skyOnly = false)
    {
        if (!ScatteringInterval(position, view, skyOnly, out double start, out double end, out bool groundHit))
            return (Vector3.Zero, Vector3.Zero);

        ScatteringIntegrationCount++;
        float cosine = (float)Math.Clamp(view.X * light.X + view.Y * light.Y + view.Z * light.Z, -1, 1);
        float rayleighPhase = isotropic ? (float)(1 / (4 * Math.PI)) : RayleighPhase(cosine);
        float miePhase = isotropic ? rayleighPhase : MiePhase(cosine, _settings.MieAnisotropy);
        var radiance = Vector3.Zero;
        var response = Vector3.Zero;
        var throughput = Vector3.One;
        double previous = start;
        for (int i = 0; i < steps; i++)
        {
            double fraction = (i + 1.0) / steps;
            double next = start + (end - start) * fraction * fraction;
            double midpoint = (previous + next) * .5;
            var point = (X: position.X + view.X * midpoint, Y: position.Y + view.Y * midpoint,
                Z: position.Z + view.Z * midpoint);
            double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
            float height = (float)(radius - _settings.PlanetRadiusKm);
            float lightMu = (float)Math.Clamp((point.X * light.X + point.Y * light.Y + point.Z * light.Z) / radius, -1, 1);
            var density = DensityAtHeight(height);
            var rayleigh = _settings.RayleighScatteringPerKm * (float)density.Rayleigh;
            float mieScattering = (float)(_settings.MieScatteringPerKm * density.Mie);
            var scattering = rayleigh + new Vector3(mieScattering);
            var extinction = rayleigh + new Vector3((float)(_settings.MieExtinctionPerKm * density.Mie))
                + _settings.OzoneAbsorptionPerKm * (float)density.Ozone;
            var directSource = SourceTransmittance(point, light, tables)
                * (rayleigh * rayleighPhase + new Vector3(mieScattering * miePhase));
            var source = directSource;
            if (includeMultiScattering) source += tables.SampleMulti(height, lightMu) * scattering;
            float width = (float)(next - previous);
            radiance += throughput * SegmentIntegral(source, extinction, width);
            response += throughput * SegmentIntegral(scattering, extinction, width);
            throughput *= SegmentTransmittance(extinction, width);
            previous = next;
        }

        if (groundHit)
            radiance += DirectGroundBounce(position, view, light, end, throughput, tables);
        return (radiance, response);
    }

    static Vector3 SegmentTransmittance(Vector3 extinction, float distanceKm)
        => new((float)Math.Exp(-(double)extinction.X * distanceKm),
            (float)Math.Exp(-(double)extinction.Y * distanceKm),
            (float)Math.Exp(-(double)extinction.Z * distanceKm));

    bool ScatteringInterval((double X, double Y, double Z) position, (double X, double Y, double Z) direction, bool skyOnly,
        out double start, out double end, out bool groundHit)
    {
        start = end = 0;
        groundHit = false;
        double bottom = _settings.PlanetRadiusKm;
        if (position.X * position.X + position.Y * position.Y + position.Z * position.Z < bottom * bottom)
            return false;
        double top = bottom + _settings.AtmosphereHeightKm;
        if (!SphereRoots(position.X, position.Y, position.Z, direction.X, direction.Y, direction.Z,
                top, out double entry, out double exit) || exit <= 0)
            return false;
        start = Math.Max(entry, 0);
        end = exit;
        if (!skyOnly && SphereRoots(position.X, position.Y, position.Z, direction.X, direction.Y, direction.Z,
                bottom, out double groundEntry, out double groundExit)
            && groundEntry >= start && groundEntry <= end && groundExit > groundEntry)
        {
            end = groundEntry;
            groundHit = true;
        }
        return true;
    }

    Vector3 SourceTransmittance((double X, double Y, double Z) point, (double X, double Y, double Z) light,
        AtmosphereTables tables)
    {
        double radiusSquared = point.X * point.X + point.Y * point.Y + point.Z * point.Z;
        double projection = point.X * light.X + point.Y * light.Y + point.Z * light.Z;
        double bottom = _settings.PlanetRadiusKm;
        // Strict penetration blocks the source. A tangent still has a visible light path.
        if (projection < 0 && radiusSquared - projection * projection < bottom * bottom)
            return Vector3.Zero;
        double radius = Math.Sqrt(radiusSquared);
        return tables.SampleTransmittance((float)radius, (float)(projection / radius));
    }

    Vector3 DirectGroundBounce((double X, double Y, double Z) position, (double X, double Y, double Z) view,
        (double X, double Y, double Z) light, double distanceKm, Vector3 throughput, AtmosphereTables tables)
    {
        var point = (X: position.X + view.X * distanceKm, Y: position.Y + view.Y * distanceKm,
            Z: position.Z + view.Z * distanceKm);
        double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
        double nDotLight = Math.Max((point.X * light.X + point.Y * light.Y + point.Z * light.Z) / radius, 0);
        return throughput * tables.SampleTransmittance(_settings.PlanetRadiusKm, (float)nDotLight)
            * (float)(nDotLight * _settings.GroundAlbedo / Math.PI);
    }
}
