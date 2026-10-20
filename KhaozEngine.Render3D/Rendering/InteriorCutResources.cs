using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

internal sealed class InteriorCutResources : IDisposable
{
    readonly IGpuDevice _device;
    readonly IGpuTexture _zero;
    readonly List<IGpuTexture> _retired = new();
    InteriorHeightField? _field;

    internal InteriorCutResources(IGpuDevice device)
    {
        _device = device;
        _zero = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        try { device.UpdateTexture(_zero, new byte[4], 0, 0, 1, 1); }
        catch { _zero.Dispose(); throw; }
        Texture = _zero;
    }

    internal IGpuTexture Texture { get; private set; }
    internal long TextureGeneration { get; private set; }
    internal long Version { get; private set; }

    /// <summary>Upload each requested field once. Keep replaced images alive until every borrower has rebound.</summary>
    internal void Apply(InteriorHeightField? field)
    {
        if (ReferenceEquals(_field, field)) return;
        IGpuTexture next = _zero;
        if (field is not null)
        {
            uint cells = (uint)field.Window.Cells;
            bool reuse = !ReferenceEquals(Texture, _zero) && Texture.Width == cells && Texture.Height == cells;
            next = reuse ? Texture : _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                cells, cells, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            try
            {
                // The immutable field owns one complete array. Borrow it for the upload instead of copying a window.
                if (!MemoryMarshal.TryGetArray(field.Texels, out ArraySegment<byte> texels)
                    || texels.Array is null || texels.Offset != 0 || texels.Count != texels.Array.Length)
                    throw new InvalidOperationException("Interior texels must occupy one complete array.");
                _device.UpdateTexture(next, texels.Array, 0, 0, cells, cells);
            }
            catch
            {
                if (!reuse) next.Dispose();
                throw;
            }
        }
        if (!ReferenceEquals(next, Texture))
        {
            if (!ReferenceEquals(Texture, _zero)) _retired.Add(Texture);
            Texture = next;
            TextureGeneration++;
        }
        _field = field;
        Version++;
    }

    internal void CommitBindings()
    {
        if (_retired.Count == 0) return;
        _device.WaitForIdle();
        foreach (IGpuTexture texture in _retired) texture.Dispose();
        _retired.Clear();
    }

    public void Dispose()
    {
        foreach (IGpuTexture texture in _retired) texture.Dispose();
        _retired.Clear();
        if (!ReferenceEquals(Texture, _zero)) Texture.Dispose();
        _zero.Dispose();
        _field = null;
    }
}
