using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The trusted pin write and its guarded optimistic confirmation share one transaction.</summary>
public sealed partial class InMemoryContentAuthoringStore : IContentVersionSelectionStore
{
    /// <inheritdoc />
    public Task<bool> TrySetPinnedVersionAsync(
        int? version,
        ContentVersionSelection expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => SetPinAsync(version, actor, operatorId, expected, cancellationToken);

    /// <summary>The common trusted and conditional pin transaction.</summary>
    Task<bool> SetPinAsync(
        int? version,
        string actor,
        string operatorId,
        ContentVersionSelection? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        lock (_gate)
        {
            if (expected is ContentVersionSelection selection
                && (!string.Equals(selection.StoreEpoch, _storeEpoch, StringComparison.Ordinal)
                    || selection.ActiveVersion != _activeVersion || selection.PinnedVersion != _pinnedVersion))
            {
                return Task.FromResult(false);
            }

            if (version is int held && FindVersion(held) is null)
            {
                throw new ContentAuthoringException(
                    ContentAuthoringRefusal.VersionUnknown,
                    FormattableString.Invariant(
                        $"Version {held} does not exist, so it cannot be pinned."),
                    default,
                    0,
                    ContentAuthoringException.UnknownTypeReason)
                { VersionNumber = held };
            }

            // STAGE, then move the pin, then COMMIT. Everything that can fail is in the staging, so a pin
            // with no audit row against it is not a state this store can be left in. A provider gets the
            // same property from its one transaction.
            string? before = InMemoryContentAuditLog.Render(_pinnedVersion);
            var staged = new List<ContentAuditEntry>(1);
            _audit.Stage(
                staged,
                ContentAuditActions.Pin,
                actor,
                operatorId,
                default,
                0,
                default,
                string.Empty,
                before,
                InMemoryContentAuditLog.Render(version),
                0,
                string.Empty);

            _pinnedVersion = version;
            _audit.Commit(staged);
            return Task.FromResult(true);
        }
    }
}
