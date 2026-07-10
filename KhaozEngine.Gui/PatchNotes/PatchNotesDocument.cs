using System;
using System.Collections.Generic;

namespace KhaozEngine.Gui;

/// <summary>Category a <see cref="PatchNote"/> is grouped under, per <c>docs/CHANGELOG-STYLE.md</c>.</summary>
public enum PatchNoteCategory
{
    /// <summary>Brand-new content, features, modes, or systems.</summary>
    New,

    /// <summary>A large or headline change to how the game plays.</summary>
    Major,

    /// <summary>Small tweaks, quality-of-life, polish.</summary>
    Minor,

    /// <summary>Tuning of numbers, difficulty, economy, drop rates (label alias: "Balance").</summary>
    Rebalance,

    /// <summary>Fixes to things that were broken or behaving wrong.</summary>
    Bug,

    /// <summary>Anything that does not map to a known category label.</summary>
    Other,
}

/// <summary>
/// One run of a patch note's text: either a plain span or a <c>`backtick`</c>-wrapped span (an
/// upgrade / entity / item name, per the changelog style). A note is rendered by concatenating its
/// spans and drawing code spans distinctly (e.g. a highlighted font).
/// </summary>
/// <param name="Text">The span's text, with the surrounding backticks (if any) removed.</param>
/// <param name="IsCode">Whether this span was wrapped in backticks in the source.</param>
public readonly record struct PatchNoteSpan(string Text, bool IsCode);

/// <summary>A single changelog bullet, decomposed into plain and backtick-wrapped spans.</summary>
/// <param name="Spans">The note's text, in source order.</param>
public sealed record PatchNote(IReadOnlyList<PatchNoteSpan> Spans);

/// <summary>A category-labelled group of notes within a build (e.g. all the <c>**Bug**</c> bullets).</summary>
/// <param name="Category">The group's category.</param>
/// <param name="Notes">The notes under this category, in source order.</param>
public sealed record PatchNoteGroup(PatchNoteCategory Category, IReadOnlyList<PatchNote> Notes);

/// <summary>One <c>### Build X.Y.Z (Name)</c> entry and its category groups.</summary>
/// <param name="Version">The build's version, e.g. <c>"0.6.5"</c>.</param>
/// <param name="BuildName">The parenthesised build name, e.g. <c>"Alpha 006"</c>.</param>
/// <param name="Date">The <c>## YYYY-MM-DD</c> date heading the build falls under, or empty if none preceded it.</param>
/// <param name="Groups">The build's category groups, in source order.</param>
public sealed record PatchNotesBuild(string Version, string BuildName, string Date,
    IReadOnlyList<PatchNoteGroup> Groups);

/// <summary>
/// A parsed player-facing changelog (<c>docs/PLAY_CHANGELOG.md</c>, per <c>docs/CHANGELOG-STYLE.md</c>):
/// a title and its builds, newest first as they appear in the source. Produced by
/// <see cref="PatchNotesParser.Parse"/>.
/// </summary>
/// <param name="Title">The <c># &lt;Game&gt; - Player Changelog</c> heading text, or empty if none was found.</param>
/// <param name="Builds">The parsed builds, in source order.</param>
public sealed record PatchNotesDocument(string Title, IReadOnlyList<PatchNotesBuild> Builds)
{
    /// <summary>The empty document: no title, no builds. Returned for null/blank/unparsable input.</summary>
    public static readonly PatchNotesDocument Empty = new(string.Empty, Array.Empty<PatchNotesBuild>());

    /// <summary>True when this document has no builds (e.g. <see cref="Empty"/>).</summary>
    public bool IsEmpty => Builds.Count == 0;
}
