using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The two things an order can do that look wrong and are not. Both are ALLOWED and both are reported,
/// because an operator reading a version history deserves to know the ledger and the shipped set disagree
/// about a number even when nothing is broken by it.
/// <para>
/// <b>The identity is the id, never the order.</b> An order decides which of two PENDING upgrades runs
/// first and nothing else. So a definition whose order moved between builds has still run, and a definition
/// ordered below one the catalog already holds has still not run. Refusing either would refuse a catalog
/// that is correct, and the second one is what two feature branches merging produces every time.
/// </para>
/// </summary>
static class ContentUpgradeOrderNotes
{
    /// <summary>
    /// Every informational note the orders warrant, and an empty list when the shipped orders and the
    /// recorded ones agree.
    /// </summary>
    /// <param name="set">The definitions this build ships.</param>
    /// <param name="recorded">Every ledger row.</param>
    /// <param name="pending">The shipped definitions the ledger does not hold, ascending by order.</param>
    internal static IReadOnlyList<ContentUpgradeDiagnostic> For(
        ContentUpgradeSet set,
        IReadOnlyList<ContentUpgradeRecord> recorded,
        IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        var notes = new List<ContentUpgradeDiagnostic>();
        int highestOrder = 0;
        string highestId = string.Empty;

        for (int i = 0; i < recorded.Count; i++)
        {
            ContentUpgradeRecord record = recorded[i];
            if (record.Order > highestOrder)
            {
                highestOrder = record.Order;
                highestId = record.Id;
            }

            if (set.TryGet(record.Id, out ContentUpgradeDefinition? shipped)
                && shipped.Order != record.Order)
            {
                notes.Add(new ContentUpgradeDiagnostic(
                    ContentUpgradeCodes.UpgradeOrderMoved,
                    FormattableString.Invariant(
                        $"upgrade '{record.Id}' ran at order {record.Order} and this build ships it at order {shipped.Order}. The id is what the ledger holds, so the catalog already carries it and it will not run again. Nothing needs doing.")));
            }
        }

        var below = new List<string>();
        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i].Order < highestOrder)
            {
                below.Add(pending[i].Id);
            }
        }

        if (below.Count > 0)
        {
            notes.Add(new ContentUpgradeDiagnostic(
                ContentUpgradeCodes.PendingBelowApplied,
                FormattableString.Invariant(
                    $"pending upgrade(s) '{string.Join("', '", below)}' are ordered below '{highestId}' at order {highestOrder}, which this catalog already holds. Two feature branches merging produces this and it is allowed: they run now, in their own order, against the catalog as it stands.")));
        }

        return notes;
    }

    /// <summary>
    /// The note for a definition the run publishes FIRST because an interrupted run left its draft standing.
    /// It is the same informational shape as a pending definition ordered below an applied one, and for the
    /// same reason: the id is the identity, so an order jumped is a thing to report rather than to refuse.
    /// </summary>
    /// <param name="recovered">The definition whose draft is standing.</param>
    /// <param name="below">Every pending id ordered below it, which now runs after it.</param>
    internal static ContentUpgradeDiagnostic RecoveredFirst(
        ContentUpgradeDefinition recovered,
        IReadOnlyList<string> below)
        => new(
            ContentUpgradeCodes.PendingBelowApplied,
            FormattableString.Invariant(
                $"upgrade '{recovered.Id}' at order {recovered.Order} runs FIRST, because an interrupted run left its draft standing and only it can publish that draft. Pending upgrade(s) '{string.Join("', '", below)}' are ordered below it and run after it, against the catalog as it stands."));
}
