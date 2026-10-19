using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageSelectedReadback
{
    internal static unsafe uint[] Read(IGpuDevice device, IGpuBuffer buffer)
    {
        using var staging = device.Factory.CreateBuffer(new(buffer.SizeInBytes, GpuBufferUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        commands.Begin(); commands.CopyBuffer(buffer, 0, staging, 0, buffer.SizeInBytes); commands.End();
        device.Submit(commands); device.WaitForIdle();
        var map = device.Map(staging, GpuMapMode.Read);
        try { return new ReadOnlySpan<uint>((void*)map.Data, checked((int)buffer.SizeInBytes / 4)).ToArray(); }
        finally { device.Unmap(staging); }
    }
}
