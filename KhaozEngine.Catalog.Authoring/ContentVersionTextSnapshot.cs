using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The COMPLETE text of one committed version: the store epoch, the version number, every revision visible at
/// it, retired rows included, and every language the version recorded with its exact wire spelling and chunk
/// hash, empty languages included. Values and mappings come from the SAME version, never the active one.
/// <para>
/// <b>A snapshot is complete by construction.</b> A store that cannot prove a version's text refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> rather than returning an empty one, so
/// an empty snapshot is a proof that the version holds no text. Version 0 exists only as the empty baseline
/// a publish onto an empty store starts from.
/// </para>
/// </summary>
public sealed class ContentVersionTextSnapshot
{
    /// <summary>Builds one snapshot from owned copies.</summary>
    /// <param name="storeEpoch">The identity of the store the version belongs to.</param>
    /// <param name="versionNumber">The committed version, or 0 for an empty store's baseline.</param>
    /// <param name="revisions">Every revision visible at the version, one per string.</param>
    /// <param name="languages">Every language the version recorded, one per identity and spelling.</param>
    /// <exception cref="ArgumentNullException">An argument or an entry is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="versionNumber"/> is negative.</exception>
    /// <exception cref="ArgumentException">A revision is not visible at the version, repeats a string, or names an unrecorded language, or a language repeats.</exception>
    public ContentVersionTextSnapshot(
        string storeEpoch,
        int versionNumber,
        IReadOnlyList<ContentTextRevision> revisions,
        IReadOnlyList<ContentTextLanguage> languages)
    {
        ArgumentNullException.ThrowIfNull(storeEpoch);
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(languages);

        var recorded = new ContentTextLanguage[languages.Count];
        var declared = new ContentTextLanguageDeclaration[languages.Count];
        for (int i = 0; i < recorded.Length; i++)
        {
            recorded[i] = languages[i] ?? throw new ArgumentNullException(
                nameof(languages), FormattableString.Invariant($"Language {i} is null."));
            declared[i] = recorded[i].Declaration;
        }

        ContentDraftTextState.CopyDeclarations(declared, nameof(languages));
        Revisions = ContentTextCandidate.CopyVisible(revisions, versionNumber, declared, nameof(revisions));
        StoreEpoch = storeEpoch;
        VersionNumber = versionNumber;
        Languages = recorded;
    }

    /// <summary>The identity of the store the version belongs to.</summary>
    public string StoreEpoch { get; }

    /// <summary>The committed version, or 0 for an empty store's baseline.</summary>
    public int VersionNumber { get; }

    /// <summary>Every revision visible at the version.</summary>
    public IReadOnlyList<ContentTextRevision> Revisions { get; }

    /// <summary>Every language the version recorded, empty ones included.</summary>
    public IReadOnlyList<ContentTextLanguage> Languages { get; }

    /// <summary>The empty baseline of a store that has published nothing.</summary>
    /// <param name="storeEpoch">The store's identity.</param>
    public static ContentVersionTextSnapshot EmptyBaseline(string storeEpoch)
        => new(storeEpoch, 0, [], []);
}
