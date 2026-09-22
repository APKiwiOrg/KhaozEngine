using System;
using System.Data.Common;
using System.IO;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The two questions a run asks about an exception: is it one this package answers with a REPORT, and is it
/// worth trying again.
/// <para>
/// <b>A provider fault is not automatically contention.</b> Every provider raises one
/// <see cref="DbException"/> for a deadlock, a busy database, a wrong password and a dropped route, and
/// retrying the last two spends a whole attempt budget of replans to produce a message about another
/// publisher holding the draft, which sends an operator to the wrong place. The provider already answers
/// this: <see cref="DbException.IsTransient"/> is the standard property for it, so this package needs no
/// reference to any provider to ask.
/// </para>
/// <para>
/// <b>A cancellation is never handled here.</b> It is not a catalog fault and a host that cancelled its own
/// boot is not waiting for a report about it.
/// </para>
/// </summary>
static class ContentUpgradeFault
{
    /// <summary>
    /// Whether the run turns this exception into a report rather than letting it escape. It is every fault a
    /// catalog, its provider or its pack store raises, and nothing else, because anything else is a defect
    /// worth a stack trace.
    /// </summary>
    /// <param name="failure">The exception.</param>
    internal static bool IsHandled(Exception failure) => failure is ContentAuthoringException
        or ContentPackException
        or DbException
        or IOException
        or UnauthorizedAccessException;

    /// <summary>
    /// Whether a fault is CONTENTION with another runner rather than a defect in the plan or the catalog.
    /// The four refusal reasons are exactly what a second publisher produces: it took the draft, it took the
    /// draft away, it moved the base version, or it recorded the upgrade first. A provider fault counts only
    /// when the provider itself calls it transient.
    /// </summary>
    /// <param name="failure">The exception.</param>
    internal static bool IsContention(Exception failure) => failure switch
    {
        ContentAuthoringException refused => refused.Reason is ContentAuthoringException.PublishInProgressReason
            or ContentAuthoringException.NoOpenDraftReason
            or ContentAuthoringException.BaseVersionMovedReason
            or ContentAuthoringException.UpgradeAlreadyRecordedReason,
        DbException provider => provider.IsTransient,
        _ => false,
    };

    /// <summary>
    /// The line an operator reads for a fault the run could not resolve: which upgrade, what the run was
    /// doing, what the catalog said, and what to do next.
    /// </summary>
    /// <param name="upgradeId">The upgrade being worked on, empty when the fault came from a gate.</param>
    /// <param name="operation">What the run was doing, as a verb phrase.</param>
    /// <param name="versionNumber">The version the run was standing on.</param>
    /// <param name="failure">The fault.</param>
    internal static string Message(
        string upgradeId,
        string operation,
        int versionNumber,
        Exception failure)
    {
        string subject = upgradeId.Length == 0
            ? "this upgrade run"
            : FormattableString.Invariant($"upgrade '{upgradeId}'");
        return FormattableString.Invariant(
            $"{subject} could not {operation} on catalog version {versionNumber}: {failure.Message} Nothing further was changed. Clear the fault the catalog reported, then run the pending upgrade(s) again.");
    }
}
