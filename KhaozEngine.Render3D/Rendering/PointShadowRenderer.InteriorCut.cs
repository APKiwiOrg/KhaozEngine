using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PointShadowRenderer
{
    readonly IGpuResourceLayout _interiorCutLayout;
    readonly InteriorCutBinding _interiorCutBinding;
    InteriorCutUniforms _interiorUniforms;

    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)
    {
        _interiorCutBinding.SetTexture(texture);
        _interiorUniforms = uniforms;
    }

    void PackInteriorCut(Span<byte> slot)
    {
        MemoryMarshal.Write(slot.Slice(144), _interiorUniforms.Window);
        MemoryMarshal.Write(slot.Slice(160), _interiorUniforms.Heights);
    }
}
