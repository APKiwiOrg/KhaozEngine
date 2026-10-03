using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The first phase of a publish's preparation: steps 2 to 7 have run once, so every new row has its id, the
/// candidate is validated and the row and rule chunks are encoded. Only step 8 is left, and it is left
/// OPEN because the manifests name the output language list, which a text publish only knows after its text
/// chunks are built against these very ids.
/// <para>
/// <b>Ids are allocated exactly once.</b> A text publish runs its candidate and chunk builders on
/// <see cref="LiveRows"/> and then calls <see cref="Complete"/> with the complete language list, so the
/// manifests are encoded once and the row plan's languages equal the text chunks by construction. A row-only
/// publish completes with the baseline's languages.
/// </para>
/// <para>
/// A phase refused before step 8 carries its refusal plan and completes to it unchanged.
/// </para>
/// </summary>
internal sealed class ContentPublishRowPhase
{
    readonly ContentTypeRegistry? _registry;
    readonly Action<ContentPublishStep>? _onStep;
    readonly ContentPublishPlan? _refusal;
    readonly Parts? _parts;

    ContentPublishRowPhase(ContentPublishPlan refusal)
    {
        _refusal = refusal;
        VersionNumber = refusal.VersionNumber;
        BaseVersion = refusal.BaseVersion;
        LiveRows = refusal.LiveRows;
        FrozenEdits = refusal.FrozenEdits;
    }

    ContentPublishRowPhase(ContentTypeRegistry registry, Action<ContentPublishStep>? onStep, Parts parts)
    {
        _registry = registry;
        _onStep = onStep;
        _parts = parts;
        VersionNumber = parts.VersionNumber;
        BaseVersion = parts.BaseVersion;
        LiveRows = parts.Live;
        FrozenEdits = parts.Frozen;
    }

    /// <summary>The version being prepared.</summary>
    public int VersionNumber { get; }

    /// <summary>The base version the phase stands on.</summary>
    public int BaseVersion { get; }

    /// <summary>Every row live at the new version, ids final, which the text builders bind targets through.</summary>
    public IReadOnlyList<ContentRowRevision> LiveRows { get; }

    /// <summary>The row edits step 1 froze.</summary>
    public IReadOnlyList<ContentEdit> FrozenEdits { get; }

    /// <summary>True when the candidate validated and step 8 may run.</summary>
    public bool IsValid => _refusal is null;

    /// <summary>The refusal plan, carrying every finding, or null on a valid phase.</summary>
    public ContentPublishPlan? Refusal => _refusal;

    /// <summary>A phase refused before step 8.</summary>
    /// <param name="refusal">The refusal plan.</param>
    public static ContentPublishRowPhase Refused(ContentPublishPlan refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new ContentPublishRowPhase(refusal);
    }

    /// <summary>A phase that validated, waiting for its language list.</summary>
    /// <param name="registry">The registry the manifests name types through.</param>
    /// <param name="onStep">The crash hook, or null.</param>
    /// <param name="parts">Everything steps 2 to 7 computed.</param>
    public static ContentPublishRowPhase Ready(
        ContentTypeRegistry registry, Action<ContentPublishStep>? onStep, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(parts);
        return new ContentPublishRowPhase(registry, onStep, parts);
    }

    /// <summary>
    /// Step 8, once: both manifests, each under its own hash sub-domain, naming <paramref name="languages"/>.
    /// A refused phase returns its refusal plan.
    /// </summary>
    /// <param name="languages">The complete output language list, in ordinal wire-tag order.</param>
    public ContentPublishPlan Complete(IReadOnlyList<ManifestLanguageEntry> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        if (_refusal is not null)
        {
            return _refusal;
        }

        Parts parts = _parts!;
        _onStep?.Invoke(ContentPublishStep.BeforeManifestWrite);
        ContentManifest server = ContentManifestBuilder.Build(
            ContentManifestSide.Server, _registry!, parts.Chunks, languages, parts.VersionNumber,
            parts.MinimumServerBuild, parts.MinimumClientBuild, parts.RuleHash);
        ContentManifest client = ContentManifestBuilder.Build(
            ContentManifestSide.Client, _registry!, parts.Chunks, languages, parts.VersionNumber,
            parts.MinimumServerBuild, parts.MinimumClientBuild, parts.RuleHash);
        string serverHash = ContentManifestText.Hash(server);
        string clientHash = ContentManifestText.Hash(client);
        _onStep?.Invoke(ContentPublishStep.AfterManifestWrite);

        return new ContentPublishPlan(
            parts.VersionNumber,
            parts.BaseVersion,
            parts.Candidate,
            parts.Validation,
            parts.Allocation,
            parts.Closes,
            parts.Inserts,
            parts.Live,
            parts.Appended,
            parts.Rules,
            parts.Chunks,
            languages,
            parts.RuleHash,
            server,
            client,
            serverHash,
            clientHash,
            parts.MinimumServerBuild,
            parts.MinimumClientBuild,
            parts.Frozen);
    }

    /// <summary>What steps 2 to 7 computed for a candidate that validated.</summary>
    /// <param name="VersionNumber">The version being prepared.</param>
    /// <param name="BaseVersion">The base version.</param>
    /// <param name="Candidate">The validated candidate.</param>
    /// <param name="Validation">The sweep's report, informational findings included.</param>
    /// <param name="Allocation">What step 3 issued.</param>
    /// <param name="Closes">The row revisions this version closes.</param>
    /// <param name="Inserts">The row revisions this version inserts.</param>
    /// <param name="Live">Every row live at this version.</param>
    /// <param name="Appended">The rules this version appends.</param>
    /// <param name="Rules">The full rule list.</param>
    /// <param name="Chunks">Every chunk row, written and reused.</param>
    /// <param name="RuleHash">The rule chunk's address.</param>
    /// <param name="MinimumServerBuild">The minimum server build.</param>
    /// <param name="MinimumClientBuild">The minimum client build.</param>
    /// <param name="Frozen">The frozen row edits.</param>
    internal sealed record Parts(
        int VersionNumber,
        int BaseVersion,
        ContentSnapshot Candidate,
        ContentValidationReport Validation,
        ContentIdAllocationRecord Allocation,
        IReadOnlyList<ContentRowClose> Closes,
        IReadOnlyList<ContentRowInsert> Inserts,
        IReadOnlyList<ContentRowRevision> Live,
        IReadOnlyList<RemapRule> Appended,
        IReadOnlyList<RemapRule> Rules,
        IReadOnlyList<ContentChunkRecord> Chunks,
        string RuleHash,
        int MinimumServerBuild,
        int MinimumClientBuild,
        IReadOnlyList<ContentEdit> Frozen);
}
