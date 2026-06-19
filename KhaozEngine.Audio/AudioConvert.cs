using System;

namespace KhaozEngine.Audio;

/// <summary>Shared sample-format conversions for the audio stack.</summary>
internal static class AudioConvert
{
    /// <summary>Converts a normalized float sample (nominally [-1, 1]) to clamped, rounded 16-bit PCM.</summary>
    public static short ToShort(float f) => (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);
}
