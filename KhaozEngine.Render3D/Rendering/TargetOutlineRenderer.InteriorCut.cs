using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer
{
    readonly IGpuResourceLayout _interiorCutLayout;
    readonly InteriorCutBinding _interiorCutBinding;
    InteriorCutUniforms _interiorUniforms;

    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)
    {
        _interiorCutBinding.SetTexture(texture);
        _interiorUniforms = uniforms;
    }

    void WriteDrawPayload(int drawIndex, Matrix4x4 world, float alphaCutoff,
        float dissolve, bool dissolveComplement, Vector3 renderOrigin, bool cutInsideInteriors)
    {
        var payload = new DrawUbo
        {
            ViewProj = _viewProj,
            World = world,
            Params = new Vector4(alphaCutoff, dissolve, dissolveComplement ? 1f : 0f, cutInsideInteriors ? 1f : 0f),
            RenderOrigin = new Vector4(renderOrigin, 0f),
            InteriorCutWindow = cutInsideInteriors ? _interiorUniforms.Window : default,
            InteriorCutHeights = cutInsideInteriors ? _interiorUniforms.Heights : default,
        };
        MemoryMarshal.Write(_drawImage.AsSpan(drawIndex * DrawSlotBytes, DrawSlotBytes), in payload);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawUbo
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 World;
        public Vector4 Params;
        public Vector4 RenderOrigin;
        public Vector4 InteriorCutWindow;
        public Vector4 InteriorCutHeights;
    }
}
