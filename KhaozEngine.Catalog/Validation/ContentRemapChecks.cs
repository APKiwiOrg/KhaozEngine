using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Pass 5 of the sweep, REMAP RULES (spec 5.3): the full ordered set, its sequences, its endpoints and its
/// idempotence. It needs pass 1 to have walked the ids, because a rule's endpoints are rows.
/// <para>
/// Rules are APPEND ONLY and a published version's set is permanent, so every check here is about the set
/// as a whole rather than about the rule being added. A row is never deleted either, which is what makes
/// <c>KEC0016</c> answerable from the candidate alone: a <c>FromId</c> that no row of its type carries is an
/// id that never existed.
/// </para>
/// </summary>
internal static class ContentRemapChecks
{
    internal static void Run(ContentValidationRun run)
    {
        IReadOnlyList<RemapRule> rules = run.Rules;
        if (rules.Count == 0)
        {
            return;
        }

        if (CheckSequences(run, rules))
        {
            CheckIdempotence(run, rules);
        }

        foreach (RemapRule rule in rules)
        {
            if (rule is null)
            {
                continue;
            }

            CheckEndpoints(run, rule);
            CheckPayload(run, rule);
        }
    }

    /// <summary>
    /// <c>KEC0018</c>: sequences are contiguous from 1 and strictly ascending. The sequence IS the apply
    /// order, so a set that lost or repeated one is not the list any durable page was brought forward
    /// through.
    /// <para>
    /// Answers whether <see cref="RemapRuleSet"/> can HOLD the set, which is a weaker question than the one
    /// the finding asks. Its constructor throws on a null rule and on a sequence that is not strictly
    /// ascending, and the validator never throws for a content reason, so those two shapes skip the
    /// idempotence walk below. A set that is ascending but does not start at 1 is <c>KEC0018</c> and is
    /// still walked, because a finding that masked another finding would be the sweep stopping early.
    /// </para>
    /// </summary>
    static bool CheckSequences(ContentValidationRun run, IReadOnlyList<RemapRule> rules)
    {
        bool constructible = true;
        int last = int.MinValue;
        for (int i = 0; i < rules.Count; i++)
        {
            RemapRule rule = rules[i];
            if (rule is null)
            {
                // A null rule is a programming error rather than a content defect, so it is skipped rather
                // than reported, and the set is never handed to a constructor that would throw on it.
                constructible = false;
                continue;
            }

            if (rule.Sequence <= last)
            {
                constructible = false;
            }

            last = rule.Sequence;
            if (rule.Sequence == i + 1)
            {
                continue;
            }

            run.Add(
                rule.Type,
                rule.FromId,
                "KEC0018",
                FormattableString.Invariant(
                    $"Remap rule at position {i} carries sequence {rule.Sequence} where the set runs contiguously from 1, so it should carry {i + 1}. The sequence is the apply order, and a gap or a repeat is a set no page was migrated through."));
        }

        return constructible;
    }

    /// <summary>
    /// <c>KEC0015</c>: the set is idempotent. Applying the whole ordered set twice must produce what
    /// applying it once produced, which is what makes a crash between apply and commit safe. The walk is
    /// <see cref="RemapRuleSet.IsIdempotent"/>, so the rule has ONE implementation and the validator and the
    /// apply pass can never disagree about it.
    /// </summary>
    static void CheckIdempotence(ContentValidationRun run, IReadOnlyList<RemapRule> rules)
    {
        var set = new RemapRuleSet(rules);
        if (set.IsIdempotent(out RemapRule? offending))
        {
            return;
        }

        run.Add(
            offending.Type,
            offending.FromId,
            "KEC0015",
            FormattableString.Invariant(
                $"Remap rule {offending.Sequence} sends {offending.FromId} to {offending.Destination}, which an earlier rule of the same type already names as a source. A second pass would chain it again, so the set is not idempotent."));
    }

    /// <summary>
    /// <c>KEC0016</c> on a source that never existed and <c>KEC0017</c> on a destination the rule could not
    /// have named. A destination of 0 fails either half on purpose: 0 is the reserved no-content id, so a
    /// rule that moves a page onto it names no row at all.
    /// <para>
    /// <b>The liveness half is asked of the version under sweep ONLY, and that split is the whole point.</b>
    /// Spec 5.2 defines <c>KEC0017</c> as a destination that is not live AT <c>IntroducedIn</c>, and the
    /// candidate answers that question for one version, its own. Rules are append only and permanent
    /// (contracts 8.1 and 8.5), so the full list is re-swept on every later publish, and checking a historic
    /// rule's destination for liveness in TODAY's candidate refuses a legal chain the moment its middle row
    /// is retired: 10 replaced by 11 at version 3, then 11 replaced by 12 at version 7, is a rule set no
    /// later publish could ever get past. What a historic rule is still owed is that its destination EXISTS,
    /// which the candidate does answer, because a row is never deleted and <c>KEC0016</c> already leans on
    /// exactly that.
    /// </para>
    /// <para>
    /// For a rule introduced in the version under sweep, both halves hold and publish step 6.5 relies on it:
    /// a <c>Retire</c> under the replacement policy and a <c>Fork</c> both write their destination row in
    /// the same transaction and BEFORE appending the rule, so a destination that is missing or already
    /// retired is the edit being written in the wrong order. An author retiring two rows into one live
    /// target in a single publish writes both rules to that target directly rather than chaining them
    /// through a row this version also retires, so refusing the same-version chain costs nothing legal.
    /// </para>
    /// </summary>
    static void CheckEndpoints(ContentValidationRun run, RemapRule rule)
    {
        if (!run.Candidate.TryGetRow(rule.Type, rule.FromId, out _))
        {
            run.Add(
                rule.Type,
                rule.FromId,
                "KEC0016",
                FormattableString.Invariant(
                    $"Remap rule {rule.Sequence} names source {rule.FromId}, which no row of its type carries. A row is never deleted, so an id the candidate does not hold is an id that never existed."));
        }

        if (!CarriesDestination(rule))
        {
            return;
        }

        int destination = rule.Destination;
        bool exists = destination > 0 && run.Candidate.TryGetRow(rule.Type, destination, out _);
        if (rule.IntroducedIn != run.Candidate.VersionNumber)
        {
            if (exists)
            {
                return;
            }

            run.Add(
                rule.Type,
                rule.FromId,
                "KEC0017",
                FormattableString.Invariant(
                    $"Remap rule {rule.Sequence}, introduced at version {rule.IntroducedIn}, sends {rule.FromId} to {destination}, which names a row that does not exist. A row is never deleted, so an id no row of the type carries is an id that never existed. Whether it was LIVE is a question only the publish that appended the rule could answer."));
            return;
        }

        if (exists && !run.Candidate.IsRetired(rule.Type, destination))
        {
            return;
        }

        run.Add(
            rule.Type,
            rule.FromId,
            "KEC0017",
            FormattableString.Invariant(
                $"Remap rule {rule.Sequence} sends {rule.FromId} to {destination}, which is not a row live at version {rule.IntroducedIn}. A rule is appended after the row it names is written, so the destination is live by the time the rule exists."));
    }

    /// <summary>
    /// <c>KEC0019</c>: a payload longer than the cap, or malformed for its kind. The LENGTH and the byte
    /// shape are already refused by <see cref="RemapRule"/>'s own constructor and by the rule chunk decoder,
    /// so what is left for the validator is the part a byte shape cannot say: a retire under the replacement
    /// policy that replaces with nothing, and a lowered stack cap that is not a stack at all.
    /// </summary>
    static void CheckPayload(ContentValidationRun run, RemapRule rule)
    {
        if (rule.PayloadLength > RemapRule.MaxPayloadBytes)
        {
            run.Add(
                rule.Type,
                rule.FromId,
                "KEC0019",
                FormattableString.Invariant(
                    $"Remap rule {rule.Sequence} carries a {rule.PayloadLength} byte payload, over the {RemapRule.MaxPayloadBytes} byte cap."));
            return;
        }

        ReadOnlySpan<byte> payload = rule.Payload;
        switch (rule.Kind)
        {
            case RemapRuleKind.Retired when payload.Length == 1 + sizeof(int)
                && payload[0] == RemapRule.RetirePolicyReplacement
                && BinaryPrimitives.ReadInt32LittleEndian(payload[1..]) <= 0:
                run.Add(
                    rule.Type,
                    rule.FromId,
                    "KEC0019",
                    FormattableString.Invariant(
                        $"Remap rule {rule.Sequence} retires {rule.FromId} under the replacement policy and names replacement {BinaryPrimitives.ReadInt32LittleEndian(payload[1..])}. Id 0 is no content, so a replacement policy has to name a row."));
                break;
            case RemapRuleKind.StackCapLowered when rule.TryGetStackCap(out int cap) && cap < 1:
                run.Add(
                    rule.Type,
                    rule.FromId,
                    "KEC0019",
                    FormattableString.Invariant(
                        $"Remap rule {rule.Sequence} lowers the stack cap of {rule.FromId} to {cap}. One slot holds at least one of anything, so a cap below 1 is not a cap."));
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// True for a rule that names a destination row. The two plain id remaps always do. A retire does only
    /// under the REPLACEMENT policy, because a placeholder retire and a lowered stack cap move no id at all
    /// and so have no destination to check.
    /// </summary>
    static bool CarriesDestination(RemapRule rule) => rule.Kind switch
    {
        RemapRuleKind.ReplacedBy or RemapRuleKind.MovedToLegacy => true,
        RemapRuleKind.Retired => !rule.IsRetiredPlaceholder,
        _ => false,
    };
}
