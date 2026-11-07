namespace KhaozEngine.Gpu;

/// <summary>Attachments whose contents are dead after a rendering pass ends. Zero retains every attachment.
/// Colour bit n names attachment n. Depth and stencil are independent even on a combined texture.
/// The pass owner must clear or overwrite discarded data before any later read, including a reopened pass,
/// a capture or a readback. Backends may retain discarded contents as an optimization fallback.</summary>
public readonly record struct GpuAttachmentStoreActions(
    ulong DiscardColourMask = 0, bool DiscardDepth = false, bool DiscardStencil = false)
{
    /// <summary>Whether colour attachment n is dead after this pass.</summary>
    public bool DiscardsColour(int index) => index is >= 0 and < 64 && (DiscardColourMask & (1ul << index)) != 0;
}
