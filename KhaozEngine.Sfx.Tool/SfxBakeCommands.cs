using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KhaozEngine.Sfx;

/// <summary>
/// The reusable command logic behind the <c>ke-sfxbake</c> dotnet tool. Lives in the tool project (which has no
/// runtime library) so it is unit-testable and <c>Program.cs</c> stays a one-liner.
/// </summary>
public static class SfxBakeCommands
{
    const string Usage =
        "Usage: ke-sfxbake bake <manifest.jsonc> [options]\n" +
        "  Manifest-driven bulk SFX generation + bake (ElevenLabs -> ffmpeg/oggenc -> asset tree).\n" +
        "  Options:\n" +
        "    --dry-run             print the plan + estimated credits, generate nothing\n" +
        "    --force               regenerate every entry, ignoring sidecars\n" +
        "    --model <id>          override the ElevenLabs model id\n" +
        "    --source-format <f>   override the API source output_format (e.g. pcm_44100)\n" +
        "  Key: ELEVENLABS_API_KEY env var (not needed for --dry-run).";

    /// <summary>Entry point: builds real dependencies and dispatches. Returns a process exit code (0 = success).</summary>
    public static int Run(string[] args, TextWriter outw, TextWriter errw) =>
        Run(args, outw, errw, BuildRealDependencies());

    /// <summary>Testable core: dispatches with injected <paramref name="deps"/>.</summary>
    public static int Run(string[] args, TextWriter outw, TextWriter errw, SfxBakeDependencies deps)
    {
        if (args.Length == 0) { errw.WriteLine(Usage); return 1; }
        if (args[0] is "--help" or "-h") { outw.WriteLine(Usage); return 0; }
        if (args[0] != "bake") { errw.WriteLine($"Unknown command '{args[0]}'.\n{Usage}"); return 1; }

        return Bake(args, outw, errw, deps);
    }

    static int Bake(string[] args, TextWriter outw, TextWriter errw, SfxBakeDependencies deps)
    {
        string? manifestPath = null;
        bool dryRun = false, force = false;
        string? modelOverride = null, sourceFormatOverride = null;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--dry-run": dryRun = true; break;
                case "--force": force = true; break;
                case "--model": modelOverride = Next(args, ref i, errw, a); break;
                case "--source-format": sourceFormatOverride = Next(args, ref i, errw, a); break;
                default:
                    if (a.StartsWith('-')) return Fail(errw, $"Unknown option '{a}'.\n{Usage}");
                    if (manifestPath is not null) return Fail(errw, $"Unexpected extra argument '{a}'.");
                    manifestPath = a;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestPath)) return Fail(errw, $"bake: manifest path is required.\n{Usage}");
        if (!deps.Fs.FileExists(manifestPath)) return Fail(errw, $"manifest not found: {manifestPath}");

        SfxManifest manifest;
        try
        {
            manifest = SfxManifestParser.Parse(deps.Fs.ReadAllText(manifestPath));
        }
        catch (SfxManifestException ex)
        {
            return Fail(errw, $"manifest error: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(modelOverride)) manifest = manifest with { Model = modelOverride! };
        if (!string.IsNullOrWhiteSpace(sourceFormatOverride)) manifest = manifest with { SourceFormat = sourceFormatOverride! };

        string manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        IReadOnlyList<SfxPlanItem> plan = SfxPlanner.Plan(manifest, manifestDir, force, deps.Fs);

        bool needsVorbis = plan.Any(p => p.Action == SfxAction.Generate && p.Entry.Format == SfxFormat.Ogg);
        VorbisProbeResult? probe = needsVorbis ? deps.ProbeVorbis() : null;

        return dryRun
            ? DryRun(plan, manifest, probe, outw)
            : Execute(plan, manifest, probe, needsVorbis, deps, outw, errw);
    }

    static int DryRun(IReadOnlyList<SfxPlanItem> plan, SfxManifest manifest, VorbisProbeResult? probe, TextWriter outw)
    {
        int gen = 0, credits = 0;
        outw.WriteLine($"Dry run: {plan.Count} entr{(plan.Count == 1 ? "y" : "ies")} (model {manifest.Model}, source {manifest.SourceFormat})");
        foreach (SfxPlanItem p in plan)
        {
            if (p.Action == SfxAction.Generate)
            {
                gen++;
                credits += p.EstimatedCredits;
                outw.WriteLine($"  generate  {p.Entry.Key,-28} {p.Entry.Format.ToString().ToLowerInvariant()}/{p.Entry.Channels.ToString().ToLowerInvariant()}  (~{p.EstimatedCredits} credits, {p.Reason}) -> {p.Entry.Out}");
            }
            else
            {
                outw.WriteLine($"  skip      {p.Entry.Key,-28} ({p.Reason})");
            }
        }
        outw.WriteLine($"Plan: generate {gen}, skip {plan.Count - gen}. Estimated ~{credits} credits (ElevenLabs API list rate, approximate).");
        if (probe is not null)
            outw.WriteLine($"Vorbis encoder: {(probe.Available ? "ok - " + probe.Message : "MISSING - " + probe.Message)}");
        return 0;
    }

    static int Execute(IReadOnlyList<SfxPlanItem> plan, SfxManifest manifest, VorbisProbeResult? probe, bool needsVorbis,
        SfxBakeDependencies deps, TextWriter outw, TextWriter errw)
    {
        var toGenerate = plan.Where(p => p.Action == SfxAction.Generate).ToList();
        if (toGenerate.Count == 0)
        {
            outw.WriteLine($"Nothing to do: all {plan.Count} entr{(plan.Count == 1 ? "y is" : "ies are")} up to date.");
            return 0;
        }

        if (needsVorbis && probe is { Available: false })
            return Fail(errw, probe.Message);

        if (string.IsNullOrWhiteSpace(deps.ApiKey))
            return Fail(errw, "ELEVENLABS_API_KEY is not set. Export your ElevenLabs API key (or use --dry-run).");

        VorbisBackend backend = probe?.Backend ?? VorbisBackend.FfmpegLibvorbis;
        int generated = 0, credits = 0;

        foreach (SfxPlanItem p in plan)
        {
            if (p.Action == SfxAction.Skip)
            {
                outw.WriteLine($"  skip      {p.Entry.Key} ({p.Reason})");
                continue;
            }

            try
            {
                BakeOne(p, manifest, backend, deps);
            }
            catch (Exception ex)
            {
                return Fail(errw, $"failed baking '{p.Entry.Key}': {ex.Message}");
            }

            generated++;
            credits += p.EstimatedCredits;
            outw.WriteLine($"  ok        {p.Entry.Key} -> {p.Entry.Out}");
        }

        outw.WriteLine($"Done: generated {generated}, skipped {plan.Count - generated}, ~{credits} credits.");
        return 0;
    }

    static void BakeOne(SfxPlanItem p, SfxManifest manifest, VorbisBackend backend, SfxBakeDependencies deps)
    {
        SfxEntry e = p.Entry;
        byte[] source = deps.Client.Generate(new SfxGenRequest
        {
            Prompt = e.Prompt,
            DurationSeconds = e.DurationSeconds,
            PromptInfluence = e.PromptInfluence,
            Model = manifest.Model,
            OutputFormat = manifest.SourceFormat,
        });

        string srcTemp = deps.Fs.NewTempPath(SfxSourceFormat.SourceSuffix(manifest.SourceFormat));
        deps.Fs.WriteAllBytes(srcTemp, source);
        try
        {
            deps.Fs.EnsureDirectoryFor(p.OutPath);
            deps.Encoder.Encode(new EncodeRequest
            {
                SourcePath = srcTemp,
                OutPath = p.OutPath,
                Format = e.Format,
                Channels = e.Channels,
                SourceContainer = SfxSourceFormat.ContainerOf(manifest.SourceFormat),
                SourceSampleRate = SfxSourceFormat.SampleRateOf(manifest.SourceFormat),
                SourceChannels = 2, // ElevenLabs emits stereo; -ac downmixes for mono entries
            }, backend);

            deps.Fs.WriteAllText(p.SidecarPath, new SfxSidecar
            {
                Hash = p.Hash,
                Key = e.Key,
                GeneratedUtc = deps.UtcNowIso(),
                Model = manifest.Model,
                SourceFormat = manifest.SourceFormat,
            }.Serialize());
        }
        finally
        {
            deps.Fs.DeleteFile(srcTemp);
        }
    }

    static string? Next(string[] args, ref int i, TextWriter errw, string flag)
    {
        if (i + 1 >= args.Length) { errw.WriteLine($"{flag}: expected a value."); return null; }
        return args[++i];
    }

    static int Fail(TextWriter errw, string message) { errw.WriteLine(message); return 1; }

    static SfxBakeDependencies BuildRealDependencies()
    {
        var runner = new SystemProcessRunner();
        return new SfxBakeDependencies
        {
            Fs = new SystemFileSystem(),
            Client = new ElevenLabsSfxClient(Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")),
            Encoder = new FfmpegAudioEncoder(runner),
            ProbeVorbis = () => VorbisEncoderProbe.Detect(runner),
            ApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY"),
            UtcNowIso = () => DateTime.UtcNow.ToString("O"),
        };
    }
}
