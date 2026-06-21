using System;
using System.Collections.Generic;

namespace KhaozEngine.Sfx;

/// <summary>Container of the API source bytes handed to the encoder.</summary>
public enum SfxSourceContainer
{
    /// <summary>Self-describing mp3 (e.g. mp3_44100_192) - ffmpeg detects rate/channels.</summary>
    Mp3,
    /// <summary>Headerless signed 16-bit little-endian PCM (e.g. pcm_44100) - rate/channels must be supplied.</summary>
    RawPcmS16,
}

/// <summary>Which Vorbis encoder the preflight probe selected for OGG output.</summary>
public enum VorbisBackend
{
    /// <summary>An ffmpeg built with libvorbis (can encode mono at quality).</summary>
    FfmpegLibvorbis,
    /// <summary>The standalone <c>oggenc</c> from vorbis-tools (fed a WAV by ffmpeg first).</summary>
    OggEnc,
}

/// <summary>One external command invocation: an executable and its argument vector.</summary>
public sealed record EncoderCommand(string Exe, IReadOnlyList<string> Args);

/// <summary>The ordered external commands that convert one source file into the target output.</summary>
public sealed record EncoderPlan(IReadOnlyList<EncoderCommand> Steps);

/// <summary>What to encode: source file + container, and the desired target.</summary>
public sealed record EncodeRequest
{
    /// <summary>Path to the raw API source bytes on disk.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Final output path.</summary>
    public required string OutPath { get; init; }
    /// <summary>Target container.</summary>
    public required SfxFormat Format { get; init; }
    /// <summary>Target channel layout.</summary>
    public required SfxChannels Channels { get; init; }
    /// <summary>Source container. Defaults to mp3 (the default API source format).</summary>
    public SfxSourceContainer SourceContainer { get; init; } = SfxSourceContainer.Mp3;
    /// <summary>Source sample rate, used only for headerless raw PCM input.</summary>
    public int SourceSampleRate { get; init; } = 44100;
    /// <summary>Source channel count, used only for headerless raw PCM input (ElevenLabs emits stereo).</summary>
    public int SourceChannels { get; init; } = 2;
    /// <summary>OGG Vorbis quality.</summary>
    public int OggQuality { get; init; } = SfxDefaults.OggQuality;
}

/// <summary>
/// Pure construction of the ffmpeg / oggenc command lines that implement the engine SFX format policy:
/// OGG Vorbis at quality, or 16-bit PCM 44.1 kHz WAV, downmixed to mono when requested. Kept side-effect-free
/// so it is unit-tested without running any process.
/// </summary>
public static class EncoderArgs
{
    /// <summary>The ffmpeg executable name (resolved on PATH at run time).</summary>
    public const string Ffmpeg = "ffmpeg";
    /// <summary>The oggenc executable name (resolved on PATH at run time).</summary>
    public const string OggEnc = "oggenc";

    /// <summary>
    /// Builds the command plan. <paramref name="vorbisBackend"/> and <paramref name="intermediateWavPath"/>
    /// are only consulted for OGG output.
    /// </summary>
    public static EncoderPlan Build(EncodeRequest request, VorbisBackend vorbisBackend, string intermediateWavPath)
    {
        int channels = request.Channels == SfxChannels.Mono ? 1 : 2;

        return request.Format switch
        {
            SfxFormat.Wav => new EncoderPlan(new[] { FfmpegToWav(request, channels, request.OutPath) }),
            SfxFormat.Ogg when vorbisBackend == VorbisBackend.FfmpegLibvorbis =>
                new EncoderPlan(new[] { FfmpegToOgg(request, channels) }),
            SfxFormat.Ogg => new EncoderPlan(new[]
            {
                FfmpegToWav(request, channels, intermediateWavPath),
                OggEncFromWav(request, intermediateWavPath),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    // ffmpeg input-side flags: raw PCM is headerless so its rate/channels must be declared before -i.
    static void AppendInput(List<string> args, EncodeRequest r)
    {
        args.Add("-y");
        if (r.SourceContainer == SfxSourceContainer.RawPcmS16)
        {
            args.Add("-f"); args.Add("s16le");
            args.Add("-ar"); args.Add(r.SourceSampleRate.ToString());
            args.Add("-ac"); args.Add(r.SourceChannels.ToString());
        }
        args.Add("-i"); args.Add(r.SourcePath);
    }

    static EncoderCommand FfmpegToWav(EncodeRequest r, int channels, string outPath)
    {
        var args = new List<string>();
        AppendInput(args, r);
        args.Add("-ar"); args.Add(SfxDefaults.WavSampleRate.ToString());
        args.Add("-ac"); args.Add(channels.ToString());
        args.Add("-c:a"); args.Add("pcm_s16le");
        args.Add("-f"); args.Add("wav");
        args.Add(outPath);
        return new EncoderCommand(Ffmpeg, args);
    }

    static EncoderCommand FfmpegToOgg(EncodeRequest r, int channels)
    {
        var args = new List<string>();
        AppendInput(args, r);
        args.Add("-ac"); args.Add(channels.ToString());
        args.Add("-c:a"); args.Add("libvorbis");
        args.Add("-q:a"); args.Add(r.OggQuality.ToString());
        args.Add(r.OutPath);
        return new EncoderCommand(Ffmpeg, args);
    }

    static EncoderCommand OggEncFromWav(EncodeRequest r, string wavPath) =>
        new(OggEnc, new[] { "-Q", "-q", r.OggQuality.ToString(), "-o", r.OutPath, wavPath });
}
