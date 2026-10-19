using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class SilhouetteRenderer
{
    readonly IGpuResourceLayout _interiorCutLayout;
    readonly InteriorCutBinding _interiorCutBinding;
    InteriorCutUniforms _interiorUniforms;

    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)
    {
        _interiorCutBinding.SetTexture(texture);
        _interiorUniforms = uniforms;
    }

    void WriteDrawPayload(int drawIndex, Matrix4x4 world, Color color, float widthMetres, bool cutInsideInteriors)
    {
        var payload = new DrawUbo
        {
            ViewProj = _viewProj,
            World = world,
            Color = color.ToVector4(),
            Params = new Vector4(widthMetres, cutInsideInteriors ? 1f : 0f, 0f, 0f),
            InteriorCutWindow = cutInsideInteriors ? _interiorUniforms.Window : default,
            InteriorCutHeights = cutInsideInteriors ? _interiorUniforms.Heights : default,
        };
        MemoryMarshal.Write(_image.AsSpan(drawIndex * SlotBytes, SlotBytes), in payload);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawUbo
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 World;
        public Vector4 Color;
        public Vector4 Params;
        public Vector4 InteriorCutWindow;
        public Vector4 InteriorCutHeights;
    }
}
