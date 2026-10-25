using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    readonly Guid _opticsOwner = Guid.NewGuid();
    int _opticsIndex;
    /// <summary>Live triangle fields, the only kind the TileWorld optics capability can upload.</summary>
    public Dictionary<WaterOpticsHandle, WaterOpticsTriangleField> TriangleFields { get; } = new();
    public List<WaterOpticsHandle> TriangleLoads { get; } = new();
    public List<WaterOpticsHandle> OpticalUnloads { get; } = new();
    public float OpticalPadding { get; set; }
    /// <summary>One-based upload ordinal that throws, uploading nothing.</summary>
    public int ThrowOnOpticalLoad { get; set; }
    /// <summary>One-based ordinal, counted like <see cref="ThrowOnOpticalLoad"/>, whose upload the backend refuses
    /// with <see cref="WaterOpticsSurfaceException"/> before allocating.</summary>
    public int RefuseSurfaceOnOpticalLoad { get; set; }
    public Action? BeforeOpticalLoad { get; set; }

    public WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field)
    {
        WaterOpticsHandle handle = NextOpticalHandle();
        TriangleFields.Add(handle, field);
        TriangleLoads.Add(handle);
        return handle;
    }

    WaterOpticsHandle NextOpticalHandle()
    {
        BeforeOpticalLoad?.Invoke();
        int ordinal = TriangleLoads.Count + 1;
        if (ordinal == ThrowOnOpticalLoad) throw new InvalidOperationException("Refused optical upload.");
        if (ordinal == RefuseSurfaceOnOpticalLoad)
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.UnrepresentableSurface,
                "Refused optical scalar.");
        return new WaterOpticsHandle(_opticsOwner, _opticsIndex++, 1);
    }

    public void UnloadWaterOpticsField(WaterOpticsHandle handle)
    {
        if (!TriangleFields.Remove(handle))
            throw new ArgumentException("Stale optical handle.", nameof(handle));
        OpticalUnloads.Add(handle);
    }

    /// <summary>Refuses an FFT look like the shipped scene. This fake has no scene default, so only the look counts.</summary>
    public bool TryGetWaterOpticsPadding(WaterLook? look, out float padding, out WaterOpticsSurfaceFailure reason)
    {
        bool certified = look?.WaveSource != WaterWaveSource.FftOcean;
        padding = certified ? OpticalPadding : 0f;
        reason = certified ? default : WaterOpticsSurfaceFailure.UncertifiedDisplacement;
        return certified;
    }
}
