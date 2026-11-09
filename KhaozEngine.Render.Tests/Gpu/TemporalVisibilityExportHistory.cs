using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Before each frame after the first, replace the export's preceding metadata colour with production's
/// preceding colour. The export keeps its own confidence, lock, shimmer record, depth and visibility, so matching
/// output locks still prove that the export did not change the decision. Raw half bytes avoid a numeric conversion.
/// One staging texture and byte buffer serve every frame at the fixtures' fixed display size.</summary>
internal sealed class TemporalVisibilityExportHistory(IGpuDevice productionDevice, TemporalHistory production,
    IGpuDevice exportingDevice, TemporalHistory exporting) : IDisposable
{
    readonly IGpuCommandList _commands = productionDevice.Factory.CreateCommandList();
    IGpuTexture? _staging;
    byte[] _bytes = [];

    internal void CopyPreviousColour()
    {
        IGpuTexture source = production.Color(production.WriteIndex);
        IGpuTexture destination = exporting.Color(exporting.WriteIndex);
        Assert.Equal(TemporalFormats.HistoryColor, source.Format);
        Assert.Equal(source.Format, destination.Format);
        Assert.Equal(source.Width, destination.Width);
        Assert.Equal(source.Height, destination.Height);
        int rowBytes = checked((int)source.Width * TemporalFormats.HistoryColorBytesPerPixel);
        if (_staging is null)
        {
            _staging = productionDevice.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                source.Width, source.Height, source.Format, GpuTextureUsage.Staging));
            _bytes = new byte[checked(rowBytes * (int)source.Height)];
        }
        Assert.Equal(source.Width, _staging.Width);
        Assert.Equal(source.Height, _staging.Height);
        using (GpuRecording.Open(productionDevice, _commands, nameof(TemporalVisibilityExportHistory)))
            _commands.CopyTexture(source, _staging);
        productionDevice.Submit(_commands);
        productionDevice.WaitForIdle();
        MappedData map = productionDevice.Map(_staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < source.Height; y++)
                Marshal.Copy(map.Data + (int)(y * map.RowPitch), _bytes, y * rowBytes, rowBytes);
        }
        finally
        {
            productionDevice.Unmap(_staging);
        }
        exportingDevice.UpdateTexture(destination, _bytes, 0, 0, source.Width, source.Height);
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _commands.Dispose();
    }
}
