namespace KhaozEngine.Gpu;

/// <summary>Why GpuTextureTransfer refused an operation.</summary>
public enum GpuTextureTransferRefusal
{
    /// <summary>Refuses mip generation without a real texture and multiple mip levels.</summary>
    MipChainRequired,
    /// <summary>Refuses a multisample resolve whose source or destination is not a compatible attachment.</summary>
    ResolveShapeMismatch,
    /// <summary>Refuses a copied subresource index outside the selected source or destination texture.</summary>
    SubresourceOutOfRange,
    /// <summary>Refuses a whole-texture copy between incompatible shapes.</summary>
    TextureShapeMismatch,
}
