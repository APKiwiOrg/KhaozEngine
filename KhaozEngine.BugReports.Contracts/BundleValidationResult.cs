namespace KhaozEngine.BugReports.Contracts;

public sealed record BundleValidationResult(bool IsValid, string? Error, BugReportManifest? Manifest);

// Stable error codes. BundleValidationResult.Error is "<code>" or "<code>: <entry name>", with the
// name sanitised as the package README describes.
public static class BundleErrors
{
    public const string TooLarge = "too_large";
    public const string NotAZip = "not_a_zip";
    public const string TooManyEntries = "too_many_entries";
    public const string EntryNotAllowed = "entry_not_allowed";
    public const string DuplicateEntry = "duplicate_entry";
    public const string CompressedEntry = "compressed_entry";
    public const string EncryptedEntry = "encrypted_entry";
    public const string OverlappingEntries = "overlapping_entries";
    public const string ManifestMissing = "manifest_missing";
    public const string ManifestInvalid = "manifest_invalid";
    public const string FormatVersion = "format_version";
    public const string ReportIdInvalid = "report_id_invalid";
    public const string GameIdInvalid = "game_id_invalid";
    public const string DescriptionInvalid = "description_invalid";
    public const string CategoryInvalid = "category_invalid";
    public const string EntryMismatch = "entry_mismatch";
    public const string SizeMismatch = "size_mismatch";
    public const string HashMismatch = "hash_mismatch";
    public const string ImageInvalid = "image_invalid";
    public const string MediaInvalid = "media_invalid";
    public const string ClipIndexInvalid = "clip_index_invalid";
    public const string SectionInvalid = "section_invalid";
    public const string SectionsTooLarge = "sections_too_large";
}
