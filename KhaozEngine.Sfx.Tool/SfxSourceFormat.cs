using System;
using System.Globalization;

namespace KhaozEngine.Sfx;

/// <summary>
/// Maps an ElevenLabs API <c>output_format</c> string (e.g. <c>mp3_44100_192</c>, <c>pcm_44100</c>) to the
/// source-container descriptor the encoder needs.
/// </summary>
public static class SfxSourceFormat
{
    /// <summary>True if the format string names a raw (headerless) PCM output.</summary>
    public static bool IsRawPcm(string apiFormat) =>
        apiFormat.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The container kind for an API format string.</summary>
    public static SfxSourceContainer ContainerOf(string apiFormat) =>
        IsRawPcm(apiFormat) ? SfxSourceContainer.RawPcmS16 : SfxSourceContainer.Mp3;

    /// <summary>Sample rate encoded in the format string (e.g. 44100), or 44100 if it cannot be parsed.</summary>
    public static int SampleRateOf(string apiFormat)
    {
        string[] parts = apiFormat.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate)
            ? rate
            : 44100;
    }

    /// <summary>File suffix for a temp source file of this format (".mp3" or ".pcm").</summary>
    public static string SourceSuffix(string apiFormat) => IsRawPcm(apiFormat) ? ".pcm" : ".mp3";
}
