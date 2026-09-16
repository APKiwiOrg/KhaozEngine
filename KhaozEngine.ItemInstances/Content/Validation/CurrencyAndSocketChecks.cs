using System.Collections.Generic;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The currency and socket families' half of spec 8.9: the parent references of <c>currency_step</c>,
/// <c>currency_guard</c> and <c>socket_tag_rule</c>, the guard that may not reach into another currency's
/// steps, the currency's step set against its declared ceiling, and the socket type whose accept and reject
/// tags disagree.
/// <para>
/// The field indices below are the positions each type's <c>CreateSchema</c> declares.
/// </para>
/// </summary>
internal static class CurrencyAndSocketChecks
{
    const int CurrencyMaxSteps = 4;

    const int StepCurrencyId = 0;
    const int StepSort = 1;

    const int GuardCurrencyId = 0;
    const int GuardStepId = 1;

    const int SocketTagRuleSocketTypeId = 0;
    const int SocketTagRuleTagId = 2;
    const int SocketTagRuleRule = 3;

    internal static void Run(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        Dictionary<int, long> currencyOfStep = CheckSteps(candidate, findings);
        CheckGuards(candidate, currencyOfStep, findings);
        CheckSocketTypes(candidate, findings);
    }

    /// <summary>
    /// <c>KEC0100</c> on each step's currency and <c>KEC0110</c> on the set: the declared ceiling, the row
    /// count against it, and a sort that two steps of one currency share. Hands back the step to currency
    /// map the guard check needs, so the step rows are walked once.
    /// </summary>
    static Dictionary<int, long> CheckSteps(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        List<ContentRow> steps = LiveRows(candidate, InstanceContentTypeIds.CurrencyStepTypeId);
        var currencyOfStep = new Dictionary<int, long>(steps.Count);
        var countPerCurrency = new Dictionary<long, int>();
        var sortsTaken = new Dictionary<(long Currency, long Sort), int>();

        foreach (ContentRow row in steps)
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.CurrencyStepTypeKey,
                StepCurrencyId,
                CurrencyStepContentType.CraftingCurrencyIdField,
                InstanceContentTypeIds.CraftingCurrencyTypeId,
                InstanceContentTypeIds.CraftingCurrencyTypeKey);

            long currencyId = Number(row, StepCurrencyId) ?? 0;
            currencyOfStep[row.Id] = currencyId;
            countPerCurrency[currencyId] = countPerCurrency.TryGetValue(currencyId, out int seen) ? seen + 1 : 1;

            long sort = Number(row, StepSort) ?? 0;
            if (sortsTaken.TryGetValue((currencyId, sort), out int firstId))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.CurrencyStepSet,
                    InstanceContentFindings.CurrencyDuplicateSort(row.Id, sort, currencyId, firstId)));
                continue;
            }

            sortsTaken.Add((currencyId, sort), row.Id);
        }

        foreach (ContentRow currency in LiveRows(candidate, InstanceContentTypeIds.CraftingCurrencyTypeId))
        {
            long maxSteps = Number(currency, CurrencyMaxSteps) ?? 0;
            if (maxSteps > CraftingCurrencyContentType.MaxSteps)
            {
                findings.Add(new ContentFinding(
                    currency.Type,
                    currency.Id,
                    InstanceContentFindings.CurrencyStepSet,
                    InstanceContentFindings.CurrencyMaxSteps(
                        currency.Id, maxSteps, CraftingCurrencyContentType.MaxSteps)));
            }

            int carried = countPerCurrency.TryGetValue(currency.Id, out int count) ? count : 0;
            if (carried > maxSteps)
            {
                findings.Add(new ContentFinding(
                    currency.Type,
                    currency.Id,
                    InstanceContentFindings.CurrencyStepSet,
                    InstanceContentFindings.CurrencyStepCount(currency.Id, carried, maxSteps)));
            }
        }

        return currencyOfStep;
    }

    /// <summary>
    /// <c>KEC0100</c> on each guard's currency and, where the guard names a step, on that step belonging to
    /// the SAME currency. An ABSENT step reference is a target guard and is the ordinary shape, which is
    /// what makes the two guard kinds one type.
    /// </summary>
    static void CheckGuards(
        IContentSnapshot candidate,
        Dictionary<int, long> currencyOfStep,
        ICollection<ContentFinding> findings)
    {
        foreach (ContentRow row in LiveRows(candidate, InstanceContentTypeIds.CurrencyGuardTypeId))
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.CurrencyGuardTypeKey,
                GuardCurrencyId,
                CurrencyGuardContentType.CraftingCurrencyIdField,
                InstanceContentTypeIds.CraftingCurrencyTypeId,
                InstanceContentTypeIds.CraftingCurrencyTypeKey);

            long? stepId = Number(row, GuardStepId);
            if (stepId is not long named || named == 0)
            {
                continue;
            }

            long guardCurrency = Number(row, GuardCurrencyId) ?? 0;
            if (!IsLive(candidate, InstanceContentTypeIds.CurrencyStepTypeId, named)
                || !currencyOfStep.TryGetValue((int)named, out long stepCurrency))
            {
                findings.Add(new ContentFinding(
                    row.Type,
                    row.Id,
                    InstanceContentFindings.ParentUnresolved,
                    InstanceContentFindings.ParentMissing(
                        InstanceContentTypeIds.CurrencyGuardTypeKey,
                        row.Id,
                        CurrencyGuardContentType.CurrencyStepIdField,
                        InstanceContentTypeIds.CurrencyStepTypeKey,
                        named)));
                continue;
            }

            if (stepCurrency == guardCurrency)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                row.Type,
                row.Id,
                InstanceContentFindings.ParentUnresolved,
                InstanceContentFindings.GuardOnForeignStep(row.Id, named, (int)stepCurrency, guardCurrency)));
        }
    }

    /// <summary>
    /// <c>KEC0100</c> on each tag rule's socket type and <c>KEC0108</c> on a socket type whose accept and
    /// reject sets are not disjoint. Reject wins over accept, so an overlap is a dead accept row rather than
    /// a subtle precedence question.
    /// </summary>
    static void CheckSocketTypes(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        List<ContentRow> rules = LiveRows(candidate, InstanceContentTypeIds.SocketTagRuleTypeId);
        var rejected = new Dictionary<(int SocketType, long Tag), int>();

        foreach (ContentRow row in rules)
        {
            CheckParent(
                candidate,
                findings,
                row,
                InstanceContentTypeIds.SocketTagRuleTypeKey,
                SocketTagRuleSocketTypeId,
                SocketTagRuleContentType.SocketTypeIdField,
                InstanceContentTypeIds.SocketTypeTypeId,
                InstanceContentTypeIds.SocketTypeTypeKey);

            if (Key(row) is { } key
                && (Number(row, SocketTagRuleRule) ?? 0) == SocketTagRuleContentType.RuleReject)
            {
                _ = rejected.TryAdd(key, row.Id);
            }
        }

        // The second walk is over the ROWS in id order rather than over a dictionary, so the findings come
        // out in the same order on every run and two sweeps of one candidate agree.
        var reported = new HashSet<(int SocketType, long Tag)>();
        foreach (ContentRow row in rules)
        {
            if (Key(row) is not { } key
                || (Number(row, SocketTagRuleRule) ?? 0) != SocketTagRuleContentType.RuleAccept
                || !rejected.TryGetValue(key, out int rejectRowId)
                || !reported.Add(key))
            {
                continue;
            }

            findings.Add(new ContentFinding(
                Type(InstanceContentTypeIds.SocketTypeTypeId),
                key.SocketType,
                InstanceContentFindings.SocketTagOverlap,
                InstanceContentFindings.SocketOverlap(key.SocketType, key.Tag, row.Id, rejectRowId)));
        }
    }

    /// <summary>One tag rule's socket type and tag pair, or null when the socket type is not an id at all.</summary>
    static (int SocketType, long Tag)? Key(ContentRow row)
    {
        long socketTypeId = Number(row, SocketTagRuleSocketTypeId) ?? 0;
        return socketTypeId is <= 0 or > int.MaxValue
            ? null
            : ((int)socketTypeId, Number(row, SocketTagRuleTagId) ?? 0);
    }
}
