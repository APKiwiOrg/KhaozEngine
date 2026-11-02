using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Bytes are the payload the operation names. GenerateMipmaps records 0 because the driver derives its own
/// work. EncoderOrdinal is the blit encoder holding the operation.</summary>
internal readonly record struct PassTransfer(int CallbackIndex, long CommandBuffer, int EncoderOrdinal, string Kind,
    ulong Bytes, long Destination, int LabelId, int LabelOccurrence);

internal static class PassTransferKind
{
    internal const string BufferUpload = "bufferUpload";
    internal const string BufferCopy = "bufferCopy";
    internal const string TextureToTexture = "textureToTexture";
    internal const string TextureToBuffer = "textureToBuffer";
    internal const string BufferToTexture = "bufferToTexture";
    internal const string StagingToStaging = "stagingToStaging";
    internal const string GenerateMipmaps = "generateMipmaps";
    internal const string SwapchainToTexture = "swapchainToTexture";
    internal const string SwapchainToBuffer = "swapchainToBuffer";

    internal static string For(MetalTransferCase copy) => copy switch
    {
        MetalTransferCase.TextureToTexture => TextureToTexture,
        MetalTransferCase.TextureToBuffer => TextureToBuffer,
        MetalTransferCase.BufferToTexture => BufferToTexture,
        _ => StagingToStaging,
    };

    /// <summary>Never throws. A hook on the copy path must not fail a copy over a format this table lacks, which
    /// reads as 0 bytes in the report.</summary>
    internal static ulong TextureBytes(GpuPixelFormat format, uint width, uint height)
    {
        try { return MetalStagingLayout.RowPitch(width, format) * height; }
        catch (ArgumentOutOfRangeException) { return 0; }
    }
}
