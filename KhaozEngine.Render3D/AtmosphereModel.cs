using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>CPU reference for physical atmosphere transport. Geometry and optical depth use
/// double precision internally, with planet-centred positions and distances in kilometres.</summary>
public sealed partial class AtmosphereModel
{
    AtmosphereSettings _settings;

    /// <summary>Validates and retains an independent snapshot of the supplied settings.</summary>
    public AtmosphereModel(AtmosphereSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings.Clone();
    }

    /// <summary>Validates the incoming values and replaces the retained snapshot only when they differ.
    /// Returns true when the snapshot changed. Later edits to the caller's settings cannot affect it.</summary>
    public bool UpdateSettings(AtmosphereSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (_settings.ValueEquals(settings)) return false;
        _settings = settings.Clone();
        _tables = null;
        return true;
    }

    /// <summary>Rayleigh, Mie and ozone relative densities at the supplied altitude in kilometres.
    /// Underground altitudes use surface density.</summary>
    public Vector3 Density(float heightKm)
    {
        if (!float.IsFinite(heightKm))
            throw new ArgumentOutOfRangeException(nameof(heightKm), "Atmosphere altitude must be finite.");
        var density = DensityAtHeight(heightKm);
        return new Vector3((float)density.Rayleigh, (float)density.Mie, (float)density.Ozone);
    }

    /// <summary>RGB transmission along a normalized ray through the atmosphere, using quadratic
    /// midpoint segments. Rays crossing the planet return zero, including inward rays at the surface.
    /// Rays with no forward atmosphere segment return one. Directions must be finite and nonzero.</summary>
    public Vector3 Transmittance(Vector3 positionKm, Vector3 direction, int steps = 256)
    {
        FinitePosition(positionKm);
        var (dx, dy, dz) = NormalizeDirection(direction);
        if (steps <= 0)
            throw new ArgumentOutOfRangeException(nameof(steps), "Atmosphere integration needs positive steps.");

        double px = positionKm.X, py = positionKm.Y, pz = positionKm.Z;
        double bottom = _settings.PlanetRadiusKm;
        double top = bottom + _settings.AtmosphereHeightKm;
        if (!SphereRoots(px, py, pz, dx, dy, dz, top, out double entry, out double exit) || exit <= 0)
            return Vector3.One;
        double start = Math.Max(entry, 0);
        if (SphereRoots(px, py, pz, dx, dy, dz, bottom, out double groundEntry, out double groundExit)
            && groundEntry < exit && groundExit > Math.Max(start, groundEntry))
            return Vector3.Zero;

        double rayleighDepth = 0, mieDepth = 0, ozoneDepth = 0;
        double previous = start;
        for (int i = 0; i < steps; i++)
        {
            double fraction = (i + 1.0) / steps;
            double next = start + (exit - start) * fraction * fraction;
            double midpoint = (previous + next) * .5;
            double x = px + dx * midpoint, y = py + dy * midpoint, z = pz + dz * midpoint;
            double height = Math.Sqrt(x * x + y * y + z * z) - bottom;
            var density = DensityAtHeight(height);
            double width = next - previous;
            rayleighDepth += density.Rayleigh * width;
            mieDepth += density.Mie * width;
            ozoneDepth += density.Ozone * width;
            previous = next;
        }

        var rayleigh = _settings.RayleighScatteringPerKm;
        var ozone = _settings.OzoneAbsorptionPerKm;
        double mie = _settings.MieExtinctionPerKm * mieDepth;
        return new Vector3(
            (float)Math.Exp(-(rayleigh.X * rayleighDepth + mie + ozone.X * ozoneDepth)),
            (float)Math.Exp(-(rayleigh.Y * rayleighDepth + mie + ozone.Y * ozoneDepth)),
            (float)Math.Exp(-(rayleigh.Z * rayleighDepth + mie + ozone.Z * ozoneDepth)));
    }

    /// <summary>Intersects the line through a planet-centred origin with a sphere.
    /// Returns both ordered, signed distances along the normalized direction, including roots behind
    /// the origin. A tangent returns equal roots. Directions must be finite and nonzero.</summary>
    public static bool IntersectSphere(Vector3 originKm, Vector3 direction, float radiusKm, out Vector2 distancesKm)
    {
        FinitePosition(originKm);
        var (dx, dy, dz) = NormalizeDirection(direction);
        if (!float.IsFinite(radiusKm) || radiusKm <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusKm), "Sphere radius must be finite and positive.");
        bool intersects = SphereRoots(originKm.X, originKm.Y, originKm.Z, dx, dy, dz, radiusKm,
            out double near, out double far);
        distancesKm = intersects ? new Vector2((float)near, (float)far) : Vector2.Zero;
        return intersects;
    }

    (double Rayleigh, double Mie, double Ozone) DensityAtHeight(double heightKm)
    {
        double height = Math.Max(heightKm, 0);
        return (Math.Exp(-height / _settings.RayleighScaleHeightKm),
            Math.Exp(-height / _settings.MieScaleHeightKm),
            Math.Max(0, 1 - Math.Abs(height - _settings.OzoneCenterKm) / (_settings.OzoneWidthKm * .5)));
    }

    static (double X, double Y, double Z) NormalizeDirection(Vector3 direction)
    {
        double x = direction.X, y = direction.Y, z = direction.Z;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length) || length == 0)
            throw new ArgumentException("Atmosphere direction must be finite and nonzero.", nameof(direction));
        return (x / length, y / length, z / length);
    }

    static void FinitePosition(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentException("Atmosphere position must be finite.", nameof(position));
    }

    static bool SphereRoots(double px, double py, double pz, double dx, double dy, double dz,
        double radius, out double near, out double far)
    {
        double projection = px * dx + py * dy + pz * dz;
        double c = px * px + py * py + pz * pz - radius * radius;
        double discriminant = projection * projection - c;
        if (discriminant < 0)
        {
            near = far = 0;
            return false;
        }

        // Recover the other root from their product to avoid subtracting nearly equal values.
        double q = -projection - Math.CopySign(Math.Sqrt(discriminant), projection);
        double other = q == 0 ? 0 : c / q;
        near = Math.Min(q, other);
        far = Math.Max(q, other);
        return true;
    }
}
