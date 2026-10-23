using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReporterIdentityRedactionTests
{
    [Fact]
    public void Names_and_subjects_are_literal_case_insensitive_matches()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["Aster (Dawn)+", "discord:12345"]);

        Assert.Equal("Joined [redacted] as [redacted].", redactor.Redact("Joined ASTER (dawn)+ as discord:12345."));
        const string different = "Joined Aster Dawn as discord:1234.";
        Assert.Same(different, redactor.Redact(different));
    }

    [Fact]
    public void Longest_term_wins_when_terms_overlap()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["Aster", "Aster Dawn", "ASTER DAWN"]);

        Assert.Equal("[redacted] met [redacted].", redactor.Redact("Aster Dawn met Aster."));
    }

    [Fact]
    public void Short_empty_null_and_blank_terms_are_ignored()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["Al", "x", "", null!, "   ", "Eve"]);

        Assert.Equal("Al x   [redacted]", redactor.Redact("Al x   Eve"));
    }

    [Fact]
    public void No_match_preserves_the_input_instance()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["Aster Dawn", "discord:12345"]);
        string text = new("Loaded 12 tiles in 3.2 ms".ToCharArray());

        Assert.Same(text, redactor.Redact(text));
    }

    [Fact]
    public void Snapshot_reads_the_callback_once_and_leaves_the_original_unchanged()
    {
        var original = new ReportRedactor("");
        string[] terms = ["Aster Dawn"];
        int calls = 0;
        ReportRedactor snapshot = With(original, () => { calls++; return terms; });
        terms[0] = "Rowan Vale";

        Assert.Equal(1, calls);
        Assert.Equal("[redacted] and Rowan Vale", snapshot.Redact("Aster Dawn and Rowan Vale"));
        Assert.Equal("Aster Dawn", original.Redact("Aster Dawn"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Identity_runs_after_secrets_and_before_home()
    {
        ReportRedactor redactor = With(new ReportRedactor("/Users/Aster"), () => ["hunter22", "Aster"]);

        Assert.Equal("password=[redacted] /Users/[redacted]/log", redactor.Redact("password=hunter22 /Users/Aster/log"));
    }

    [Fact]
    public void Environment_values_use_the_identity_rules()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["Aster Dawn"]);

        TelemetryHeaderValue value = Assert.Single(redactor.SelectEnvironment(
            [new KeyValuePair<string, string?>("KE_LABEL", "Aster Dawn")]));

        Assert.Equal("[redacted]", value.Value);
    }

    [Fact]
    public void Regex_syntax_in_a_term_is_literal_even_on_adversarial_input()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => ["(a+)+$"]);
        string prefix = new('a', 20_000);

        Assert.Equal(prefix + "! [redacted]", redactor.Redact(prefix + "! (a+)+$"));
    }

    [Fact]
    public void Oversized_literal_cannot_fail_open_when_the_pattern_is_refused()
    {
        string term = new('z', 2_000);
        ReportRedactor redactor = With(new ReportRedactor(""), () => [term]);

        string result = redactor.Redact("player " + term);

        Assert.DoesNotContain(term, result, StringComparison.Ordinal);
        Assert.Contains(ReportRedactor.Mask, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Callback_failure_masks_every_string()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), () => throw new InvalidOperationException("Aster Dawn"));

        Assert.Equal(ReportRedactor.Mask, redactor.Redact("player Aster Dawn"));
        Assert.Equal(ReportRedactor.Mask, redactor.Redact("diagnostics"));
    }

    [Fact]
    public void Enumeration_failure_masks_every_string()
    {
        ReportRedactor redactor = With(new ReportRedactor(""), BrokenTerms);

        Assert.Equal(ReportRedactor.Mask, redactor.Redact("player Rowan Vale"));
    }

    static IEnumerable<string> BrokenTerms()
    {
        yield return "Aster Dawn";
        throw new InvalidOperationException("Rowan Vale");
    }

    internal static ReportRedactor With(ReportRedactor redactor, Func<IEnumerable<string>> source) =>
        redactor.WithReporterIdentityTerms(source);
}

[Collection("AllocSensitive")]
public sealed class ReporterIdentityAllocationTests
{
    [Fact]
    public void Clean_text_does_not_allocate_after_a_full_collection()
    {
        ReportRedactor redactor = ReporterIdentityRedactionTests.With(
            new ReportRedactor(""), () => ["Aster Dawn", "discord:12345"]);
        const string text = "Loaded 12 tiles in 3.2 ms";
        void Run() => redactor.Redact(text);
        Run();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        AllocAssert.NoPerCallAllocation("Reporter identity redaction on clean text", Run);
    }
}
