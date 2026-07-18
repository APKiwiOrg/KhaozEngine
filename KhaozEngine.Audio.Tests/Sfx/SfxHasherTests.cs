using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class SfxHasherTests
{
    static SfxEntry Entry() => new()
    {
        Key = "ui/confirm",
        Prompt = "crisp blip",
        DurationSeconds = 1.2,
        PromptInfluence = 0.4,
        Format = SfxFormat.Ogg,
        Channels = SfxChannels.Mono,
        Out = "ui/confirm.ogg",
    };

    [Fact]
    public void Hash_is_stable_for_identical_inputs()
    {
        Assert.Equal(
            SfxHasher.Compute(Entry(), SfxDefaults.Model, SfxDefaults.SourceFormat),
            SfxHasher.Compute(Entry(), SfxDefaults.Model, SfxDefaults.SourceFormat));
    }

    [Fact]
    public void Hash_is_hex_sha256()
    {
        string h = SfxHasher.Compute(Entry(), SfxDefaults.Model, SfxDefaults.SourceFormat);
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("duration")]
    [InlineData("influence")]
    [InlineData("format")]
    [InlineData("channels")]
    [InlineData("model")]
    [InlineData("source")]
    public void Hash_changes_when_any_input_changes(string field)
    {
        string baseline = SfxHasher.Compute(Entry(), SfxDefaults.Model, SfxDefaults.SourceFormat);

        SfxEntry e = Entry();
        string model = SfxDefaults.Model, source = SfxDefaults.SourceFormat;
        switch (field)
        {
            case "prompt": e = e with { Prompt = "different" }; break;
            case "duration": e = e with { DurationSeconds = 2.0 }; break;
            case "influence": e = e with { PromptInfluence = 0.9 }; break;
            case "format": e = e with { Format = SfxFormat.Wav }; break;
            case "channels": e = e with { Channels = SfxChannels.Stereo }; break;
            case "model": model = "other_model"; break;
            case "source": source = "pcm_44100"; break;
        }

        Assert.NotEqual(baseline, SfxHasher.Compute(e, model, source));
    }

    [Fact]
    public void Out_path_does_not_affect_hash()
    {
        string a = SfxHasher.Compute(Entry(), SfxDefaults.Model, SfxDefaults.SourceFormat);
        string b = SfxHasher.Compute(Entry() with { Out = "somewhere/else.ogg" }, SfxDefaults.Model, SfxDefaults.SourceFormat);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Sidecar_round_trips()
    {
        var s = new SfxSidecar { Hash = "abc123", Key = "ui/confirm", GeneratedUtc = "2026-06-21T00:00:00Z", Model = "m", SourceFormat = "mp3_44100_192" };
        SfxSidecar? back = SfxSidecar.TryParse(s.Serialize());
        Assert.NotNull(back);
        Assert.Equal("abc123", back!.Hash);
        Assert.Equal("ui/confirm", back.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void Sidecar_tryparse_returns_null_for_unreadable(string? text)
    {
        Assert.Null(SfxSidecar.TryParse(text));
    }
}
