using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// A <see cref="LegacyFreezeStoreView"/> that also keeps the upgrade ledger, which is the shape of a custom
/// provider an upgrade runner can read but which was built before the guarded freeze existed. Both ledger members
/// forward to the inner store and are recorded in the same call sequence as every authoring member.
/// <para>
/// <b>It declares no guarded freeze and no text companion</b>, so a runner over it sees exactly the released seam
/// plus the ledger, and a test can say which members a stopped run reached.
/// </para>
/// </summary>
internal sealed class LegacyFreezeLedgerView : LegacyFreezeStoreView, IContentUpgradeLedger
{
    readonly IContentUpgradeLedger _ledger;

    /// <summary>Wraps a real store that keeps the upgrade ledger.</summary>
    /// <param name="inner">The real store every member is answered from.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> keeps no upgrade ledger.</exception>
    public LegacyFreezeLedgerView(IContentAuthoringStore inner)
        : base(inner)
    {
        _ledger = inner as IContentUpgradeLedger
            ?? throw new ArgumentException("a legacy ledger view's inner store must keep the upgrade ledger.", nameof(inner));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ListUpgradesAsync));
        return _ledger.ListUpgradesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(RecordUpgradeAsync));
        return _ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
    }
}
