using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The model vertex stream and immutable instance stream shared by every foliage pass.</summary>
internal static class FoliageVertexLayouts
{
    internal static GpuVertexLayoutDescription[] Create() =>
    [
        new GpuVertexLayoutDescription(
            new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
            new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
            new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4)),
        new GpuVertexLayoutDescription(ModelRenderer.FoliageInstanceData.SizeInBytes, 1,
        [
            new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
            new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
            new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
            new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
            new GpuVertexElement("FoliageParameters", GpuVertexElementFormat.Float4),
            new GpuVertexElement("FoliageMotion", GpuVertexElementFormat.Float4),
        ]),
    ];
}
