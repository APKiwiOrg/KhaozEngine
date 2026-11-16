using System.IO;
using System.Linq;
using KhaozEngine.Tools.Coverage;
using Xunit;
using Step = KhaozEngine.Tools.Coverage.CoveragePublicationStep;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageArtifactPublicationTests
{
    // The new evidence set shares its payload, and so its generation directory, with the old one. A handled
    // failure at any step must leave the old outputs, their parts and that shared directory exactly as they were.
    [Theory]
    [InlineData(nameof(Step.StagePart), 0, false)]
    [InlineData(nameof(Step.StagePart), 0, true)]
    [InlineData(nameof(Step.StagePart), 5, true)]
    [InlineData(nameof(Step.FlushPart), 0, false)]
    [InlineData(nameof(Step.FlushPart), 3, true)]
    [InlineData(nameof(Step.Backup), 0, true)]
    [InlineData(nameof(Step.Backup), 2, true)]
    [InlineData(nameof(Step.ReplaceTable), 0, false)]
    [InlineData(nameof(Step.ReplaceTable), 0, true)]
    [InlineData(nameof(Step.ReplaceDiagnostic), 0, false)]
    [InlineData(nameof(Step.ReplaceDiagnostic), 0, true)]
    [InlineData(nameof(Step.ReplaceEvidence), 0, false)]
    [InlineData(nameof(Step.ReplaceEvidence), 0, true)]
    public void Every_publication_failure_preserves_the_previous_complete_set(string name, int ordinal, bool existing)
    {
        var step = System.Enum.Parse<Step>(name);
        using var set = new CoveragePublicationFixture();
        var old = set.Stage("old");
        if (existing) set.Publish(old);
        if (existing) set.SetDistinctModes();
        var before = set.Snapshot();
        var directories = set.Directories().ToArray();
        var next = set.Stage("new", partBytes: 4096);

        var error = Assert.Throws<CoverageBuildValidationException>(() => set.Publish(next, new(step, ordinal)));

        Assert.Equal(CoverageBuildValidationRefusal.OutputIo, error.Reason);
        Assert.Equal(step, Assert.IsType<CoveragePublicationFaultException>(error.InnerException).Step);
        Assert.Empty(error.RollbackFailures);
        Assert.Equal(before, set.Snapshot());
        Assert.Equal(directories, set.Directories());
        if (existing) { set.AssertPublished(old); set.AssertDistinctModes(); }

        set.Publish(next);
        set.AssertPublished(next);
        Assert.DoesNotContain(set.Snapshot().Keys, CoveragePublicationFixture.IsRecoveryFile);
        foreach (var part in before.Where(p => p.Key.Contains(".parts/")))
            Assert.Equal(part.Value, CoverageArtifactSet.HashFile(Path.Combine(set.Output, part.Key)));
    }

    [Fact]
    public void Rollback_attempts_every_restore_and_retains_recovery_material_on_failure()
    {
        using var set = new CoveragePublicationFixture();
        var old = set.Stage("old");
        set.Publish(old);
        set.SetDistinctModes();
        var before = set.Snapshot();
        var next = set.Stage("new", partBytes: 4096);

        var error = Assert.Throws<CoverageBuildValidationException>(() => set.Publish(next, new(Step.ReplaceEvidence, 0, Step.ReplaceTable)));

        Assert.Equal(CoverageBuildValidationRefusal.OutputIo, error.Reason);
        Assert.Equal(Step.ReplaceEvidence, Assert.IsType<CoveragePublicationFaultException>(error.InnerException).Step);
        Assert.Equal(Step.ReplaceTable, Assert.IsType<CoveragePublicationFaultException>(Assert.Single(error.RollbackFailures)).Step);
        // The diagnostic root was restored despite the failed table restore. The table keeps its backup.
        var after = set.Snapshot();
        Assert.Equal(before["diagnostics.json"], after["diagnostics.json"]);
        Assert.Equal(before["evidence.json"], after["evidence.json"]);
        Assert.Equal(next.Files[0].Sha256, after["table.bin"]);
        Assert.Contains(after.Keys, k => k.StartsWith("table.bin.", System.StringComparison.Ordinal) && k.EndsWith(".bak", System.StringComparison.Ordinal));
        Assert.True(File.Exists(set.Evidence + CoveragePublicationJournal.JournalSuffix));

        // Recovery never overwrites a destination someone changed after the failure.
        File.WriteAllText(set.Table, "edited");
        var refused = Assert.Throws<CoverageBuildValidationException>(() => CoveragePublisher.Recover(set.Evidence));
        Assert.Equal(CoverageBuildValidationRefusal.OutputRecoveryRequired, refused.Reason);
        Assert.Equal("edited", File.ReadAllText(set.Table));
        Assert.True(File.Exists(set.Evidence + CoveragePublicationJournal.JournalSuffix));

        // With the recorded bytes back in place, recovery restores the exact old set.
        File.Copy(next.Files[0].StagePath, set.Table, true);
        CoveragePublisher.Recover(set.Evidence);
        Assert.Equal(before, set.Snapshot());
        set.AssertPublished(old);
        set.AssertDistinctModes();
    }

    // Each state is built deterministically by a failing rollback or cleanup, standing in for an interrupted
    // process. "generation": complete new parts beside old roots. "table": table replaced before the evidence root.
    // "root": every root replaced before journal cleanup.
    [Theory]
    [InlineData("generation")]
    [InlineData("table")]
    [InlineData("root")]
    public void Interrupted_publication_is_fail_closed_and_recoverable(string state)
    {
        using var set = new CoveragePublicationFixture();
        var old = set.Stage("old");
        set.Publish(old);
        var before = set.Snapshot();
        var next = set.Stage("new", partBytes: 4096);
        CoveragePublicationFault fault = state switch
        {
            "generation" => new(Step.Backup, 0, Step.StagePart),
            "table" => new(Step.ReplaceDiagnostic, 0, Step.ReplaceTable),
            _ => new(Step.Cleanup, 0),
        };

        var error = Assert.Throws<CoverageBuildValidationException>(() => set.Publish(next, fault));
        Assert.Equal(CoverageBuildValidationRefusal.OutputIo, error.Reason);
        if (state == "generation")
            foreach (var part in next.Artifacts.SelectMany(a => a.Parts)) Assert.Equal(part.Sha256, CoverageArtifactSet.HashFile(part.DestinationPath));
        if (state == "table") Assert.Equal(next.Files[0].Sha256, CoverageArtifactSet.HashFile(set.Table));

        // No reader accepts any root of an incomplete transaction, whichever bytes it holds.
        foreach (string root in new[] { set.Evidence, set.Diagnostics })
            Assert.Equal(CoverageBuildValidationRefusal.OutputRecoveryRequired,
                Assert.Throws<CoverageBuildValidationException>(() => CoverageArtifactReader.Open(root, set.Scratch)).Reason);
        Assert.Equal(CoverageBuildValidationRefusal.OutputRecoveryRequired,
            Assert.Throws<CoverageBuildValidationException>(() => CoveragePublicationJournal.EnsureReadable(set.Table)).Reason);

        CoveragePublisher.Recover(set.Evidence);

        Assert.DoesNotContain(set.Snapshot().Keys, CoveragePublicationFixture.IsRecoveryFile);
        if (state == "root") set.AssertPublished(next);
        else
        {
            Assert.Equal(before, set.Snapshot());
            set.AssertPublished(old);
        }
    }

    [Fact]
    public void Immutable_generation_reuse_never_overwrites_existing_bytes()
    {
        using var set = new CoveragePublicationFixture();
        var first = set.Stage("first");
        set.Publish(first);
        // Identical names and bytes are reused after verification.
        var again = set.Stage("first");
        set.Publish(again);
        set.AssertPublished(again);

        // A same-name part with other bytes refuses and is left exactly as found.
        var other = set.Stage("other", evidenceSamples: 61);
        string planted = other.EvidenceSet.Parts[1].DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, "planted");
        var before = set.Snapshot();
        var error = Assert.Throws<CoverageBuildValidationException>(() => set.Publish(other));
        Assert.Equal(CoverageBuildValidationRefusal.OutputIo, error.Reason);
        Assert.Equal(before, set.Snapshot());

        // Publishing a new generation leaves the older complete generation readable by its own root.
        File.Delete(planted);
        byte[] firstRoot = File.ReadAllBytes(set.Evidence);
        set.Publish(other);
        set.AssertPublished(other);
        File.WriteAllBytes(set.Evidence, firstRoot);
        using var read = CoverageArtifactReader.Open(set.Evidence, set.Scratch);
        Assert.Equal(first.EvidenceSet.RootSha256, read.RootSha256);
    }

    // The publisher checks identities itself, so a caller that skipped its own checks still cannot replace a
    // checked input. The protected input is the existing table through another spelling.
    [Fact]
    public void Publication_refuses_an_output_that_is_a_hard_linked_protected_input() => AssertProtectedInputRefused("hard");

    [DirectoryLinkTheory]
    [InlineData("linked")]
    public void Publication_refuses_an_output_that_is_a_protected_input(string spelling) => AssertProtectedInputRefused(spelling);

    static void AssertProtectedInputRefused(string spelling)
    {
        using var set = new CoveragePublicationFixture();
        var old = set.Stage("old");
        set.Publish(old);
        string input = CoveragePathSpelling.Create(set.Output, "table.bin", spelling);
        var before = set.Snapshot();
        var next = set.Stage("new");

        var error = Assert.Throws<CoverageBuildValidationException>(() => set.Publish(next, protectedInputs: [input]));

        Assert.Equal(CoverageBuildValidationRefusal.OutputAlias, error.Reason);
        Assert.Equal(before, set.Snapshot());
        set.Publish(next, protectedInputs: [Path.Combine(set.Folder, "elsewhere.json")]);
        set.AssertPublished(next);
    }
}
