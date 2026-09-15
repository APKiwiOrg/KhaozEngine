namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The format constants of section 7.1. A measurement copy, not the shipped type: the spike needs the
/// numbers the budgets are taken against, not the registration machinery around them.
/// </summary>
public static class ContentPackFormat
{
    public const ushort ChunkFormatVersion = 1;
    public const ushort ManifestFormatVersion = 1;
    public const ushort RuleChunkFormatVersion = 1;
    public const ushort TextChunkFormatVersion = 1;
    public const int Generation = 1;
    public const int HashSchemeVersion = 1;
    public const int MaxContentRowBytes = 4096;
    public const int DefaultMaxRowBytes = 1024;
    public const int MaxChunkUncompressedBytes = 16 * 1024 * 1024;

    /// <summary>The fixed part of a KECC file, never compressed (section 7.2).</summary>
    public const int ChunkHeaderBytes = 36;

    /// <summary>The fixed part of a KECT file. The language tag adds its own bytes (section 7.6).</summary>
    public const int TextHeaderFixedBytes = 17;

    public const int RuleHeaderBytes = 20;

    public const byte VisibilityClient = 0;
    public const byte VisibilityServerOnly = 1;

    public const byte CompressionNone = 0;
    public const byte CompressionBrotli = 1;

    /// <summary>Section 7.5's recommendation: Brotli at quality 5, window 22.</summary>
    public const int BrotliQuality = 5;
    public const int BrotliWindow = 22;
}
