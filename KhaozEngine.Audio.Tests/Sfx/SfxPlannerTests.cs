using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Sfx;
using Xunit;

namespace KhaozEngine.Tests.Sfx;

public class SfxPlannerTests
{
    sealed class FakeFs : ISfxFileProbe
    {
        public Dictionary<string, string> Files { get; } = new();
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string? TryReadText(string path) => Files.TryGetValue(path, out string? v) ? v : null;
    }

    const string ManifestDir = "/game";

    static SfxManifest OneEntry(string outRel = "Assets/a.ogg") => new()
    {
        Sounds = new[] { new SfxEntry { Key = "a", Prompt = "p", Out = outRel } },
    };

    static string Abs(string rel) => Path.GetFullPath(Path.Combine(ManifestDir, rel));

    static FakeFs OutputWithSidecar(string hash, int? hashVersion, string? model = null)
    {
        var fs = new FakeFs();
        fs.Files[Abs("Assets/a.ogg")] = "audio-bytes";
        fs.Files[Abs("Assets/a.ogg") + ".sfxmeta"] =
            new SfxSidecar { Hash = hash, Key = "a", HashVersion = hashVersion, Model = model }.Serialize();
        return fs;
    }

    [Fact]
    public void Generates_when_output_missing()
    {
        var fs = new FakeFs();
        SfxPlanItem item = SfxPlanner.Plan(OneEntry(), ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
        Assert.Equal(Abs("Assets/a.ogg"), item.OutPath);
        Assert.Equal(Abs("Assets/a.ogg") + ".sfxmeta", item.SidecarPath);
    }

    // A model change keeps approved outputs: the current scheme leaves the model out, and a scheme 1 sidecar is
    // checked against the model it recorded or, for one adopted by hand with free-text provenance, the scheme 1
    // default.
    [Theory]
    [InlineData("current scheme")]
    [InlineData("scheme 1, recorded model")]
    [InlineData("scheme 1, adopted by hand")]
    public void Skips_when_output_and_matching_sidecar_exist(string sidecar)
    {
        SfxManifest m = OneEntry();
        SfxEntry e = m.Sounds[0];
        string current = SfxHasher.Compute(e, m.SourceFormat);
        FakeFs fs = sidecar switch
        {
            "current scheme" => OutputWithSidecar(current, SfxHasher.HashVersion, m.Model),
            "scheme 1, recorded model" => OutputWithSidecar(
                SfxHasher.ComputeLegacy(e, "recorded_model", m.SourceFormat), null, "recorded_model"),
            _ => OutputWithSidecar(
                SfxHasher.ComputeLegacy(e, SfxHasher.LegacyDefaultModel, m.SourceFormat), null, "picked through the MCP"),
        };

        SfxPlanItem item = SfxPlanner.Plan(m, ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Skip, item.Action);
        Assert.Equal(0, item.EstimatedCredits);
    }

    [Theory]
    [InlineData("current scheme")]
    [InlineData("scheme 1")]
    [InlineData("unknown scheme")]
    public void Generates_when_sidecar_hash_differs(string sidecar)
    {
        SfxManifest m = OneEntry();
        SfxEntry e = m.Sounds[0];
        FakeFs fs = sidecar switch
        {
            "current scheme" => OutputWithSidecar("stale-hash", SfxHasher.HashVersion),
            "scheme 1" => OutputWithSidecar(
                SfxHasher.ComputeLegacy(e with { Prompt = "older prompt" }, SfxHasher.LegacyDefaultModel, m.SourceFormat),
                null, SfxHasher.LegacyDefaultModel),
            _ => OutputWithSidecar(SfxHasher.Compute(e, m.SourceFormat), SfxHasher.HashVersion + 1),
        };

        SfxPlanItem item = SfxPlanner.Plan(m, ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
    }

    [Fact]
    public void Generates_when_output_present_but_sidecar_missing()
    {
        var fs = new FakeFs();
        fs.Files[Abs("Assets/a.ogg")] = "audio-bytes"; // no sidecar

        SfxPlanItem item = SfxPlanner.Plan(OneEntry(), ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
    }

    [Fact]
    public void Force_regenerates_even_when_up_to_date()
    {
        SfxManifest m = OneEntry();
        FakeFs fs = OutputWithSidecar(SfxHasher.Compute(m.Sounds[0], m.SourceFormat), SfxHasher.HashVersion);

        SfxPlanItem item = SfxPlanner.Plan(m, ManifestDir, force: true, fs).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
    }

    [Fact]
    public void Generate_items_carry_estimated_credits()
    {
        SfxManifest m = new() { Sounds = new[] { new SfxEntry { Key = "a", Prompt = "p", DurationSeconds = 2.0, Out = "a.ogg" } } };
        SfxPlanItem item = SfxPlanner.Plan(m, ManifestDir, force: false, new FakeFs()).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
        Assert.Equal(SfxCreditEstimator.Estimate(m.Sounds[0]), item.EstimatedCredits);
        Assert.True(item.EstimatedCredits > 0);
    }

    [Fact]
    public void Estimator_uses_auto_credits_when_duration_omitted()
    {
        Assert.Equal(SfxCreditEstimator.AutoDurationCredits,
            SfxCreditEstimator.Estimate(new SfxEntry { Key = "a", Prompt = "p", Out = "a.ogg" }));
    }

    [Fact]
    public void Estimator_uses_per_second_when_duration_set()
    {
        Assert.Equal(SfxCreditEstimator.CreditsPerSecond * 2,
            SfxCreditEstimator.Estimate(new SfxEntry { Key = "a", Prompt = "p", DurationSeconds = 2.0, Out = "a.ogg" }));
    }
}
