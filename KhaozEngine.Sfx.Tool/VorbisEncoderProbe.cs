using System;

namespace KhaozEngine.Sfx;

/// <summary>Outcome of probing for a usable Vorbis encoder.</summary>
public sealed record VorbisProbeResult
{
    /// <summary>True if a usable encoder was found.</summary>
    public required bool Available { get; init; }
    /// <summary>The selected backend (null when none available).</summary>
    public VorbisBackend? Backend { get; init; }
    /// <summary>Human-readable status / remediation message.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Preflight detection of a usable Vorbis encoder. Stock Homebrew ffmpeg ships no libvorbis (its built-in
/// <c>vorbis</c> encoder is stereo-only and low quality), so we require either an ffmpeg built with libvorbis
/// or <c>oggenc</c> from vorbis-tools, and fail loudly otherwise rather than emitting bad/stereo-only OGG.
/// </summary>
public static class VorbisEncoderProbe
{
    const string Remediation =
        "no usable Vorbis encoder found. Install vorbis-tools (brew install vorbis-tools) or an ffmpeg built " +
        "with libvorbis. Stock Homebrew ffmpeg has no libvorbis and its built-in 'vorbis' encoder is " +
        "stereo-only and low quality, so it is not used.";

    /// <summary>True if <c>ffmpeg -encoders</c> output advertises the libvorbis encoder (not the bare built-in).</summary>
    public static bool HasLibvorbis(string ffmpegEncodersOutput)
    {
        if (string.IsNullOrEmpty(ffmpegEncodersOutput)) return false;
        foreach (string line in ffmpegEncodersOutput.Split('\n'))
        {
            if (line.Contains("libvorbis", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Detects the best available backend using <paramref name="runner"/>.</summary>
    public static VorbisProbeResult Detect(IProcessRunner runner)
    {
        if (runner.ToolExists(EncoderArgs.Ffmpeg))
        {
            ProcessResult r = runner.Run(EncoderArgs.Ffmpeg, new[] { "-hide_banner", "-encoders" });
            if (HasLibvorbis(r.StdOut + "\n" + r.StdErr))
                return new VorbisProbeResult { Available = true, Backend = VorbisBackend.FfmpegLibvorbis, Message = "ffmpeg with libvorbis" };
        }

        if (runner.ToolExists(EncoderArgs.OggEnc))
            return new VorbisProbeResult { Available = true, Backend = VorbisBackend.OggEnc, Message = "oggenc (vorbis-tools)" };

        return new VorbisProbeResult { Available = false, Backend = null, Message = Remediation };
    }
}
