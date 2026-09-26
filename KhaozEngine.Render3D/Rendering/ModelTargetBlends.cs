using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The per-attachment blend arrays of the pipelines that draw into the model framebuffer, sized from its
/// outputs. The target has three colour attachments, and a fourth (motion) while temporal rendering is active, and a
/// pipeline's blend array must name every attachment of the framebuffer it renders into. For three attachments each
/// array is exactly the literal it replaces.</summary>
internal static class ModelTargetBlends
{
    /// <summary>Opaque passes overwrite every attachment.</summary>
    internal static GpuBlendAttachment[] Opaque(in GpuOutputDescription outputs)
    {
        var blends = new GpuBlendAttachment[outputs.Colour.Length];
        for (int i = 0; i < blends.Length; i++) blends[i] = GpuBlendAttachment.OverrideBlend;
        return blends;
    }

    /// <summary>Transparent passes blend colour with <paramref name="colour"/> and keep every other attachment: the
    /// normal and depth the edge pass reads, and the motion the temporal resolve reads.</summary>
    internal static GpuBlendAttachment[] Transparent(GpuBlendAttachment colour, in GpuOutputDescription outputs)
    {
        var blends = new GpuBlendAttachment[outputs.Colour.Length];
        blends[0] = colour;
        for (int i = 1; i < blends.Length; i++) blends[i] = GpuBlendAttachment.PreserveDestination;
        return blends;
    }
}
