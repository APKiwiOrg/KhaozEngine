using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Reads the FFT ocean's produced height maps back from a real device.</summary>
internal static class RendererPerfOceanHeights
{
    /// <summary>The largest surface height the vertex stage can sample over <paramref name="times"/>: for each
    /// time, the sum over cascades of the cascade's largest absolute texel height.</summary>
    internal static float Peak(WaterSettings water, IEnumerable<float> times)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        if (!device.Capabilities.SupportsCompute) throw new InvalidOperationException($"{device.Backend} has no compute.");
        int n = water.SeaState.CascadeResolution, cascades = water.SeaState.CascadeCount;
        using var producer = new OceanFftProducer(device);
        float peak = 0f;
        foreach (float time in times)
        {
            producer.Prepare(water, time, wantOcean: true);
            using (IGpuCommandList commands = device.Factory.CreateCommandList())
            {
                commands.Begin();
                bool active = producer.Record(commands);
                commands.End();
                device.Submit(commands);
                device.WaitForIdle();
                if (!active) throw new InvalidOperationException("The ocean producer did not run.");
            }
            float sum = 0f;
            for (int cascade = 0; cascade < cascades; cascade++) sum += MaxHeight(device, producer.Map, (uint)cascade, n);
            peak = MathF.Max(peak, sum);
        }
        return peak;
    }

    // Height is channel 1 of the displacement layers, stored as half floats.
    static float MaxHeight(IGpuDevice device, IGpuTexture map, uint layer, int n)
    {
        using IGpuTexture staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            (uint)n, (uint)n, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
        using (IGpuCommandList commands = device.Factory.CreateCommandList())
        {
            commands.Begin();
            commands.CopyTextureSubresource(map, 0, layer, staging, (uint)n, (uint)n);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
        }
        var row = new byte[n * 8];
        float max = 0f;
        MappedData mapped = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < n; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.Data, (int)(y * mapped.RowPitch)), row, 0, row.Length);
                for (int x = 0; x < n; x++) max = MathF.Max(max, MathF.Abs((float)BitConverter.ToHalf(row, x * 8 + 2)));
            }
        }
        finally { device.Unmap(staging); }
        return max;
    }
}
