using System;
using System.Buffers;
using System.IO.Compression;

namespace KhaozEngine.Catalog;

/// <summary>
/// The ONE Brotli seam every chunk format goes through, so the three codecs cannot answer differently for
/// the same stream. Spec 8.4 names the refusal kinds generically across <c>KECC</c>, <c>KECR</c> and
/// <c>KECT</c>, and spec 7.6 says a text chunk takes the same header refusals a row chunk does, so a shared
/// rule is what the specs describe rather than a convenience.
/// <para>
/// <b>The discrimination is the whole point.</b> The static <see cref="BrotliDecoder.TryDecompress"/>
/// collapses every failure into one <c>false</c>, which made a stream expanding past its declared length
/// read as <c>chunk-decompress</c> (a corrupt stream) in two codecs and <c>chunk-too-large</c> (a resource
/// refusal) in the third, for the same bytes. Those are different operator actions: one says the file is
/// damaged, the other says the file is honest and too big for the bound the reader holds it to.
/// </para>
/// <para>
/// The reason tokens are declared here and each codec's public <c>Reason*</c> const takes its value from
/// this class, so the strings cannot drift apart. The consts stay public and per codec because the decoder
/// fuzzer reflects them to build its accepted set.
/// </para>
/// </summary>
internal static class ContentCompression
{
    /// <summary>
    /// The stream expands past the length its own container declares. A resource refusal, not a corrupt
    /// stream: the destination is sized from the header exactly so the cost is bounded before the first
    /// byte is written.
    /// </summary>
    internal const string ReasonTooLarge = "chunk-too-large";

    /// <summary>The body is not a stream this decoder can finish reading.</summary>
    internal const string ReasonDecompress = "chunk-decompress";

    /// <summary>
    /// Decompresses <paramref name="stored"/> into <paramref name="destination"/>, which the caller has
    /// sized to the declared uncompressed length. Total: false plus a stable reason token, never a throw.
    /// <para>
    /// The destination must be filled EXACTLY and the input consumed WHOLE. A short fill is a stream that
    /// ended early, and leftover input is bytes nothing in the file names, so both are corrupt rather than
    /// oversized.
    /// </para>
    /// </summary>
    /// <param name="stored">The stored bytes, the body of the file after its header.</param>
    /// <param name="destination">The declared uncompressed length, exactly.</param>
    /// <param name="reason">The refusal token, or null on success.</param>
    internal static bool TryFill(ReadOnlySpan<byte> stored, Span<byte> destination, out string? reason)
    {
        using var decoder = new BrotliDecoder();
        OperationStatus status = decoder.Decompress(stored, destination, out int consumed, out int written);
        if (status == OperationStatus.DestinationTooSmall)
        {
            reason = ReasonTooLarge;
            return false;
        }

        if (status != OperationStatus.Done || written != destination.Length || consumed != stored.Length)
        {
            reason = ReasonDecompress;
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Compresses <paramref name="body"/> into <paramref name="destination"/>, returning false when the
    /// result is not SMALLER than the body, which is the case a chunk stores uncompressed.
    /// <para>
    /// The caller sizes the destination at <c>body.Length</c> rather than at
    /// <see cref="BrotliEncoder.GetMaxCompressedLength"/>, because a compressed form that does not fit in
    /// the body's own length is a form the caller would reject anyway. That makes the scratch buffer the
    /// smaller of the two sizings and removes the copy the larger one needed.
    /// </para>
    /// </summary>
    /// <param name="body">The canonical uncompressed body.</param>
    /// <param name="quality">The Brotli quality, 0 to 11.</param>
    /// <param name="destination">Scratch sized at <c>body.Length</c>.</param>
    /// <param name="written">The compressed length, valid only when this returns true.</param>
    internal static bool TryCompress(
        ReadOnlySpan<byte> body,
        int quality,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (body.Length == 0)
        {
            return false;
        }

        return BrotliEncoder.TryCompress(body, destination, out written, quality, ContentPackFormat.BrotliWindow)
            && written < body.Length;
    }
}
