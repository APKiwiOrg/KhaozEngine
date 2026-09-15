using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The row an edit names, and the key a draft deduplicates on: the content type, the definition id and the
/// content key TOGETHER. It is <c>catalog_draft_edit</c>'s unique index
/// <c>(type_id, definition_id, content_key)</c> as a value (spec 4.4).
/// <para>
/// All three halves matter. An ordinary add carries id 0, so its uniqueness comes from the KEY half, which
/// is what refuses two adds of the same key in one draft. An update and a retire of a published row carry
/// both, and two rows of different types never collide at all.
/// </para>
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="DefinitionId">The definition id, or 0 for a row whose id does not exist yet.</param>
/// <param name="Key">The row's key, compared ordinally over its UTF-8 bytes.</param>
public readonly record struct ContentEditTarget(ContentTypeId Type, int DefinitionId, ContentKey Key);

/// <summary>
/// The ordered, deduplicated edit list that is the durable form of a draft (spec 2.3). It holds ONE pending
/// intent per target, in the order the edits were applied, which is the order ids are allocated in at
/// publish and the order the audit will show.
/// <para>
/// <b>Dedup and collision are two different answers to a second edit of an occupied target</b>, and the
/// split is spec 4.4's. A second edit under the SAME operation REPLACES the first, so a console that saves
/// the same row twice updates the one edit rather than queueing two. A second edit under a DIFFERENT
/// operation is REFUSED, because an update followed by a retire would otherwise silently flip the first
/// edit's operation and drop its fields. An operator who wants both gets them in two publishes.
/// </para>
/// <para>
/// It is a MUTABLE collection, deliberately: a draft accumulates edits across requests and a provider
/// rebuilds one from <c>catalog_draft_edit</c> at every read. It holds no ambient state and is not thread
/// safe, so a store serialises its own access the way its backend already does.
/// </para>
/// </summary>
public sealed class ContentChangeSet
{
    readonly List<ContentEdit> _edits = [];
    readonly Dictionary<ContentEditTarget, int> _byTarget = [];

    /// <summary>An empty change set, which is what an open draft with nothing pending holds.</summary>
    public ContentChangeSet()
    {
    }

    /// <summary>
    /// Rebuilds a change set from edits already stored, in their stored ordinal order. A collision throws,
    /// because a provider reading its own unique-indexed table cannot produce one and a store that does has
    /// a defect worth surfacing at the read rather than at the publish.
    /// </summary>
    /// <param name="edits">The stored edits, in edit-ordinal order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="edits"/> or one of its entries is null.</exception>
    /// <exception cref="ContentAuthoringException">Two stored edits name one target.</exception>
    public ContentChangeSet(IReadOnlyList<ContentEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        for (int i = 0; i < edits.Count; i++)
        {
            Apply(edits[i] ?? throw new ArgumentNullException(
                nameof(edits), FormattableString.Invariant($"Edit {i} is null.")));
        }
    }

    /// <summary>The pending edits, one per target, in the order they were applied.</summary>
    public IReadOnlyList<ContentEdit> Edits => _edits;

    /// <summary>How many edits are pending.</summary>
    public int Count => _edits.Count;

    /// <summary>The target one edit names, which is what this set deduplicates on.</summary>
    /// <param name="edit">The edit.</param>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    public static ContentEditTarget TargetOf(ContentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return new ContentEditTarget(edit.Type, edit.DefinitionId, edit.Key);
    }

    /// <summary>
    /// Applies one edit, replacing a standing edit of the same target and operation and REFUSING one whose
    /// operation differs.
    /// </summary>
    /// <param name="edit">The edit to apply.</param>
    /// <param name="standing">The standing edit that refused it, populated only when this returns false.</param>
    /// <returns>True when the edit was added or replaced an equivalent one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    public bool TryApply(ContentEdit edit, [MaybeNullWhen(true)] out ContentEdit standing)
    {
        ContentEditTarget target = TargetOf(edit);
        if (!_byTarget.TryGetValue(target, out int index))
        {
            _byTarget.Add(target, _edits.Count);
            _edits.Add(edit);
            standing = null;
            return true;
        }

        ContentEdit held = _edits[index];
        if (held.Operation != edit.Operation)
        {
            standing = held;
            return false;
        }

        // Same target, same operation: the console saved the row twice and the newer edit wins the slot the
        // older one already holds, so the draft never carries two intents for one row.
        _edits[index] = edit;
        standing = null;
        return true;
    }

    /// <summary>
    /// Applies one edit, throwing the refusal <see cref="TryApply"/> reports. This is the path a caller
    /// takes when a collision is a programming error rather than an operator's mistake.
    /// </summary>
    /// <param name="edit">The edit to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    /// <exception cref="ContentAuthoringException">The target is held under a different operation.</exception>
    public void Apply(ContentEdit edit)
    {
        if (TryApply(edit, out ContentEdit? standing))
        {
            return;
        }

        throw new ContentAuthoringException(
            FormattableString.Invariant(
                $"The open draft already holds a {standing.Operation} edit for type {edit.Type.Value} row {edit.DefinitionId} ('{edit.Key}'), so a {edit.Operation} of the same row is refused. A draft holds one pending intent per row."),
            edit.Type,
            edit.DefinitionId,
            ContentAuthoringException.EditTargetCollisionReason);
    }

    /// <summary>The standing edit for one target, when the draft holds one.</summary>
    /// <param name="target">The target to look up.</param>
    /// <param name="edit">The standing edit.</param>
    /// <returns>True when the draft holds an edit for that target.</returns>
    public bool TryGet(ContentEditTarget target, [MaybeNullWhen(false)] out ContentEdit edit)
    {
        if (_byTarget.TryGetValue(target, out int index))
        {
            edit = _edits[index];
            return true;
        }

        edit = null;
        return false;
    }

    /// <summary>Drops every pending edit, which is what discarding a draft does to its change set.</summary>
    public void Clear()
    {
        _edits.Clear();
        _byTarget.Clear();
    }
}
