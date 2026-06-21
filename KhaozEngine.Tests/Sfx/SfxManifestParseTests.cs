using System.Linq;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class SfxManifestParseTests
{
    const string WithCommentsAndTrailingCommas = """
    {
        // global settings
        "model": "eleven_text_to_sound_v2",
        "sounds": [
            {
                "key": "ui/confirm",
                "prompt": "crisp sci-fi UI confirm blip, short synth tail",
                "durationSeconds": 1.2,
                "promptInfluence": 0.4,
                "format": "ogg",
                "channels": "mono",
                "out": "Assets/Sfx/ui/confirm.ogg", // trailing comma below is allowed
            },
            {
                "key": "ui/error",
                "prompt": "harsh denied buzzer",
                "out": "Assets/Sfx/ui/error.ogg",
            },
        ],
    }
    """;

    [Fact]
    public void Parses_jsonc_with_comments_and_trailing_commas()
    {
        SfxManifest m = SfxManifestParser.Parse(WithCommentsAndTrailingCommas);

        Assert.Equal(2, m.Sounds.Count);
        SfxEntry confirm = m.Sounds[0];
        Assert.Equal("ui/confirm", confirm.Key);
        Assert.Equal("crisp sci-fi UI confirm blip, short synth tail", confirm.Prompt);
        Assert.Equal(1.2, confirm.DurationSeconds);
        Assert.Equal(0.4, confirm.PromptInfluence);
        Assert.Equal(SfxFormat.Ogg, confirm.Format);
        Assert.Equal(SfxChannels.Mono, confirm.Channels);
        Assert.Equal("Assets/Sfx/ui/confirm.ogg", confirm.Out);
    }

    [Fact]
    public void Applies_defaults_when_optional_fields_omitted()
    {
        SfxManifest m = SfxManifestParser.Parse(WithCommentsAndTrailingCommas);
        SfxEntry error = m.Sounds[1];

        Assert.Null(error.DurationSeconds);
        Assert.Null(error.PromptInfluence);
        Assert.Equal(SfxFormat.Ogg, error.Format);     // default
        Assert.Equal(SfxChannels.Mono, error.Channels); // default
    }

    [Fact]
    public void Applies_global_model_and_sourceformat_defaults()
    {
        SfxManifest m = SfxManifestParser.Parse("""{ "sounds": [ { "key": "a", "prompt": "p", "out": "a.ogg" } ] }""");

        Assert.Equal(SfxDefaults.Model, m.Model);
        Assert.Equal(SfxDefaults.SourceFormat, m.SourceFormat);
    }

    [Fact]
    public void Parses_wav_and_stereo_overrides()
    {
        SfxManifest m = SfxManifestParser.Parse("""
        { "sounds": [ { "key": "amb", "prompt": "p", "format": "wav", "channels": "stereo", "out": "amb.wav" } ] }
        """);

        Assert.Equal(SfxFormat.Wav, m.Sounds[0].Format);
        Assert.Equal(SfxChannels.Stereo, m.Sounds[0].Channels);
    }

    [Theory]
    [InlineData("""{ "sounds": [ { "prompt": "p", "out": "a.ogg" } ] }""")]      // missing key
    [InlineData("""{ "sounds": [ { "key": "a", "out": "a.ogg" } ] }""")]         // missing prompt
    [InlineData("""{ "sounds": [ { "key": "a", "prompt": "p" } ] }""")]          // missing out
    public void Throws_on_missing_required_field(string json)
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse(json));
    }

    [Fact]
    public void Throws_on_unknown_format()
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse(
            """{ "sounds": [ { "key": "a", "prompt": "p", "format": "flac", "out": "a.flac" } ] }"""));
    }

    [Fact]
    public void Throws_on_duplicate_key()
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse("""
        { "sounds": [
            { "key": "dup", "prompt": "p", "out": "a.ogg" },
            { "key": "dup", "prompt": "q", "out": "b.ogg" }
        ] }
        """));
    }

    [Fact]
    public void Throws_on_duration_out_of_range()
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse(
            """{ "sounds": [ { "key": "a", "prompt": "p", "durationSeconds": 99, "out": "a.ogg" } ] }"""));
    }

    [Fact]
    public void Throws_on_prompt_influence_out_of_range()
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse(
            """{ "sounds": [ { "key": "a", "prompt": "p", "promptInfluence": 1.5, "out": "a.ogg" } ] }"""));
    }

    [Fact]
    public void Throws_on_malformed_json()
    {
        Assert.Throws<SfxManifestException>(() => SfxManifestParser.Parse("{ not valid"));
    }
}
