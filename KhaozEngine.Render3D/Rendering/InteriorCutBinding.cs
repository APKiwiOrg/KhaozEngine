using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns one interior-cut image/sampler set. The image, sampler and renderer's layout are borrowed.</summary>
internal sealed class InteriorCutBinding(IGpuDevice device, IGpuResourceLayout layout) : IDisposable
{
    IGpuTexture? _texture;

    /// <summary>The binding after the first SetTexture call, shared by all draws using this layout.</summary>
    internal IGpuResourceSet? Set { get; private set; }

    internal void SetTexture(IGpuTexture texture)
    {
        if (ReferenceEquals(texture, _texture)) return;
        IGpuResourceSet next = device.Factory.CreateResourceSet(new GpuResourceSetDescription(
            layout, texture, device.PointSampler));
        if (Set is not null)
        {
            device.WaitForIdle();
            Set.Dispose();
        }
        Set = next;
        _texture = texture;
    }

    public void Dispose() => Set?.Dispose();
}
