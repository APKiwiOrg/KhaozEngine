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

    [Fact]
    public void Generates_when_output_missing()
    {
        var fs = new FakeFs();
        SfxPlanItem item = SfxPlanner.Plan(OneEntry(), ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Generate, item.Action);
        Assert.Equal(Abs("Assets/a.ogg"), item.OutPath);
        Assert.Equal(Abs("Assets/a.ogg") + ".sfxmeta", item.SidecarPath);
    }

    [Fact]
    public void Skips_when_output_and_matching_sidecar_exist()
    {
        SfxManifest m = OneEntry();
        string hash = SfxHasher.Compute(m.Sounds[0], m.Model, m.SourceFormat);
        var fs = new FakeFs();
        fs.Files[Abs("Assets/a.ogg")] = "audio-bytes";
        fs.Files[Abs("Assets/a.ogg") + ".sfxmeta"] = new SfxSidecar { Hash = hash, Key = "a" }.Serialize();

        SfxPlanItem item = SfxPlanner.Plan(m, ManifestDir, force: false, fs).Single();

        Assert.Equal(SfxAction.Skip, item.Action);
        Assert.Equal(0, item.EstimatedCredits);
    }

    [Fact]
    public void Generates_when_sidecar_hash_differs()
    {
        SfxManifest m = OneEntry();
        var fs = new FakeFs();
        fs.Files[Abs("Assets/a.ogg")] = "audio-bytes";
        fs.Files[Abs("Assets/a.ogg") + ".sfxmeta"] = new SfxSidecar { Hash = "stale-hash", Key = "a" }.Serialize();

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
        string hash = SfxHasher.Compute(m.Sounds[0], m.Model, m.SourceFormat);
        var fs = new FakeFs();
        fs.Files[Abs("Assets/a.ogg")] = "audio-bytes";
        fs.Files[Abs("Assets/a.ogg") + ".sfxmeta"] = new SfxSidecar { Hash = hash, Key = "a" }.Serialize();

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
