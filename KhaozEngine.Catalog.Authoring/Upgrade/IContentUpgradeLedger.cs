using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The durable history of which content upgrades a catalog holds, as a SEPARATE seam from
/// <see cref="IContentAuthoringStore"/>, whose member list is fixed.
/// <para>
/// <b>History lives in the database beside the content it describes</b>, in the
/// <c>catalog_content_upgrade</c> table schema version 2 adds. A convention over version notes cannot tell
/// "applied, then tuned back" from "never applied", which is the defaults-reapplied failure this seam exists
/// to prevent.
/// </para>
/// <para>
/// <b>An <c>applied</c> row is never written through this interface.</b> It is written inside the publish
/// commit transaction, from <see cref="ContentPublishRequest.Upgrade"/>, so the version and its history entry
/// commit together or not at all. The primary key is what makes a second publish of one upgrade a refusal
/// with nothing changed.
/// </para>
/// </summary>
public interface IContentUpgradeLedger
{
    /// <summary>
    /// Every ledger row, ascending by order and then by id, which is the order the upgrades ran in.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an upgrade the runner did not publish: <see cref="ContentUpgradeDisposition.Adopted"/> when the
    /// content was already there, or <see cref="ContentUpgradeDisposition.Baseline"/> when the catalog was
    /// seeded from a bundle that carried it. The ledger row and one audit row land in ONE transaction, and an
    /// id the ledger already holds is a no-op rather than an error, so a crash between a seed and its baseline
    /// record is recoverable by running it again.
    /// </summary>
    /// <param name="stamp">The upgrade's id and order.</param>
    /// <param name="disposition">Adopted or baseline.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded, empty when it forwarded none.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="disposition"/> is <see cref="ContentUpgradeDisposition.Applied"/>, which only a publish commit writes.</exception>
    Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default);
}
