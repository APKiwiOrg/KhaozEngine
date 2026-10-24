using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    readonly Guid _opticsOwner = Guid.NewGuid();
    int _opticsIndex;
    public Dictionary<WaterOpticsHandle, WaterOpticsField> OpticalFields { get; } = new();
    public List<WaterOpticsHandle> OpticalLoads { get; } = new();
    public List<WaterOpticsHandle> OpticalUnloads { get; } = new();
    public float OpticalPadding { get; set; }
    public int ThrowOnOpticalLoad { get; set; }
    public Action? BeforeOpticalLoad { get; set; }

    public WaterOpticsHandle LoadWaterOpticsField(WaterOpticsField field)
    {
        BeforeOpticalLoad?.Invoke();
        if (OpticalLoads.Count + 1 == ThrowOnOpticalLoad)
            throw new InvalidOperationException("Refused optical upload.");
        var handle = new WaterOpticsHandle(_opticsOwner, _opticsIndex++, 1);
        OpticalFields.Add(handle, field);
        OpticalLoads.Add(handle);
        return handle;
    }

    public void UnloadWaterOpticsField(WaterOpticsHandle handle)
    {
        if (!OpticalFields.Remove(handle)) throw new ArgumentException("Stale optical handle.", nameof(handle));
        OpticalUnloads.Add(handle);
    }

    public float GetWaterOpticsPadding(WaterLook? look = null) => OpticalPadding;
}
