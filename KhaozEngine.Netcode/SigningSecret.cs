using System;
using System.Security.Cryptography;

namespace KhaozEngine.Netcode;

/// <summary>
/// Loads and validates the HMAC key <see cref="SignedToken"/> mints and verifies under. It sits beside the token
/// because both ends need it: the auth service mints under the key and the game server verifies under it, and the
/// server must be able to load its key without referencing anything that mints.
/// <para>
/// Three outcomes, and the middle one is the point. Unset or blank is <c>null</c>, which a host reads as local
/// development. Set and valid is the decoded key. Set but INVALID throws, because a production key that quietly
/// failed to parse would degrade to a door that verifies nothing, or to a key short enough to brute-force, and
/// nobody would notice until an unsigned client walked in.
/// </para>
/// <para>
/// The value is standard base64 (the output of <c>openssl rand -base64 32</c>), trimmed, decoding to at least
/// <see cref="MinimumBytes"/>. Every error names the SOURCE (the variable or setting the value came from) and the
/// rule it broke, and never any part of the value. The variable names stay game-owned.
/// </para>
/// </summary>
public static class SigningSecret
{
    /// <summary>
    /// The shortest key accepted, in bytes. <see cref="SignedToken"/> signs with HMAC-SHA256, whose output is 256
    /// bits, so a shorter key is the weakest link in the signature.
    /// </summary>
    public const int MinimumBytes = 32;

    private const string GenerateHint = "Generate a key with: openssl rand -base64 32";

    /// <summary>
    /// Decodes <paramref name="raw"/>. Returns <c>null</c> when it is null, empty or whitespace. Otherwise returns
    /// the decoded key, a fresh array the caller owns.
    /// </summary>
    /// <param name="raw">The configured value, as read.</param>
    /// <param name="sourceName">Where the value came from, for the error message. Never the value itself.</param>
    /// <exception cref="InvalidOperationException">The value is set but is not standard base64, or decodes to fewer
    /// than <see cref="MinimumBytes"/>. The message names <paramref name="sourceName"/> and not the value.</exception>
    public static byte[]? Decode(string? raw, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string trimmed = raw.Trim();
        // An upper bound on the decoded length. TryFromBase64String reports failure rather than throwing, so no
        // decoder exception (and nothing it might quote) ever reaches the caller.
        byte[] buffer = new byte[((trimmed.Length * 3) + 3) / 4];
        try
        {
            if (!Convert.TryFromBase64String(trimmed, buffer, out int written))
                throw new InvalidOperationException(
                    $"{sourceName} is set but is not valid standard base64. {GenerateHint}");

            if (written < MinimumBytes)
                throw new InvalidOperationException(
                    $"{sourceName} decodes to {written} bytes, under the {MinimumBytes}-byte minimum. A shorter HMAC " +
                    $"key is brute-forceable, so this refuses rather than running with it. {GenerateHint}");

            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            // The working buffer held key material, and on a refusal it held the rejected value's bytes.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// <see cref="Decode"/> of <paramref name="read"/>(<paramref name="variable"/>), with the variable as the source
    /// name. The environment arrives as a delegate, so a host running in process (a solo playtest, a boot smoke, a
    /// test) hands it a map and nothing writes process-wide state. Production passes
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </summary>
    /// <param name="read">Reads one variable, returning null when it is not set.</param>
    /// <param name="variable">The game's own variable name, for example <c>MYGAME_TOKEN_SECRET</c>.</param>
    /// <exception cref="InvalidOperationException">As <see cref="Decode"/>.</exception>
    public static byte[]? Load(Func<string, string?> read, string variable)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        return Decode(read(variable), variable);
    }

    /// <summary>
    /// A fresh <see cref="MinimumBytes"/> key from <see cref="RandomNumberGenerator"/>, for LOCAL DEVELOPMENT
    /// only: a key that exists in one process and cannot leave it, so a dev composition never needs a source
    /// constant. Every token minted under it dies with the process.
    /// </summary>
    public static byte[] CreateEphemeral() => RandomNumberGenerator.GetBytes(MinimumBytes);
}
