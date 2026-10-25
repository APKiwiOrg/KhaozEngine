using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Fact]
    public void Verified_comparison_joins_occurrences_when_unique_recipes_split()
    {
        using var baseline = new RendererPerfCompilerPublicationFixture();
        using var candidate = new RendererPerfCompilerPublicationFixture(splitSources: true);
        Assert.Empty(baseline.Publish(RendererPerfPublicationWriter.Files(baseline.Directory)).Failures);
        Assert.Empty(candidate.Publish(RendererPerfPublicationWriter.Files(candidate.Directory)).Failures);
        var before = RendererPerfArtifactVerification.Verify(baseline.Directory, baseline.Report.Manifest.UnitId + ".json");
        var after = RendererPerfArtifactVerification.Verify(candidate.Directory, candidate.Report.Manifest.UnitId + ".json");
        Assert.Equal(2, before.Inventory.Recipes.Length); Assert.Equal(10, after.Inventory.Recipes.Length);
        Assert.Equal(27, after.Inventory.Occurrences.Length);
        var result = RendererPerfComparison.Compare(before, after);
        Assert.True(result.Compatible, string.Join(" ", result.Reasons));
        Assert.Contains(result.Differences, d => d.Path.Contains(".source.", StringComparison.Ordinal) && !d.Incompatible);
    }
    [Fact]
    public void Live_sidecar_refusal_corrects_final_and_retains_partial()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        System.IO.Directory.CreateDirectory(f.Directory);
        var files = new RendererPerfPublicationFiles(f.Directory);
        int corrections = 0;
        var writer = new RendererPerfPublicationWriter((report, final) =>
        {
            files.Write(report, final);
            if (final && report.Publication.FinalUnitPublished) File.Delete(Path.Combine(f.Directory, report.Compiler.InventoryPath!));
            else if (final) corrections++;
        }, _ => Assert.Fail("No stamp can follow failed verification."), directory: f.Directory);
        var result = f.Publish(writer);
        Assert.Single(result.Failures); Assert.Equal(1, corrections);
        Assert.False(result.Report.Publication.FinalUnitPublished); Assert.False(result.Report.Assessment.ValidForPromotion);
        var final = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json")));
        var partial = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".partial.json")));
        Assert.False(final.Publication.FinalUnitPublished); Assert.False(partial.Publication.FinalUnitPublished);
        Assert.Equal(f.Report.Blocks.Length, partial.Blocks.Length); Assert.Equal(2, partial.Pipelines.Length);
    }
    [Fact]
    public void Final_stamp_changes_only_the_two_reserved_accounting_slots()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        System.IO.Directory.CreateDirectory(f.Directory);
        var files = new RendererPerfPublicationFiles(f.Directory);
        byte[] before = [], after = [];
        string path = Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json");
        var writer = new RendererPerfPublicationWriter((report, final) =>
        {
            if (final && report.Publication.FinalUnitPublished) f.Now += 31;
            files.Write(report, final);
        }, report =>
        {
            before = File.ReadAllBytes(path); files.RecordCompletion(report); after = File.ReadAllBytes(path);
        }, directory: f.Directory);
        var result = f.Publish(writer); Assert.Empty(result.Failures);
        Assert.Equal(before.Length, after.Length); Assert.False(before.SequenceEqual(after));
        string json = Encoding.UTF8.GetString(before);
        var slots = new[] { "elapsedMilliseconds", "estimatedCostUsd" }.Select(name =>
        {
            int property = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal);
            int value = json.IndexOf("\"value\":", property, StringComparison.Ordinal) + "\"value\":".Length;
            return Encoding.UTF8.GetByteCount(json.AsSpan(0, value));
        }).ToArray();
        for (int n = 0; n < before.Length; n++)
            if (!slots.Any(start => n >= start && n < start + 32)) Assert.Equal(before[n], after[n]);
        Assert.Equal(result.Report.Publication, RendererPerfReportJson.Parse(after).Publication);
    }
}
