using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The lines a preview and an applied step show for a plan carrying text: the planner's own lines, then one
/// line per text intent and one per language the plan introduces against its baseline, so an operator reviews
/// the translations and the new declarations an apply would publish, not only the rows.
/// <para>
/// The lines are developer output, never player-facing text. A long value is abbreviated on a whole character
/// with a visible marker, because the full value is in the plan and in the published version's history.
/// </para>
/// </summary>
internal static class ContentUpgradeTextLines
{
    /// <summary>The longest value a line quotes before it is abbreviated.</summary>
    const int MaxQuoted = 64;

    /// <summary>The planner's lines, followed by the text work of a plan carrying text.</summary>
    /// <param name="plan">A bound change set plan.</param>
    public static IReadOnlyList<string> For(ContentUpgradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CarriesText)
        {
            return plan.ChangeLines;
        }

        var lines = new List<string>(plan.ChangeLines);
        foreach (ContentTextEdit edit in plan.TextEdits)
        {
            ContentTextTarget target = edit.Target;
            lines.Add(edit.Operation == ContentTextEditOperation.Set
                ? FormattableString.Invariant(
                    $"text set type {target.Type.Value} '{target.Key}' {target.FieldName} [{target.Language}]: \"{Quote(edit.Value!)}\"")
                : FormattableString.Invariant(
                    $"text remove type {target.Type.Value} '{target.Key}' {target.FieldName} [{target.Language}]"));
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguageDeclaration language in plan.BaselineText?.Languages ?? [])
        {
            declared.Add(language.Language);
        }

        var introduced = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextEdit edit in plan.TextEdits)
        {
            string language = edit.Target.Language;
            if (edit.Operation == ContentTextEditOperation.Set && !declared.Contains(language) && introduced.Add(language))
            {
                lines.Add(FormattableString.Invariant($"language {language} introduced"));
            }
        }

        return lines;
    }

    static string Quote(string value)
    {
        if (value.Length <= MaxQuoted)
        {
            return value;
        }

        int keep = char.IsHighSurrogate(value[MaxQuoted - 1]) ? MaxQuoted - 1 : MaxQuoted;
        return string.Concat(value.AsSpan(0, keep), ContentTextAuditRendering.CutMarker);
    }
}
