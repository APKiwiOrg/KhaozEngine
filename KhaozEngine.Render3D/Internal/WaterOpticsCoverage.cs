using System;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Headless admission checks for complete optical coverage, including certified swell displacement.</summary>
internal static class WaterOpticsCoverage
{
    /// <summary>Horizontal displacement padding for the effective per-plane settings.</summary>
    internal static float Padding(WaterSettings effective)
    {
        if (effective.WaveSource == WaterWaveSource.FftOcean)
            throw new NotSupportedException("Bound water optics requires a certified displacement envelope. FFT water has none.");
        if (!float.IsFinite(effective.SwellAmplitude) || !float.IsFinite(effective.SwellWavelength)
            || !float.IsFinite(effective.SwellSteepness))
            throw new ArgumentException("Optical coverage requires finite swell inputs.", nameof(effective));
        float padding = WaterSwellReach.Of(effective.SwellAmplitude, effective.SwellWavelength,
            effective.SwellSteepness).X;
        if (!float.IsFinite(padding))
            throw new ArgumentException("The swell displacement envelope is not finite.", nameof(effective));
        return padding;
    }

    /// <summary>Refuse incomplete opted-in data before queueing a plane. Bilinear sampling accepts exact field
    /// edges. Triangle requests must fit one immutable certificate. Unbound requests keep legacy behavior.</summary>
    internal static void Validate(in WaterPlane plane, WaterSettings effective, WaterOpticsPayload payload = default)
    {
        if (!plane.Optics.IsBound) return;
        float padding = Padding(effective);
        if (!float.IsFinite(plane.CenterX) || !float.IsFinite(plane.SurfaceY) || !float.IsFinite(plane.CenterZ)
            || !float.IsFinite(plane.HalfExtentX) || !float.IsFinite(plane.HalfExtentZ)
            || plane.HalfExtentX <= 0f || plane.HalfExtentZ <= 0f)
            throw new ArgumentException("A bound water plane must have finite positive bounds.", nameof(plane));

        // Widen before adding so float rounding cannot hide incomplete coverage at a large absolute origin.
        double minX = (double)plane.CenterX - plane.HalfExtentX - padding;
        double minZ = (double)plane.CenterZ - plane.HalfExtentZ - padding;
        double maxX = (double)plane.CenterX + plane.HalfExtentX + padding;
        double maxZ = (double)plane.CenterZ + plane.HalfExtentZ + padding;
        WaterOpticsBinding binding = plane.Optics;
        double fieldMaxX = (double)binding.Origin.X + binding.Size.X;
        double fieldMaxZ = (double)binding.Origin.Z + binding.Size.Y;
        if (fieldMaxX > float.MaxValue || fieldMaxZ > float.MaxValue
            || fieldMaxX <= binding.Origin.X || fieldMaxZ <= binding.Origin.Z
            || minX < binding.Origin.X || minZ < binding.Origin.Z || maxX > fieldMaxX || maxZ > fieldMaxZ)
            throw new ArgumentException("The optical field does not cover the water rectangle and its swell displacement.", nameof(plane));
        payload.ValidateCoverage(binding, minX, minZ, maxX, maxZ);
    }
}
