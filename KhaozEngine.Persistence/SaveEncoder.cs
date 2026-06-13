using System;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Encodes/decodes save data to deter casual tampering: Base64 for obfuscation plus an
/// HMAC-SHA256 integrity tag. File format: <c>{prefix}:{hmac-hex}:{base64-payload}</c>.
/// This is a deterrent, not real security: the HMAC key ships in the game binary. Decoding is
/// lenient (recovers the JSON even on an HMAC mismatch) and reports outcomes via an
/// <see cref="ILogger"/> (the ambient <c>Log</c> facade when none is injected).
/// </summary>
public sealed class SaveEncoder
{
    private const char Separator = ':';

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

    /// <summary>Encodes a JSON string into the obfuscated save format.</summary>
    public string Encode(string json)
    {
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string hmac = ComputeHmac(base64);
        return $"{magicPrefix}{Separator}{hmac}{Separator}{base64}";
    }

    /// <summary>Returns true if <paramref name="fileContent"/> appears to be in the encoded format.</summary>
    public bool IsEncoded(string fileContent)
    {
        return fileContent.StartsWith(magicPrefix + Separator, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decodes an encoded save back to JSON. Returns null if it is not in the encoded format, is
    /// malformed, or has a corrupt payload. On an HMAC mismatch it still returns the JSON (lenient)
    /// and logs a warning. Outcomes are logged via the injected logger.
    /// </summary>
    public string? Decode(string fileContent)
    {
        if (!IsEncoded(fileContent))
        {
            return null; // not our format; quietly ignore (e.g. legacy plaintext save)
        }

        // IsEncoded guarantees the content starts with magicPrefix + Separator, so the first
        // separator is exactly at magicPrefix.Length. Computing it directly (rather than IndexOf)
        // keeps parsing correct even if the prefix itself contained a separator character.
        int firstSep = magicPrefix.Length;
        int secondSep = fileContent.IndexOf(Separator, firstSep + 1);
        if (secondSep < 0)
        {
            logger.Error("malformed encoded save (missing separator)");
            return null;
        }

        string hmac = fileContent[(firstSep + 1)..secondSep];
        string base64 = fileContent[(secondSep + 1)..];
        if (base64.Length == 0)
        {
            logger.Error("malformed encoded save (empty payload)");
            return null;
        }

        bool authentic = string.Equals(hmac, ComputeHmac(base64), StringComparison.OrdinalIgnoreCase);

        string json;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            json = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            logger.Error("failed to decode Base64 payload");
            return null;
        }

        if (authentic)
        {
            logger.Info("save decoded (HMAC ok)");
        }
        else
        {
            logger.Warn("save decoded but HMAC mismatch - possible tampering");
        }

        return json;
    }

    private string ComputeHmac(string data)
    {
        using HMACSHA256 hmac = new(hmacKey);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }
}
