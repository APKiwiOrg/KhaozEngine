using System;
using System.Collections.Generic;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class VorbisEncoderProbeTests
{
    const string EncodersWithLibvorbis = """
    Encoders:
     V..... = Video
     A..... = Audio
     ------
     A....D libopus              libopus Opus
     A....D libvorbis            libvorbis (codec vorbis)
     A....D vorbis               Vorbis
    """;

    const string EncodersWithoutLibvorbis = """
    Encoders:
     A....D vorbis               Vorbis
     A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3)
    """;

    sealed class FakeRunner : IProcessRunner
    {
        public HashSet<string> Tools { get; } = new();
        public string EncodersOutput { get; set; } = "";
        public bool ToolExists(string exe) => Tools.Contains(exe);
        public ProcessResult Run(string exe, IReadOnlyList<string> args) => new(0, EncodersOutput, "");
    }

    [Fact]
    public void HasLibvorbis_true_when_listed()
    {
        Assert.True(VorbisEncoderProbe.HasLibvorbis(EncodersWithLibvorbis));
    }

    [Fact]
    public void HasLibvorbis_false_when_only_builtin_vorbis()
    {
        Assert.False(VorbisEncoderProbe.HasLibvorbis(EncodersWithoutLibvorbis));
    }

    [Fact]
    public void Detect_prefers_ffmpeg_libvorbis()
    {
        var runner = new FakeRunner { EncodersOutput = EncodersWithLibvorbis };
        runner.Tools.Add("ffmpeg");
        runner.Tools.Add("oggenc");

        VorbisProbeResult r = VorbisEncoderProbe.Detect(runner);

        Assert.True(r.Available);
        Assert.Equal(VorbisBackend.FfmpegLibvorbis, r.Backend);
    }

    [Fact]
    public void Detect_falls_back_to_oggenc_when_ffmpeg_lacks_libvorbis()
    {
        var runner = new FakeRunner { EncodersOutput = EncodersWithoutLibvorbis };
        runner.Tools.Add("ffmpeg");
        runner.Tools.Add("oggenc");

        VorbisProbeResult r = VorbisEncoderProbe.Detect(runner);

        Assert.True(r.Available);
        Assert.Equal(VorbisBackend.OggEnc, r.Backend);
    }

    [Fact]
    public void Detect_uses_oggenc_when_ffmpeg_absent()
    {
        var runner = new FakeRunner();
        runner.Tools.Add("oggenc");

        VorbisProbeResult r = VorbisEncoderProbe.Detect(runner);

        Assert.True(r.Available);
        Assert.Equal(VorbisBackend.OggEnc, r.Backend);
    }

    [Fact]
    public void Detect_unavailable_with_remediation_when_nothing_usable()
    {
        var runner = new FakeRunner { EncodersOutput = EncodersWithoutLibvorbis };
        runner.Tools.Add("ffmpeg"); // present but no libvorbis, and no oggenc

        VorbisProbeResult r = VorbisEncoderProbe.Detect(runner);

        Assert.False(r.Available);
        Assert.Null(r.Backend);
        Assert.Contains("vorbis-tools", r.Message);
    }
}
