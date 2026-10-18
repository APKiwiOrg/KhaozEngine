using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ShadowMapRenderer
{
    readonly IGpuResourceLayout _interiorFrameLayout;
    readonly IGpuResourceLayout _interiorCutLayout;
    IGpuTexture? _interiorTexture;
    IGpuResourceSet? _interiorCutSet;
    InteriorCutUniforms _interiorUniforms;

    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)
    {
        if (!ReferenceEquals(texture, _interiorTexture))
        {
            IGpuResourceSet next = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _interiorCutLayout, texture, _gd.PointSampler));
            if (_interiorCutSet is not null)
            {
                _gd.WaitForIdle();
                _interiorCutSet.Dispose();
            }
            _interiorCutSet = next;
            _interiorTexture = texture;
        }
        _interiorUniforms = uniforms;
    }

    void PackInteriorCut(Span<byte> slot)
    {
        MemoryMarshal.Write(slot.Slice(96), _interiorUniforms.Window);
        MemoryMarshal.Write(slot.Slice(112), _interiorUniforms.Heights);
    }
}
