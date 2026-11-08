using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Sfx;

/// <summary>
/// Computes the idempotency hash for an entry. Inputs: prompt, durationSeconds, promptInfluence, format,
/// channels, and the API source output_format. Any change flips the hash, forcing a regenerate. The model is
/// left out so a model change, such as ElevenLabs retiring one, keeps already approved outputs. Re-baking on a
/// new model is a deliberate <c>--force</c>.
/// </summary>
public static class SfxHasher
{
    /// <summary>Hash scheme written to new sidecars.</summary>
    public const int HashVersion = 2;

    /// <summary>
    /// The default model of every tool that wrote scheme 1 sidecars, and so the model hashed for any manifest
    /// that did not set one. A hand-adopted sidecar can record free-text provenance in its model field while
    /// its hash used this value.
    /// </summary>
    public const string LegacyDefaultModel = "eleven_text_to_sound_v2";

    /// <summary>Computes a stable lowercase hex SHA-256 over the generation inputs.</summary>
    public static string Compute(SfxEntry entry, string sourceFormat) => Hash(entry, model: null, sourceFormat);

    /// <summary>
    /// The version 1 hash, which also covered the model. Only used to check sidecars written before
    /// <see cref="HashVersion"/> existed, so it must keep reproducing their bytes exactly.
    /// </summary>
    public static string ComputeLegacy(SfxEntry entry, string model, string sourceFormat) => Hash(entry, model, sourceFormat);

    static string Hash(SfxEntry entry, string? model, string sourceFormat)
    {
        var sb = new StringBuilder();
        sb.Append("prompt=").Append(entry.Prompt).Append('\n');
        sb.Append("duration=").Append(entry.DurationSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "auto").Append('\n');
        sb.Append("influence=").Append(entry.PromptInfluence?.ToString("R", CultureInfo.InvariantCulture) ?? "default").Append('\n');
        sb.Append("format=").Append(entry.Format).Append('\n');
        sb.Append("channels=").Append(entry.Channels).Append('\n');
        if (model is not null) sb.Append("model=").Append(model).Append('\n');
        sb.Append("source=").Append(sourceFormat).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
