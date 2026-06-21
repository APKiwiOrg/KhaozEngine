using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Sfx;

/// <summary>
/// Computes the idempotency hash for an entry. Inputs: prompt, durationSeconds, promptInfluence, format,
/// channels, model, and the API source output_format. Any change flips the hash, forcing a regenerate.
/// </summary>
public static class SfxHasher
{
    /// <summary>Computes a stable lowercase hex SHA-256 over the generation inputs.</summary>
    public static string Compute(SfxEntry entry, string model, string sourceFormat)
    {
        var sb = new StringBuilder();
        sb.Append("prompt=").Append(entry.Prompt).Append('\n');
        sb.Append("duration=").Append(entry.DurationSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "auto").Append('\n');
        sb.Append("influence=").Append(entry.PromptInfluence?.ToString("R", CultureInfo.InvariantCulture) ?? "default").Append('\n');
        sb.Append("format=").Append(entry.Format).Append('\n');
        sb.Append("channels=").Append(entry.Channels).Append('\n');
        sb.Append("model=").Append(model).Append('\n');
        sb.Append("source=").Append(sourceFormat).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
