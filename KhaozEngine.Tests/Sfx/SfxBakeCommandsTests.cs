using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class SfxBakeCommandsTests
{
    // In-memory filesystem. The fake encoder writes its output here so the planner's idempotency check is real.
    sealed class FakeFs : ISfxFileSystem
    {
        public Dictionary<string, string> Text { get; } = new();
        public Dictionary<string, byte[]> Bytes { get; } = new();
        int _temp;

        public bool FileExists(string path) => Text.ContainsKey(path) || Bytes.ContainsKey(path);
        public string? TryReadText(string path) => Text.TryGetValue(path, out string? v) ? v : null;
        public string ReadAllText(string path) => Text.TryGetValue(path, out string? v) ? v : throw new FileNotFoundException(path);
        public void WriteAllBytes(string path, byte[] data) => Bytes[path] = data;
        public void WriteAllText(string path, string text) => Text[path] = text;
        public void EnsureDirectoryFor(string filePath) { }
        public string NewTempPath(string suffix) => $"/tmp/fake{_temp++}{suffix}";
        public void DeleteFile(string path) { Text.Remove(path); Bytes.Remove(path); }
    }

    sealed class FakeClient : IElevenLabsSfxClient
    {
        readonly FakeFs? _fs;
        public FakeClient(FakeFs? fs = null) => _fs = fs;
        public List<SfxGenRequest> Calls { get; } = new();
        public byte[] Generate(SfxGenRequest request)
        {
            Calls.Add(request);
            return Encoding.UTF8.GetBytes("audio:" + request.Prompt);
        }
    }

    sealed class FakeEncoder : IAudioEncoder
    {
        readonly FakeFs _fs;
        public FakeEncoder(FakeFs fs) => _fs = fs;
        public List<(EncodeRequest req, VorbisBackend backend)> Calls { get; } = new();
        public void Encode(EncodeRequest request, VorbisBackend vorbisBackend)
        {
            Calls.Add((request, vorbisBackend));
            _fs.WriteAllBytes(request.OutPath, new byte[] { 1, 2, 3 }); // pretend ffmpeg produced output
        }
    }

    sealed class Harness
    {
        public FakeFs Fs = new();
        public FakeClient Client;
        public FakeEncoder Encoder;
        public VorbisProbeResult Probe = new() { Available = true, Backend = VorbisBackend.FfmpegLibvorbis, Message = "ok" };
        public string? ApiKey = "test-key";
        public StringWriter Out = new();
        public StringWriter Err = new();

        public Harness()
        {
            Client = new FakeClient(Fs);
            Encoder = new FakeEncoder(Fs);
        }

        public SfxBakeDependencies Deps() => new()
        {
            Fs = Fs,
            Client = Client,
            Encoder = Encoder,
            ProbeVorbis = () => Probe,
            ApiKey = ApiKey,
            UtcNowIso = () => "2026-06-21T00:00:00Z",
        };

        public int Run(params string[] args) => SfxBakeCommands.Run(args, Out, Err, Deps());

        public void WriteManifest(string path, string json) => Fs.WriteAllText(path, json);
    }

    const string ManifestPath = "/game/sfx.manifest.jsonc";

    static string Manifest(string body) => "{ \"sounds\": [ " + body + " ] }";

    static string Abs(string rel) => Path.GetFullPath(Path.Combine("/game", rel));

    [Fact]
    public void Bake_generates_encodes_and_writes_sidecar()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "ui/confirm", "prompt": "blip", "out": "a.ogg" }"""));

        int code = h.Run("bake", ManifestPath);

        Assert.Equal(0, code);
        Assert.Single(h.Client.Calls);
        Assert.Single(h.Encoder.Calls);
        Assert.Equal(SfxFormat.Ogg, h.Encoder.Calls[0].req.Format);
        Assert.True(h.Fs.FileExists(Abs("a.ogg")));
        Assert.True(h.Fs.FileExists(Abs("a.ogg") + ".sfxmeta"));
    }

    [Fact]
    public void Rerun_skips_unchanged_entry_without_calling_api()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));

        Assert.Equal(0, h.Run("bake", ManifestPath));
        Assert.Single(h.Client.Calls);

        // Second run: nothing changed -> no API call.
        Assert.Equal(0, h.Run("bake", ManifestPath));
        Assert.Single(h.Client.Calls); // still 1
    }

    [Fact]
    public void Changed_prompt_triggers_regeneration()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));
        h.Run("bake", ManifestPath);

        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "different", "out": "a.ogg" }"""));
        h.Run("bake", ManifestPath);

        Assert.Equal(2, h.Client.Calls.Count);
    }

    [Fact]
    public void Force_regenerates_even_when_unchanged()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));
        h.Run("bake", ManifestPath);
        h.Run("bake", ManifestPath, "--force");

        Assert.Equal(2, h.Client.Calls.Count);
    }

    [Fact]
    public void Dry_run_spends_nothing_and_prints_plan_with_credits()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""
            { "key": "a", "prompt": "blip", "durationSeconds": 2.0, "out": "a.ogg" },
            { "key": "b", "prompt": "boop", "out": "b.ogg" }
        """));

        int code = h.Run("bake", ManifestPath, "--dry-run");

        Assert.Equal(0, code);
        Assert.Empty(h.Client.Calls);   // spends nothing
        Assert.Empty(h.Encoder.Calls);
        string outText = h.Out.ToString();
        Assert.Contains("generate", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credit", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_run_without_api_key_fails_and_does_not_call_api()
    {
        var h = new Harness { ApiKey = null };
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));

        int code = h.Run("bake", ManifestPath);

        Assert.NotEqual(0, code);
        Assert.Empty(h.Client.Calls);
        Assert.Contains("ELEVENLABS_API_KEY", h.Err.ToString());
    }

    [Fact]
    public void Dry_run_without_api_key_still_works()
    {
        var h = new Harness { ApiKey = null };
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));

        Assert.Equal(0, h.Run("bake", ManifestPath, "--dry-run"));
    }

    [Fact]
    public void Ogg_bake_fails_when_no_vorbis_encoder()
    {
        var h = new Harness { Probe = new VorbisProbeResult { Available = false, Backend = null, Message = "install vorbis-tools" } };
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "out": "a.ogg" }"""));

        int code = h.Run("bake", ManifestPath);

        Assert.NotEqual(0, code);
        Assert.Empty(h.Client.Calls);
        Assert.Contains("vorbis-tools", h.Err.ToString());
    }

    [Fact]
    public void Wav_only_bake_does_not_require_vorbis_encoder()
    {
        var h = new Harness { Probe = new VorbisProbeResult { Available = false, Backend = null, Message = "none" } };
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "format": "wav", "out": "a.wav" }"""));

        Assert.Equal(0, h.Run("bake", ManifestPath));
        Assert.Single(h.Encoder.Calls);
    }

    [Fact]
    public void Missing_manifest_file_fails_clearly()
    {
        var h = new Harness();
        int code = h.Run("bake", "/game/nope.jsonc");

        Assert.NotEqual(0, code);
        Assert.Contains("nope.jsonc", h.Err.ToString());
    }

    [Fact]
    public void No_args_prints_usage_and_fails()
    {
        var h = new Harness();
        Assert.NotEqual(0, h.Run());
        Assert.Contains("ke-sfxbake", h.Err.ToString() + h.Out.ToString());
    }

    [Fact]
    public void Source_format_override_flows_to_api_request()
    {
        var h = new Harness();
        h.WriteManifest(ManifestPath, Manifest("""{ "key": "a", "prompt": "blip", "format": "wav", "out": "a.wav" }"""));

        h.Run("bake", ManifestPath, "--source-format", "pcm_44100");

        Assert.Equal("pcm_44100", h.Client.Calls[0].OutputFormat);
        Assert.Equal(SfxSourceContainer.RawPcmS16, h.Encoder.Calls[0].req.SourceContainer);
    }
}
