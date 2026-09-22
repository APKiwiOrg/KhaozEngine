using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The format constants every catalog byte is written and read against. A version is bumped and NEVER
/// reused, and a mismatched version is a refusal of the whole record with a reason token rather than a
/// best-effort partial read.
/// </summary>
public static class ContentPackFormat
{
    /// <summary>The <c>KECC</c> chunk file's format version, the first field after its magic.</summary>
    public const ushort ChunkFormatVersion = 1;

    /// <summary>The <c>KECM</c> manifest file's format version.</summary>
    public const ushort ManifestFormatVersion = 1;

    /// <summary>The <c>KECR</c> remap rule chunk's format version.</summary>
    public const ushort RuleChunkFormatVersion = 1;

    /// <summary>The <c>KECT</c> per-language text chunk's format version.</summary>
    public const ushort TextChunkFormatVersion = 1;

    /// <summary>
    /// The ENGINE's own format generation, incremented only when an engine-owned row codec or the pack
    /// format gains a field an older reader cannot skip. It is not a game's to set and not a game's to
    /// bump: a game that changed nothing of its own would otherwise have no way to say that the engine
    /// underneath it did. A reader whose generation is below a manifest's refuses on the same side and in
    /// the same way as a build minimum, the server at boot and the client at the door.
    /// <para>
    /// <b>Generation 2 is the <c>item</c> row gaining its trailing <c>category</c> field.</b> A generation 1
    /// reader stops one field short of the end of every item row a generation 2 publish writes and refuses the
    /// whole chunk, so the rule's test is met: it would be WRONG rather than merely older. Bumping the constant
    /// moves that refusal to the manifest, where it reads as "this pack needs a newer build" instead of as a
    /// malformed row deep in a chunk load.
    /// </para>
    /// <para>
    /// It does not invalidate anything already published. A reader compares its own generation against the
    /// manifest's and refuses only a manifest that is AHEAD of it, so a generation 2 build reads every
    /// generation 1 pack. What it does mean is that the first version a consumer publishes after adopting this
    /// engine is refused by its own older clients, which is the announcement this number exists to make.
    /// </para>
    /// </summary>
    public const int Generation = 2;

    /// <summary>
    /// Folded into every digest, and bumped on any canonicalisation change, on purpose: bumping it
    /// re-digests every published version, which is precisely why it exists.
    /// </summary>
    public const int HashSchemeVersion = 1;

    /// <summary>
    /// The ABSOLUTE row ceiling. No content type may declare a cap above it. The largest realistic engine
    /// row is an item base with three asset references, a tag list and a dozen ints, which is under 450
    /// bytes, so this is headroom rather than a target.
    /// </summary>
    public const int MaxContentRowBytes = 4096;

    /// <summary>A type's row cap when its registration declares none.</summary>
    public const int DefaultMaxRowBytes = 1024;

    /// <summary>
    /// The ceiling on one chunk's UNCOMPRESSED canonical bytes. It is checked from the header alone,
    /// before a body byte is read and before any buffer is sized, because the chunk hash is over the
    /// uncompressed bytes and so cannot be computed until after the decompression it is meant to bound.
    /// </summary>
    public const int MaxChunkUncompressedBytes = 16 * 1024 * 1024;

    /// <summary>The fixed part of a <c>KECC</c> file, never compressed, so a reader learns the type, the
    /// id range, the row count and the two lengths without touching the compressor.</summary>
    public const int ChunkHeaderBytes = 36;

    /// <summary>The fixed part of a <c>KECT</c> file. The language tag adds its own bytes.</summary>
    public const int TextHeaderFixedBytes = 17;

    /// <summary>The fixed part of a <c>KECR</c> file.</summary>
    public const int RuleHeaderBytes = 20;

    /// <summary>A chunk whose rows are visible to a client, the <c>visibility</c> header byte's value 0.</summary>
    public const byte VisibilityClient = 0;

    /// <summary>A chunk the client manifest omits entirely, the <c>visibility</c> header byte's value 1.</summary>
    public const byte VisibilityServerOnly = 1;

    /// <summary>The <c>compression</c> header byte's value for a body stored uncompressed.</summary>
    public const byte CompressionNone = 0;

    /// <summary>The <c>compression</c> header byte's value for a Brotli body.</summary>
    public const byte CompressionBrotli = 1;

    /// <summary>The Brotli quality a publish compresses at, measured against the pack size budget.</summary>
    public const int BrotliQuality = 5;

    /// <summary>The Brotli window a publish compresses at.</summary>
    public const int BrotliWindow = 22;

    /// <summary>The four-character ASCII magic of a chunk file, <c>KECC</c>.</summary>
    public static ReadOnlySpan<byte> ChunkMagic => "KECC"u8;

    /// <summary>The four-character ASCII magic of a manifest file, <c>KECM</c>.</summary>
    public static ReadOnlySpan<byte> ManifestMagic => "KECM"u8;

    /// <summary>The four-character ASCII magic of a remap rule chunk, <c>KECR</c>.</summary>
    public static ReadOnlySpan<byte> RuleChunkMagic => "KECR"u8;

    /// <summary>The four-character ASCII magic of a per-language text chunk, <c>KECT</c>.</summary>
    public static ReadOnlySpan<byte> TextChunkMagic => "KECT"u8;

    /// <summary>The width of every magic, and the fewest bytes a standalone file can be identified from.</summary>
    public const int MagicBytes = 4;
}
