using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The four edit operations of spec 3.7. The NUMBERING is durable and is written out explicitly rather than
/// left to declaration order, because <c>catalog_draft_edit.operation</c> stores it under a
/// <c>CHECK (operation IN (1, 2, 3, 4))</c> (spec 4.4).
/// </summary>
public enum ContentEditOperation
{
    /// <summary>Allocates an id and writes a row valid from the new version.</summary>
    Add = 1,

    /// <summary>Closes the current row and writes a successor with the merged field set.</summary>
    Update = 2,

    /// <summary>
    /// Closes the current row, writes a successor with the retired flag set and appends a
    /// <see cref="RemapRuleKind.Retired"/> rule. Irreversible for pages already migrated past it.
    /// </summary>
    Retire = 3,

    /// <summary>
    /// The keep-legacy copy. Allocates a NEW id, copies the source row's whole field set onto it under the
    /// new key, sets the named flag field on the copy, applies the changed fields to the ORIGINAL, and
    /// appends a <see cref="RemapRuleKind.MovedToLegacy"/> rule from the original id to the new one.
    /// </summary>
    Fork = 4,
}

/// <summary>
/// What a <see cref="ContentEditOperation.Retire"/> tells a loaded page to do, contracts 8.2. The numbers
/// ARE the remap rule payload's first byte, so the two cannot drift, and <c>catalog_draft_edit</c> stores
/// them under a <c>CHECK (retire_policy IN (0, 1, 2))</c>.
/// </summary>
public enum ContentRetirePolicy : byte
{
    /// <summary>No policy, which is what every operation other than a retire carries.</summary>
    None = 0,

    /// <summary>The reference is kept and the item is shown through a placeholder, unusable and untradable.</summary>
    Placeholder = RemapRule.RetirePolicyPlaceholder,

    /// <summary>The reference moves to a replacement id, and the behaviour is then a plain replace.</summary>
    Replacement = RemapRule.RetirePolicyReplacement,
}

/// <summary>
/// One CHANGED field on one edit: the schema field's name and the value the edit sets it to. An edit stores
/// the changed fields only and never the whole row (spec 3.7), which is what lets the audit record a
/// field-level before and after with no extra table, and what makes two operators editing different fields
/// of one row a merge rather than a last-write-wins clobber.
/// </summary>
/// <param name="Name">The schema field name, compared ORDINALLY like every other key in the catalog.</param>
/// <param name="Value">The value, carrying its own kind so a provider knows which column to write.</param>
public readonly record struct ContentFieldEdit(string Name, ContentFieldValue Value);

/// <summary>
/// One pending edit against the open draft (spec 3.7): its type, the row it TARGETS, the operation, and the
/// fields it changes. Built through the four factories rather than a constructor, so an operation can never
/// be paired with a payload it has no meaning for.
/// <para>
/// <b>Ids are allocated at PUBLISH</b> (spec 6.3), so an ordinary <see cref="ContentEditOperation.Add"/>
/// carries <see cref="DefinitionId"/> 0 and a <see cref="ContentEditOperation.Fork"/> carries no id for its
/// copy at all. The one exception is a bulk import into an EMPTY database, which may carry the bundle's own
/// id (contracts 5.1), and <see cref="ContentEdit.Import"/> is the only path that writes one.
/// </para>
/// </summary>
public sealed class ContentEdit
{
    ContentEdit(
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        ContentEditOperation operation,
        IReadOnlyList<ContentFieldEdit> fields,
        ContentRetirePolicy retirePolicy,
        int replacementId,
        ContentKey forkKey,
        string? forkFlagField,
        long? familyId,
        bool importedAsRetired = false)
    {
        Type = type;
        DefinitionId = definitionId;
        Key = key;
        Operation = operation;
        Fields = Copy(fields);
        RetirePolicy = retirePolicy;
        ReplacementId = replacementId;
        ForkKey = forkKey;
        ForkFlagField = forkFlagField;
        FamilyId = familyId;
        ImportedAsRetired = importedAsRetired;
    }

    /// <summary>The content type the edited row belongs to.</summary>
    public ContentTypeId Type { get; }

    /// <summary>
    /// The definition id the edit names, or 0 for an ordinary add whose id does not exist yet. For a
    /// <see cref="ContentEditOperation.Fork"/> this is the SOURCE row, never the copy.
    /// </summary>
    public int DefinitionId { get; }

    /// <summary>The row's key, which is the other half of the target the draft deduplicates on.</summary>
    public ContentKey Key { get; }

    /// <summary>Which of the four operations this is.</summary>
    public ContentEditOperation Operation { get; }

    /// <summary>
    /// The CHANGED fields only, in authored order. For a <see cref="ContentEditOperation.Fork"/> these are
    /// the changes to the ORIGINAL row, which is the direction that reads correctly at a console: the author
    /// is editing the row they have open, and the fork is how they keep what players already rolled.
    /// </summary>
    public IReadOnlyList<ContentFieldEdit> Fields { get; }

    /// <summary>The retire policy, and <see cref="ContentRetirePolicy.None"/> for every other operation.</summary>
    public ContentRetirePolicy RetirePolicy { get; }

    /// <summary>The destination id of a replacement-policy retire, and 0 otherwise.</summary>
    public int ReplacementId { get; }

    /// <summary>The COPY's key on a fork, and the default key on every other operation.</summary>
    public ContentKey ForkKey { get; }

    /// <summary>
    /// The name of the <see cref="ContentFieldKind.Bool"/> field a fork sets to true ON THE COPY, and null
    /// on every other operation. The field is the CALLER's: the engine has no opinion about which boolean
    /// means superseded on a type it did not define, so it checks only that the field exists and is Bool.
    /// </summary>
    public string? ForkFlagField { get; }

    /// <summary>The family an add joins, or null when the row is allocated from the plain id counter.</summary>
    public long? FamilyId { get; }

    /// <summary>
    /// Whether the imported row is ALREADY RETIRED, which only <see cref="Import"/> ever sets. A bundle
    /// carries every live row including the retired ones, and a lossless import has to reproduce them
    /// without appending a second retire rule, because the bundle already carries the rule that retired
    /// them. Every other path reaches the retired flag through a <see cref="ContentEditOperation.Retire"/>,
    /// which is the operation that appends the rule.
    /// </summary>
    public bool ImportedAsRetired { get; }

    /// <summary>A new row. Its id is allocated at publish, so the edit carries none.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The new row's key, unique within its type and immutable once published.</param>
    /// <param name="fields">The fields the row is created with.</param>
    /// <param name="familyId">The family to allocate the id from, or null for the plain counter.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    public static ContentEdit Add(
        ContentTypeId type,
        ContentKey key,
        IReadOnlyList<ContentFieldEdit> fields,
        long? familyId = null)
    {
        RequireKey(key, nameof(key));
        return new ContentEdit(
            type, 0, key, ContentEditOperation.Add, fields, ContentRetirePolicy.None, 0, default, null, familyId);
    }

    /// <summary>
    /// A new row CARRYING its own id, which only a bulk import into an empty database may write (contracts
    /// 5.1, spec 10.9). Every other write path leaves the id to the allocator.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The id the bundle named, or 0 to let the allocator issue one.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="fields">The fields the row is created with.</param>
    /// <param name="familyId">The family the row belongs to, or null.</param>
    /// <param name="isRetired">Whether the row is already retired, which a bundle's retired rows carry.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definitionId"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    public static ContentEdit Import(
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        IReadOnlyList<ContentFieldEdit> fields,
        long? familyId = null,
        bool isRetired = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(definitionId);
        RequireKey(key, nameof(key));
        return new ContentEdit(
            type,
            definitionId,
            key,
            ContentEditOperation.Add,
            fields,
            ContentRetirePolicy.None,
            0,
            default,
            null,
            familyId,
            isRetired);
    }

    /// <summary>Changes some of an existing row's fields, leaving every field it does not name alone.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The row's id.</param>
    /// <param name="key">The row's key, which an update never changes.</param>
    /// <param name="fields">The changed fields only.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definitionId"/> is below 1.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    public static ContentEdit Update(
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        IReadOnlyList<ContentFieldEdit> fields)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(definitionId, 1);
        RequireKey(key, nameof(key));
        return new ContentEdit(
            type,
            definitionId,
            key,
            ContentEditOperation.Update,
            fields,
            ContentRetirePolicy.None,
            0,
            default,
            null,
            null);
    }

    /// <summary>
    /// Takes a definition out of play. The row stays in the pack forever so a stored stack still decodes,
    /// and the policy is carried by the appended remap rule rather than by the row.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The row's id, or 0 when the retire names a row added in the same draft.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="policy">Placeholder or replacement, contracts 8.2.</param>
    /// <param name="replacementId">The destination id under the replacement policy, and 0 otherwise.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty, or the policy and the replacement id disagree.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definitionId"/> or <paramref name="replacementId"/> is negative.</exception>
    public static ContentEdit Retire(
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        ContentRetirePolicy policy,
        int replacementId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(definitionId);
        ArgumentOutOfRangeException.ThrowIfNegative(replacementId);
        RequireKey(key, nameof(key));

        if (policy == ContentRetirePolicy.None)
        {
            throw new ArgumentException(
                "A retire names a policy of placeholder or replacement, contracts 8.2.", nameof(policy));
        }

        if (policy == ContentRetirePolicy.Placeholder && replacementId != 0)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"A placeholder retire moves no id, so it cannot name replacement id {replacementId}."),
                nameof(replacementId));
        }

        return new ContentEdit(
            type, definitionId, key, ContentEditOperation.Retire, [], policy, replacementId, default, null, null);
    }

    /// <summary>
    /// The keep-legacy fork of spec 3.7, which carries FIVE things and is applied or refused whole: the
    /// source id, the copy's new key, the <see cref="ContentFieldKind.Bool"/> flag field to set on the copy,
    /// the changed fields for the ORIGINAL, and no id for the copy.
    /// <para>
    /// It is one operation rather than three because the three are not separable. Allocating the id, copying
    /// the fields and appending the rule are one atomic statement about one definition, and an author who
    /// did them as three edits could have the publish succeed with the rule missing, which is the state
    /// nothing can detect afterwards: the pages are already migrated and the original values are gone.
    /// </para>
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The SOURCE row's id, which keeps its id and takes the changed fields.</param>
    /// <param name="key">The source row's key.</param>
    /// <param name="forkKey">The COPY's key. Required: a key is immutable once published and the engine will not invent one.</param>
    /// <param name="flagField">The Bool field set to true on the copy. The field is the caller's to name.</param>
    /// <param name="fields">The changed fields for the ORIGINAL row.</param>
    /// <exception cref="ArgumentException">A key is empty, or <paramref name="flagField"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definitionId"/> is below 1.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> or <paramref name="flagField"/> is null.</exception>
    public static ContentEdit Fork(
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        ContentKey forkKey,
        string flagField,
        IReadOnlyList<ContentFieldEdit> fields)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(definitionId, 1);
        RequireKey(key, nameof(key));
        RequireKey(forkKey, nameof(forkKey));
        ArgumentNullException.ThrowIfNull(flagField);

        if (string.IsNullOrWhiteSpace(flagField))
        {
            throw new ArgumentException(
                "A fork names the Bool field it sets on the copy, and the name may not be blank.",
                nameof(flagField));
        }

        return new ContentEdit(
            type,
            definitionId,
            key,
            ContentEditOperation.Fork,
            fields,
            ContentRetirePolicy.None,
            0,
            forkKey,
            flagField,
            null);
    }

    static void RequireKey(ContentKey key, string parameterName)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("A content key may not be empty, contracts 5.3.", parameterName);
        }
    }

    static ContentFieldEdit[] Copy(IReadOnlyList<ContentFieldEdit> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var copy = new ContentFieldEdit[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEdit field = fields[i];
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Field edit {i} names no field."), nameof(fields));
            }

            copy[i] = field;
        }

        return copy;
    }
}

/// <summary>
/// The ONE open draft (spec 3.7): a change set against the last published version rather than a copy of it,
/// with the stamps that say who opened it and when. There is exactly one per database.
/// <para>
/// <b>It carries the FREEZE marker of spec 6.2.</b> Step 1 of a publish marks the draft frozen for the base
/// version it is publishing, and while that marker stands every write to the draft is refused with
/// <see cref="ContentAuthoringException.PublishInProgressReason"/>, so the change set the pipeline read at
/// step 1 is the one step 10 deletes. The marker is DURABLE rather than a held lock, because steps 1 to 10
/// span the pack writes and no provider here holds a row lock across those.
/// </para>
/// </summary>
public sealed class ContentDraft
{
    /// <summary>Builds the draft a store hands back.</summary>
    /// <param name="baseVersion">The published version the edits are against, or 0 on an empty database.</param>
    /// <param name="openedBy">The identity that opened it, at most 128 characters.</param>
    /// <param name="openedAtUtc">When it was opened.</param>
    /// <param name="note">The operator's note, at most 1,024 characters, empty when none.</param>
    /// <param name="changes">The ordered, deduplicated edit list.</param>
    /// <param name="frozenForBaseVersion">The base version a publish in flight froze this draft for, or null when it is not frozen.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseVersion"/> is negative.</exception>
    public ContentDraft(
        int baseVersion,
        string openedBy,
        DateTimeOffset openedAtUtc,
        string note,
        ContentChangeSet changes,
        int? frozenForBaseVersion = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);
        ArgumentNullException.ThrowIfNull(openedBy);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(changes);

        if (frozenForBaseVersion is int frozen)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(frozen, nameof(frozenForBaseVersion));
        }

        BaseVersion = baseVersion;
        OpenedBy = openedBy;
        OpenedAtUtc = openedAtUtc;
        Note = note;
        Changes = changes;
        FrozenForBaseVersion = frozenForBaseVersion;
    }

    /// <summary>The published version these edits are against. 0 means the database has published none.</summary>
    public int BaseVersion { get; }

    /// <summary>The identity that opened the draft, which is the operator the console forwarded.</summary>
    public string OpenedBy { get; }

    /// <summary>When the draft was opened.</summary>
    public DateTimeOffset OpenedAtUtc { get; }

    /// <summary>The operator's note, empty when none was given.</summary>
    public string Note { get; }

    /// <summary>The pending edits, one per target, in the order they were applied.</summary>
    public ContentChangeSet Changes { get; }

    /// <summary>How many edits are pending, which is what a console's pending-changes panel shows.</summary>
    public int EditCount => Changes.Count;

    /// <summary>
    /// The base version a publish in flight froze this draft for, or null when nothing has it frozen. A
    /// marker naming anything other than the version the store currently stands at belongs to a publish that
    /// died, and the next baseline read clears it.
    /// </summary>
    public int? FrozenForBaseVersion { get; }

    /// <summary>True while a publish holds this draft, which is when every write to it is refused.</summary>
    public bool IsFrozen => FrozenForBaseVersion is not null;
}
