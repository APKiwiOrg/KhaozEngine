namespace KhaozEngine.Persistence;

/// <summary>
/// How <see cref="GameStorage.LoadWithOutcome{T}"/> resolved a load. Distinguishes a clean primary read
/// from the recovery paths (a legacy plaintext read, a fall-back to a backup generation) and from the
/// two default paths (nothing on disk versus everything on disk rejected), so a caller can surface the
/// right message rather than silently swallowing a corrupt save.
/// </summary>
public enum SaveLoadOutcome
{
    /// <summary>The primary file was valid and loaded.</summary>
    Loaded,

    /// <summary>No candidate existed on disk, so a fresh default was returned.</summary>
    FreshDefault,

    /// <summary>The primary was a valid plaintext save read while an encoder was configured (a legacy or hand-edited save). A re-save re-encodes it.</summary>
    LoadedLegacyPlaintext,

    /// <summary>The primary was invalid but a backup generation was valid and loaded. See <see cref="SaveLoadResult{T}.RecoveredGeneration"/>.</summary>
    RecoveredFromBackup,

    /// <summary>At least one candidate existed but every generation was invalid, so a fresh default was returned.</summary>
    RejectedAndDefaulted,
}

/// <summary>
/// Per-generation validity of a candidate save file, as classified by the load probe. Used by the
/// generation-listing surface so a caller can report which slots are usable.
/// </summary>
public enum SaveGenerationValidity
{
    /// <summary>The file decoded (or read, when plaintext) and parsed as JSON.</summary>
    Valid,

    /// <summary>The file decoded but failed its integrity check, or a plaintext save was rejected under the configured policy.</summary>
    Tampered,

    /// <summary>The file was structurally broken: unreadable, a malformed envelope, or non-JSON content.</summary>
    Corrupt,

    /// <summary>No file exists at this generation.</summary>
    Missing,
}

/// <summary>
/// The outcome-reporting result of <see cref="GameStorage.LoadWithOutcome{T}"/>: the loaded (or default)
/// <see cref="Value"/> plus how it was resolved. A load never throws on a bad save, so the
/// <see cref="Outcome"/> is how the caller learns that a recovery or a reset happened.
/// </summary>
/// <typeparam name="T">The loaded value type.</typeparam>
public sealed record SaveLoadResult<T>
{
    /// <summary>The loaded value, or a fresh default when no valid candidate was found.</summary>
    public required T Value { get; init; }

    /// <summary>How the load resolved.</summary>
    public SaveLoadOutcome Outcome { get; init; }

    /// <summary>A human-readable detail for a recovery or a reset (the first failure encountered, or the tamper note on a lenient accept), or null.</summary>
    public string? Detail { get; init; }

    /// <summary>The backup generation the value was recovered from (0 for a primary read or a default), set on <see cref="SaveLoadOutcome.RecoveredFromBackup"/>.</summary>
    public int RecoveredGeneration { get; init; }

    /// <summary>Metadata recovered from the loaded envelope, or null for a plaintext or defaulted load.</summary>
    public SaveMetadata? Metadata { get; init; }
}
