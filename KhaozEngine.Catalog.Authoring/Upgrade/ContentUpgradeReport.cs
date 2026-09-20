using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What a run did, as the operator's lines and one exit code.
/// <para>
/// <b>It never exits the process and it never writes on its own.</b> The engine has no business deciding when
/// a host dies, so the runner builds the lines and the code and the host writes them and exits, which is the
/// same shape <c>ContentBootResult</c> established and the same reason: the whole table is then testable in
/// one assembly, in process.
/// </para>
/// <para>
/// <b>Every line carries <see cref="ContentBoot.LinePrefix"/></b>, so an operator greps one token across a
/// boot and an upgrade rather than two.
/// </para>
/// </summary>
public sealed class ContentUpgradeReport
{
    /// <summary>Builds the report. The runner is its only caller, which is why the lists are taken as given.</summary>
    /// <param name="outcome">How the run ended.</param>
    /// <param name="activeVersionBefore">The active version the run found, and 0 on an empty catalog.</param>
    /// <param name="activeVersionAfter">The active version the run left, which equals the before on every refusal.</param>
    /// <param name="steps">One entry per shipped definition that the run reached a conclusion about, in order.</param>
    /// <param name="diagnostics">Everything the run has to say, under its stable codes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A version number is negative.</exception>
    public ContentUpgradeReport(
        ContentUpgradeOutcome outcome,
        int activeVersionBefore,
        int activeVersionAfter,
        IReadOnlyList<ContentUpgradeStepResult> steps,
        IReadOnlyList<ContentUpgradeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentOutOfRangeException.ThrowIfNegative(activeVersionBefore);
        ArgumentOutOfRangeException.ThrowIfNegative(activeVersionAfter);

        Outcome = outcome;
        ActiveVersionBefore = activeVersionBefore;
        ActiveVersionAfter = activeVersionAfter;
        Steps = steps;
        Diagnostics = diagnostics;
        Lines = Render(this);
    }

    /// <summary>How the run ended.</summary>
    public ContentUpgradeOutcome Outcome { get; }

    /// <summary>True for up to date, applied, preview only and no catalog, and false for every refusal.</summary>
    public bool Success => ContentUpgradeOutcomes.IsSuccess(Outcome);

    /// <summary>
    /// True when the catalog holds no active version at all. It is exposed separately from
    /// <see cref="Success"/> because it IS a success and a caller still has to act on it: a host seeds its
    /// bundle and then calls <see cref="ContentUpgradeRunner.RecordBaselineAsync"/>, and a caller that does
    /// not seed treats it as nothing to do.
    /// </summary>
    public bool CatalogAbsent => Outcome == ContentUpgradeOutcome.NoCatalog;

    /// <summary>The active version the run found, and 0 when the catalog holds none.</summary>
    public int ActiveVersionBefore { get; }

    /// <summary>
    /// The active version the run left. It equals <see cref="ActiveVersionBefore"/> on every refusal and on
    /// every run that published nothing, which is the assertion "nothing changed" is made with.
    /// </summary>
    public int ActiveVersionAfter { get; }

    /// <summary>One entry per definition the run reached a conclusion about, ascending by order.</summary>
    public IReadOnlyList<ContentUpgradeStepResult> Steps { get; }

    /// <summary>Everything the run has to say, under the stable codes of <see cref="ContentUpgradeCodes"/>.</summary>
    public IReadOnlyList<ContentUpgradeDiagnostic> Diagnostics { get; }

    /// <summary>Every line the report writes, in order, each already carrying the content line prefix.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>0 on success and the content refusal code otherwise, which is the host's own exit code.</summary>
    public int ExitCode => Success ? 0 : ContentBootResult.ContentFailureExitCode;

    /// <summary>
    /// Writes every line, to <paramref name="output"/> on a success and to <paramref name="error"/> on a
    /// refusal. The split is whole rather than per line, so a host that redirects one stream gets the whole
    /// story on one of them rather than half of it on each.
    /// </summary>
    /// <param name="output">Where a successful run's lines go, ordinarily the console's output.</param>
    /// <param name="error">Where a refusal's lines go, ordinarily the console's error.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void WriteTo(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        TextWriter writer = Success ? output : error;
        for (int i = 0; i < Lines.Count; i++)
        {
            writer.WriteLine(Lines[i]);
        }
    }

    /// <summary>
    /// The lines, once, at construction: the outcome and the two version numbers, then one line per step with
    /// its change lines under it, then one line per diagnostic. Every line names the upgrade id it is about,
    /// because a line that says an upgrade failed without saying which one sends an operator to the wrong
    /// place.
    /// </summary>
    static string[] Render(ContentUpgradeReport report)
    {
        var lines = new List<string>(report.Steps.Count + report.Diagnostics.Count + 1);
        lines.Add(Line(FormattableString.Invariant(
            $"upgrade {ContentUpgradeOutcomes.Token(report.Outcome)}: catalog version {report.ActiveVersionBefore} to {report.ActiveVersionAfter}.")));

        for (int i = 0; i < report.Steps.Count; i++)
        {
            ContentUpgradeStepResult step = report.Steps[i];
            lines.Add(Line(StepLine(step)));
            for (int j = 0; j < step.ChangeLines.Count; j++)
            {
                lines.Add(Line(FormattableString.Invariant($"upgrade '{step.Id}': {step.ChangeLines[j]}")));
            }
        }

        for (int i = 0; i < report.Diagnostics.Count; i++)
        {
            lines.Add(Line(report.Diagnostics[i].ToString()));
        }

        return [.. lines];
    }

    static string StepLine(ContentUpgradeStepResult step) => step.State switch
    {
        ContentUpgradeStepState.Applied => FormattableString.Invariant(
            $"upgrade '{step.Id}' applied as version {step.PublishedVersion}. {step.Description}"),
        ContentUpgradeStepState.Adopted => FormattableString.Invariant(
            $"upgrade '{step.Id}' was already satisfied and is recorded as adopted. {step.Reason}"),
        ContentUpgradeStepState.Planned when step.ChangeLines.Count == 0 => FormattableString.Invariant(
            $"upgrade '{step.Id}' plans no change. {step.Reason}"),
        ContentUpgradeStepState.Planned => FormattableString.Invariant(
            $"upgrade '{step.Id}' plans {step.ChangeLines.Count} change(s). {step.Description}"),
        ContentUpgradeStepState.Refused => FormattableString.Invariant(
            $"upgrade '{step.Id}' refused. {step.Reason}"),
        _ => FormattableString.Invariant($"upgrade '{step.Id}' is pending. {step.Description}"),
    };

    static string Line(string text) => ContentBoot.LinePrefix + text;
}
