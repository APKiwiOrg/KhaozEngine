using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Encodes/decodes save data to deter casual tampering: Base64 for obfuscation plus an HMAC-SHA256
/// integrity tag. This is a deterrent, not real security: the HMAC key ships in the game binary.
///
/// <para>v2 format: <c>{prefix}:v2:{hmac-hex}:{meta-base64}:{payload-base64}</c> where the HMAC is
/// computed over the string <c>{meta-base64}:{payload-base64}</c> (the meta segment may be empty when no
/// metadata was supplied). v1 (legacy, read-only): <c>{prefix}:{hmac-hex}:{base64-payload}</c>, HMAC over
/// the base64 payload. Discrimination: the segment after the prefix is the literal <c>v2</c> for v2,
/// otherwise 64 hex chars for v1.</para>
///
/// <para><see cref="TryDecode"/> reports a structured <see cref="SaveDecodeResult"/> and decides nothing.
/// A payload that decodes but fails its HMAC is <see cref="SaveDecodeVerdict.TamperMismatch"/> and still
/// carries the JSON, so the strict-versus-lenient choice lives in the caller. Structural damage (missing
/// separators, bad Base64, empty payload) is <see cref="SaveDecodeVerdict.Malformed"/>. The legacy
/// <see cref="Decode"/> stays a lenient wrapper: it recovers the JSON even on an HMAC mismatch and reports
/// outcomes via an <see cref="ILogger"/> (the ambient <c>Log</c> facade when none is injected).</para>
/// </summary>
public sealed class SaveEncoder
{
    private const char Separator = ':';
    private const string V2Marker = "v2:";

    private readonly byte[] hmacKey;
    private readonly string magicPrefix;
    private readonly ILogger logger;

    /// <summary>Creates an encoder with the given HMAC key and magic prefix. <paramref name="logger"/>
    /// defaults to the ambient <c>Log</c> facade (category <c>SaveEncoder</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="hmacKey"/> is null/empty, or <paramref name="magicPrefix"/> is null/empty/whitespace.</exception>
    public SaveEncoder(byte[] hmacKey, string magicPrefix, ILogger? logger = null)
    {
        if (hmacKey is null || hmacKey.Length == 0)
        {
            throw new ArgumentException("An HMAC key must be provided.", nameof(hmacKey));
        }
        if (string.IsNullOrWhiteSpace(magicPrefix))
        {
            throw new ArgumentException("A magic prefix must be provided.", nameof(magicPrefix));
        }

        this.hmacKey = (byte[])hmacKey.Clone();
        this.magicPrefix = magicPrefix;
        this.logger = logger ?? Log.For<SaveEncoder>();
    }

    /// <summary>Encodes a JSON string into the v2 obfuscated save format, optionally embedding
    /// tamper-protected <paramref name="metadata"/>.</summary>
    public string Encode(string json, SaveMetadata? metadata = null)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string meta = metadata is null ? "" : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata));
        string signed = meta + Separator + payload;
        return $"{magicPrefix}{Separator}{V2Marker}{ComputeHmac(signed)}{Separator}{signed}";
    }

    /// <summary>Returns true if <paramref name="fileContent"/> appears to be in the encoded format.</summary>
    public bool IsEncoded(string fileContent)
    {
        return fileContent.StartsWith(magicPrefix + Separator, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decodes an encoded save into a structured <see cref="SaveDecodeResult"/> without logging or
    /// deciding a policy. Reads both v2 and legacy v1 envelopes. A payload that decodes but fails its
    /// HMAC is reported as <see cref="SaveDecodeVerdict.TamperMismatch"/> with the JSON still populated.
    /// </summary>
    public SaveDecodeResult TryDecode(string fileContent)
    {
        if (!IsEncoded(fileContent))
        {
            return SaveDecodeResult.NotEncoded();
        }

        // IsEncoded guarantees the content starts with magicPrefix + Separator, so the body begins one
        // char past the prefix. Slicing directly (rather than IndexOf) keeps parsing correct even if the
        // prefix itself contains a separator character.
        string rest = fileContent[(magicPrefix.Length + 1)..];
        return rest.StartsWith(V2Marker, StringComparison.Ordinal)
            ? DecodeV2(rest[V2Marker.Length..])
            : DecodeV1(rest);
    }

    /// <summary>
    /// Verifies the envelope's HMAC and returns any embedded metadata, without decoding the payload. On a
    /// v1 envelope the verdict reflects the payload HMAC and the metadata is always null.
    /// </summary>
    public SaveMetadataProbe TryReadMetadata(string fileContent)
    {
        if (!IsEncoded(fileContent))
        {
            return new SaveMetadataProbe(SaveDecodeVerdict.NotEncoded, null);
        }

        string rest = fileContent[(magicPrefix.Length + 1)..];
        return rest.StartsWith(V2Marker, StringComparison.Ordinal)
            ? ProbeV2(rest[V2Marker.Length..])
            : ProbeV1(rest);
    }

    /// <summary>
    /// Decodes an encoded save back to JSON, leniently. Returns null if it is not in the encoded format,
    /// is malformed, or has a corrupt payload. On an HMAC mismatch it still returns the JSON and logs a
    /// warning. Outcomes are logged via the injected logger. This is a thin wrapper over
    /// <see cref="TryDecode"/>.
    /// </summary>
    public string? Decode(string fileContent)
    {
        SaveDecodeResult result = TryDecode(fileContent);
        switch (result.Verdict)
        {
            case SaveDecodeVerdict.Ok:
                logger.Info("save decoded (HMAC ok)");
                return result.Json;
            case SaveDecodeVerdict.TamperMismatch:
                logger.Warn("save decoded but HMAC mismatch - possible tampering");
                return result.Json;
            case SaveDecodeVerdict.Malformed:
                logger.Error(result.Detail ?? "malformed encoded save");
                return null;
            default: // NotEncoded: not our format, quietly ignore (for example a legacy plaintext save).
                return null;
        }
    }

    // body = {hmac}:{meta-base64}:{payload-base64}
    private SaveDecodeResult DecodeV2(string body)
    {
        if (!TrySplitV2(body, out string hmac, out string signed, out string metaB64, out string payloadB64))
        {
            return SaveDecodeResult.Malformed("malformed encoded save (missing separator)");
        }
        if (payloadB64.Length == 0)
        {
            return SaveDecodeResult.Malformed("malformed encoded save (empty payload)");
        }

        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(payloadB64));
        }
        catch (FormatException)
        {
            return SaveDecodeResult.Malformed("failed to decode Base64 payload");
        }

        SaveMetadata? metadata = TryParseMetadata(metaB64);
        bool authentic = string.Equals(hmac, ComputeHmac(signed), StringComparison.OrdinalIgnoreCase);
        return authentic
            ? SaveDecodeResult.Ok(json, metadata)
            : SaveDecodeResult.Tampered(json, metadata, "HMAC mismatch - possible tampering");
    }

    // body = {hmac}:{payload-base64}
    private SaveDecodeResult DecodeV1(string body)
    {
        int colon = body.IndexOf(Separator);
        if (colon < 0)
        {
            return SaveDecodeResult.Malformed("malformed encoded save (missing separator)");
        }

        string hmac = body[..colon];
        string payloadB64 = body[(colon + 1)..];
        if (payloadB64.Length == 0)
        {
            return SaveDecodeResult.Malformed("malformed encoded save (empty payload)");
        }

        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(payloadB64));
        }
        catch (FormatException)
        {
            return SaveDecodeResult.Malformed("failed to decode Base64 payload");
        }

        bool authentic = string.Equals(hmac, ComputeHmac(payloadB64), StringComparison.OrdinalIgnoreCase);
        return authentic
            ? SaveDecodeResult.Ok(json, null)
            : SaveDecodeResult.Tampered(json, null, "HMAC mismatch - possible tampering");
    }

    private SaveMetadataProbe ProbeV2(string body)
    {
        if (!TrySplitV2(body, out string hmac, out string signed, out string metaB64, out _))
        {
            return new SaveMetadataProbe(SaveDecodeVerdict.Malformed, null);
        }

        bool authentic = string.Equals(hmac, ComputeHmac(signed), StringComparison.OrdinalIgnoreCase);
        SaveMetadata? metadata = TryParseMetadata(metaB64);
        return new SaveMetadataProbe(authentic ? SaveDecodeVerdict.Ok : SaveDecodeVerdict.TamperMismatch, metadata);
    }

    private SaveMetadataProbe ProbeV1(string body)
    {
        int colon = body.IndexOf(Separator);
        if (colon < 0)
        {
            return new SaveMetadataProbe(SaveDecodeVerdict.Malformed, null);
        }

        string hmac = body[..colon];
        string payloadB64 = body[(colon + 1)..];
        bool authentic = string.Equals(hmac, ComputeHmac(payloadB64), StringComparison.OrdinalIgnoreCase);
        return new SaveMetadataProbe(authentic ? SaveDecodeVerdict.Ok : SaveDecodeVerdict.TamperMismatch, null);
    }

    // Splits a v2 body into its hmac, the signed {meta}:{payload} string, and the meta/payload segments.
    // Base64 and hex never contain a separator, so the first two ':' are the true segment boundaries.
    // Returns false when either boundary is missing.
    private static bool TrySplitV2(string body, out string hmac, out string signed, out string metaB64, out string payloadB64)
    {
        hmac = "";
        signed = "";
        metaB64 = "";
        payloadB64 = "";

        int firstColon = body.IndexOf(Separator);
        if (firstColon < 0)
        {
            return false;
        }
        hmac = body[..firstColon];
        signed = body[(firstColon + 1)..];

        int metaColon = signed.IndexOf(Separator);
        if (metaColon < 0)
        {
            return false;
        }
        metaB64 = signed[..metaColon];
        payloadB64 = signed[(metaColon + 1)..];
        return true;
    }

    // Best-effort parse of the meta segment. An empty segment (no metadata) or one that is not valid
    // Base64/JSON yields null rather than throwing, so a bad meta segment never fails a decode.
    private static SaveMetadata? TryParseMetadata(string metaB64)
    {
        if (metaB64.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SaveMetadata>(Convert.FromBase64String(metaB64));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string ComputeHmac(string data)
    {
        using HMACSHA256 hmac = new(hmacKey);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }
}
