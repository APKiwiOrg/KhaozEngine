namespace KhaozEngine.Persistence;

/// <summary>The outcome of decoding an encoded save envelope.</summary>
public enum SaveDecodeVerdict
{
    /// <summary>Decoded successfully and the HMAC verified.</summary>
    Ok,

    /// <summary>The payload was recovered but its HMAC did not match (possible tampering or a wrong key).</summary>
    TamperMismatch,

    /// <summary>The envelope is structurally broken: a missing separator, an empty payload, or non-Base64 data.</summary>
    Malformed,

    /// <summary>The content is not in the encoded format at all.</summary>
    NotEncoded,
}

/// <summary>
/// The structured result of <see cref="SaveEncoder.TryDecode"/>: a verdict plus whatever could be
/// recovered. On <see cref="SaveDecodeVerdict.TamperMismatch"/> the <see cref="Json"/> is still populated
/// so the caller can choose a lenient recovery policy. Structural failures carry a <see cref="Detail"/>.
/// </summary>
public readonly struct SaveDecodeResult
{
    private SaveDecodeResult(SaveDecodeVerdict verdict, string? json, SaveMetadata? metadata, string? detail)
    {
        Verdict = verdict;
        Json = json;
        Metadata = metadata;
        Detail = detail;
    }

    /// <summary>The decode verdict.</summary>
    public SaveDecodeVerdict Verdict { get; }

    /// <summary>The recovered JSON payload, or null when nothing decodable was present.</summary>
    public string? Json { get; }

    /// <summary>The recovered metadata, or null when none was present or it could not be parsed.</summary>
    public SaveMetadata? Metadata { get; }

    /// <summary>A human-readable detail for a malformed or tampered result, or null.</summary>
    public string? Detail { get; }

    /// <summary>An authentic decode: a verified HMAC, with the JSON and any metadata.</summary>
    public static SaveDecodeResult Ok(string json, SaveMetadata? metadata) =>
        new(SaveDecodeVerdict.Ok, json, metadata, null);

    /// <summary>A payload that decoded but failed HMAC verification. Carries the JSON for lenient recovery.</summary>
    public static SaveDecodeResult Tampered(string json, SaveMetadata? metadata, string detail) =>
        new(SaveDecodeVerdict.TamperMismatch, json, metadata, detail);

    /// <summary>A structurally broken envelope, described by <paramref name="detail"/>.</summary>
    public static SaveDecodeResult Malformed(string detail) =>
        new(SaveDecodeVerdict.Malformed, null, null, detail);

    /// <summary>Content that is not in the encoded format.</summary>
    public static SaveDecodeResult NotEncoded() =>
        new(SaveDecodeVerdict.NotEncoded, null, null, null);
}

/// <summary>
/// The result of <see cref="SaveEncoder.TryReadMetadata"/>: the verdict from verifying the envelope's
/// HMAC plus any metadata recovered, without decoding the payload.
/// </summary>
public readonly struct SaveMetadataProbe
{
    /// <summary>Creates a probe result with the given <paramref name="verdict"/> and <paramref name="metadata"/>.</summary>
    public SaveMetadataProbe(SaveDecodeVerdict verdict, SaveMetadata? metadata)
    {
        Verdict = verdict;
        Metadata = metadata;
    }

    /// <summary>The verdict from verifying the envelope's HMAC.</summary>
    public SaveDecodeVerdict Verdict { get; }

    /// <summary>The recovered metadata, or null when none was present or it could not be parsed.</summary>
    public SaveMetadata? Metadata { get; }
}
