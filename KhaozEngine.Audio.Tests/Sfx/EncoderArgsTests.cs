using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class EncoderArgsTests
{
    static EncodeRequest Req(SfxFormat fmt, SfxChannels ch, SfxSourceContainer src = SfxSourceContainer.Mp3) => new()
    {
        SourcePath = "/tmp/src.bin",
        OutPath = "/game/out." + (fmt == SfxFormat.Ogg ? "ogg" : "wav"),
        Format = fmt,
        Channels = ch,
        SourceContainer = src,
    };

    // Returns the token immediately following the first occurrence of `flag` in the arg vector.
    static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Wav_uses_ffmpeg_16bit_pcm_at_44100()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Wav, SfxChannels.Mono), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");

        EncoderCommand step = Assert.Single(plan.Steps);
        Assert.Equal(EncoderArgs.Ffmpeg, step.Exe);
        Assert.Equal("pcm_s16le", ValueAfter(step.Args, "-c:a"));
        Assert.Equal("44100", ValueAfter(step.Args, "-ar"));
        Assert.Equal("1", ValueAfter(step.Args, "-ac"));     // mono
        Assert.Equal("/game/out.wav", step.Args.Last());
    }

    [Fact]
    public void Wav_stereo_keeps_two_channels()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Wav, SfxChannels.Stereo), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");
        Assert.Equal("2", ValueAfter(plan.Steps.Single().Args, "-ac"));
    }

    [Fact]
    public void Ogg_via_ffmpeg_libvorbis_is_single_step()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Ogg, SfxChannels.Mono), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");

        EncoderCommand step = Assert.Single(plan.Steps);
        Assert.Equal(EncoderArgs.Ffmpeg, step.Exe);
        Assert.Equal("libvorbis", ValueAfter(step.Args, "-c:a"));
        Assert.Equal("1", ValueAfter(step.Args, "-ac"));      // mono downmix
        Assert.Equal(SfxDefaults.OggQuality.ToString(), ValueAfter(step.Args, "-q:a"));
        Assert.Equal("/game/out.ogg", step.Args.Last());
    }

    [Fact]
    public void Ogg_stereo_keeps_two_channels()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Ogg, SfxChannels.Stereo), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");
        Assert.Equal("2", ValueAfter(plan.Steps.Single().Args, "-ac"));
    }

    [Fact]
    public void Ogg_via_oggenc_converts_to_wav_then_encodes()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Ogg, SfxChannels.Mono), VorbisBackend.OggEnc, "/tmp/mid.wav");

        Assert.Equal(2, plan.Steps.Count);

        // Step 1: ffmpeg builds a mono 16-bit PCM wav at the intermediate path.
        EncoderCommand toWav = plan.Steps[0];
        Assert.Equal(EncoderArgs.Ffmpeg, toWav.Exe);
        Assert.Equal("pcm_s16le", ValueAfter(toWav.Args, "-c:a"));
        Assert.Equal("1", ValueAfter(toWav.Args, "-ac"));
        Assert.Equal("/tmp/mid.wav", toWav.Args.Last());

        // Step 2: oggenc encodes the wav to the final ogg.
        EncoderCommand toOgg = plan.Steps[1];
        Assert.Equal(EncoderArgs.OggEnc, toOgg.Exe);
        Assert.Equal(SfxDefaults.OggQuality.ToString(), ValueAfter(toOgg.Args, "-q"));
        Assert.Equal("/game/out.ogg", ValueAfter(toOgg.Args, "-o"));
        Assert.Contains("/tmp/mid.wav", toOgg.Args);
    }

    [Fact]
    public void Raw_pcm_source_supplies_input_format_flags_before_input()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Wav, SfxChannels.Mono, SfxSourceContainer.RawPcmS16), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");
        List<string> args = plan.Steps.Single().Args.ToList();

        int fmtIdx = args.IndexOf("s16le");
        int inputIdx = args.IndexOf("-i");
        Assert.True(fmtIdx >= 0, "expected -f s16le for raw pcm input");
        Assert.Equal("-f", args[fmtIdx - 1]);
        Assert.True(fmtIdx < inputIdx, "input format flags must precede -i");
    }

    [Fact]
    public void Mp3_source_has_no_raw_input_format_flag()
    {
        EncoderPlan plan = EncoderArgs.Build(Req(SfxFormat.Wav, SfxChannels.Mono), VorbisBackend.FfmpegLibvorbis, "/tmp/mid.wav");
        Assert.DoesNotContain("s16le", plan.Steps.Single().Args);
    }
}
