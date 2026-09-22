using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// ONE staged append, kept until <see cref="ContentUpgradePlanBuilder.Build"/> can answer whether the tag id
/// names a row. It carries the row and the field as well as the id, because the refusal has to say which
/// append it is talking about and a planner staging six of them cannot be left to guess.
/// </summary>
/// <param name="Type">The appended row's content type.</param>
/// <param name="Id">The appended row's stable id.</param>
/// <param name="Key">The appended row's key.</param>
/// <param name="FieldName">The tag-list field.</param>
/// <param name="TagId">The tag id the append names.</param>
readonly record struct ContentUpgradeTagAppend(
    ContentTypeId Type,
    int Id,
    ContentKey Key,
    string FieldName,
    int TagId);

/// <summary>
/// Whether a tag id an append names will be a LIVE TAG ROW of the published version, which is the question
/// the publish validator asks of the list afterwards and the one a planner has to ask first.
/// <para>
/// <b>It mirrors <c>KEC0008</c> exactly.</b> That check accepts a list entry only when the tag type holds the
/// row AND the row is not retired, so this one accepts the same two and nothing else. A retired tag row is
/// therefore refused: it keeps its id and its bytes forever so a stored stack still decodes, and that is not
/// the same as being a tag content may still be given.
/// </para>
/// <para>
/// <b>A row this plan ADDS counts.</b> Adding the tag row and appending it in one definition is an ordinary
/// upgrade, so the answer is deferred to <see cref="ContentUpgradePlanBuilder.Build"/> rather than taken at
/// the call. Taking it at the call would make the two call orders behave differently, which is a rule nobody
/// can remember and nothing in the shape of the builder suggests.
/// </para>
/// <para>
/// Without this the mistake survived the whole plan: the builder emitted <c>Changes</c>, the runner published,
/// the validator raised <c>KEC0008</c>, the store threw and the run ended FAILED with a version half written,
/// where every other planner mistake is a refusal that changes nothing.
/// </para>
/// </summary>
static class ContentUpgradeTagRows
{
    /// <summary>
    /// The refusal for the FIRST staged append whose tag id names no live tag row, or null when every one of
    /// them resolves. An empty list of appends is the ordinary case and answers null without a lookup.
    /// </summary>
    /// <param name="context">The context the planner is building against.</param>
    /// <param name="additions">The committed rows this plan adds, which may include tag rows.</param>
    /// <param name="appends">Every append this plan staged, in the order they were staged.</param>
    internal static string? Unresolved(
        ContentUpgradeContext context,
        IReadOnlyList<ContentBundleRow> additions,
        IReadOnlyList<ContentUpgradeTagAppend> appends)
    {
        if (appends.Count == 0)
        {
            return null;
        }

        // A build that registers no tag type at all resolves nothing, which is the same answer: the catalog
        // holds no tag row under that id, and a published list naming one would be the validator's KEC0007.
        context.Registry.TryGetByKey(EngineContentTypes.TagTypeKey, out ContentTypeRegistration? tagType);
        for (int i = 0; i < appends.Count; i++)
        {
            ContentUpgradeTagAppend append = appends[i];
            if (tagType is not null && Resolves(context.Baseline, additions, tagType.Type, append.TagId))
            {
                continue;
            }

            return FormattableString.Invariant(
                $"The catalog holds no live tag row {append.TagId}, so this upgrade cannot append it to {ContentUpgradeChecks.TypeName(context.Registry, append.Type)} {append.Id} '{append.Key}' field '{append.FieldName}'. A tag list names tag ROWS, and a tag that does not exist in the catalog is refused here rather than at the publish. Nothing was changed.");
        }

        return null;
    }

    /// <summary>
    /// One tag id against the rows this plan will leave live: the ones it adds, then the ones the baseline
    /// already carries. The additions are asked first because they are the shorter list by far and because a
    /// baseline can never hold an id an addition names.
    /// </summary>
    /// <param name="baseline">The catalog as it stands.</param>
    /// <param name="additions">The committed rows this plan adds.</param>
    /// <param name="tagType">The tag content type.</param>
    /// <param name="tagId">The tag id the append names.</param>
    static bool Resolves(
        ContentBundle baseline,
        IReadOnlyList<ContentBundleRow> additions,
        ContentTypeId tagType,
        int tagId)
    {
        for (int i = 0; i < additions.Count; i++)
        {
            ContentBundleRow added = additions[i];
            if (added.Type.Value == tagType.Value && added.Id == tagId && !added.IsRetired)
            {
                return true;
            }
        }

        return ContentUpgradeChecks.FindById(baseline, tagType, tagId) is ContentBundleRow held
            && !held.IsRetired;
    }
}
