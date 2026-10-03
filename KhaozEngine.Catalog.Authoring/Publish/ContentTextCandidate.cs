using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The merged text of the version a publish will commit, as data: every value visible at the new version,
/// every declared language in manifest order, and the temporal closes and inserts the commit applies. The
/// candidate builder produces it from the frozen snapshot and the approved row plan, and nothing here
/// derives or encodes anything.
/// <para>
/// A close is the baseline revision as it stands, still live, which the commit marks replaced at the new
/// version. An insert is a new revision valid from the new version. A language with no values is declared
/// all the same, so its empty chunk still publishes.
/// </para>
/// </summary>
public sealed class ContentTextCandidate
{
    /// <summary>Builds one candidate from owned copies.</summary>
    /// <param name="values">Every revision visible at the new version, one per string.</param>
    /// <param name="declarations">Every language the new version declares, in manifest order.</param>
    /// <param name="closes">The baseline revisions the new version replaces, each still live.</param>
    /// <param name="inserts">The revisions the new version adds, each live.</param>
    /// <exception cref="ArgumentNullException">A list or an entry is null.</exception>
    /// <exception cref="ArgumentException">A string repeats in one list, a revision names an undeclared language, a close or insert is already replaced, or a declaration repeats.</exception>
    public ContentTextCandidate(
        IReadOnlyList<ContentTextRevision> values,
        IReadOnlyList<ContentTextLanguageDeclaration> declarations,
        IReadOnlyList<ContentTextRevision> closes,
        IReadOnlyList<ContentTextRevision> inserts)
    {
        Declarations = ContentDraftTextState.CopyDeclarations(declarations, nameof(declarations));
        Values = CopyVisible(values, null, Declarations, nameof(values));
        Closes = CopyVisible(closes, null, null, nameof(closes));
        Inserts = CopyVisible(inserts, null, Declarations, nameof(inserts));
    }

    /// <summary>Every revision visible at the new version.</summary>
    public IReadOnlyList<ContentTextRevision> Values { get; }

    /// <summary>Every language the new version declares, in manifest order.</summary>
    public IReadOnlyList<ContentTextLanguageDeclaration> Declarations { get; }

    /// <summary>The baseline revisions the new version replaces.</summary>
    public IReadOnlyList<ContentTextRevision> Closes { get; }

    /// <summary>The revisions the new version adds.</summary>
    public IReadOnlyList<ContentTextRevision> Inserts { get; }

    /// <summary>
    /// An owned copy of revisions, each visible at <paramref name="versionNumber"/> or still open when it is
    /// null, one per string, and each in a declared language when <paramref name="declared"/> is given.
    /// </summary>
    internal static ContentTextRevision[] CopyVisible(
        IReadOnlyList<ContentTextRevision> revisions,
        int? versionNumber,
        IReadOnlyList<ContentTextLanguageDeclaration>? declared,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(revisions, parameterName);
        HashSet<string>? languages = null;
        if (declared is not null)
        {
            languages = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < declared.Count; i++)
            {
                languages.Add(declared[i].Language);
            }
        }

        var copy = new ContentTextRevision[revisions.Count];
        var strings = new HashSet<(ushort Type, int Id, string Field, string Language)>();
        for (int i = 0; i < copy.Length; i++)
        {
            ContentTextRevision revision = revisions[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Revision {i} is null."));
            if (versionNumber is int at ? !revision.IsLiveAt(at) : revision.ReplacedInVersion is not null)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Revision {i} of type {revision.Type.Value} row {revision.DefinitionId} is not live where this list needs it."),
                    parameterName);
            }

            if (!strings.Add((revision.Type.Value, revision.DefinitionId, revision.FieldName, revision.Language)))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Revision {i} names type {revision.Type.Value} row {revision.DefinitionId} field '{revision.FieldName}' language '{revision.Language}' a second time."),
                    parameterName);
            }

            if (languages is not null && !languages.Contains(revision.Language))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Revision {i} is in language '{revision.Language}', which is not declared."),
                    parameterName);
            }

            copy[i] = revision;
        }

        return copy;
    }
}
