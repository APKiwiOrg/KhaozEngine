using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Where one committed identity stands in a baseline catalog.</summary>
public enum ContentUpgradeIdentityState
{
    /// <summary>Neither the id nor the key is occupied, so the row is the upgrade's to add.</summary>
    Absent,

    /// <summary>The same id and the same key name ONE row, so the catalog already carries the identity.</summary>
    Present,

    /// <summary>Anything else: an id under another key, a key under another id, or only one of the two.</summary>
    Conflict,
}

/// <summary>
/// The generic checks an upgrade planner runs before it emits a single edit, lifted out of the one explicit
/// catalog upgrade command that proved them and made parameterless of any game's types or keys.
/// <para>
/// <b>Every check ANSWERS rather than throws.</b> A refusal is a string a plan carries into
/// <see cref="ContentUpgradePlan.Refused"/>, because the runner's job is to report the catalog's state to an
/// operator and an exception thrown out of a planner is a stack trace rather than an instruction.
/// </para>
/// <para>
/// <b>Identity is the unit, and never value.</b> A row present under the committed id and key is present
/// whatever its fields hold, because those fields may be operator tuning that an upgrade has no business
/// reverting.
/// </para>
/// </summary>
public static class ContentUpgradeChecks
{
    /// <summary>
    /// Every type the committed target bundle declares is registered by this build and declared IDENTICALLY.
    /// A target built by a different build of the game would otherwise add rows under a schema the running
    /// process cannot encode, and the failure would surface as an unreadable chunk at the next boot.
    /// </summary>
    /// <param name="target">The committed target bundle.</param>
    /// <param name="registry">This build's registry.</param>
    /// <returns>The refusal, or null when every type agrees.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? TargetMatchesRegistry(ContentBundle target, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(registry);

        for (int i = 0; i < target.Types.Count; i++)
        {
            ContentBundleType type = target.Types[i];
            if (!registry.TryGet(type.Type, out ContentTypeRegistration? registered))
            {
                return FormattableString.Invariant(
                    $"The committed bundle declares type {type.Type.Value} '{type.TypeKey}', which this build does not register.");
            }

            if (!Matches(type, registered))
            {
                return FormattableString.Invariant(
                    $"The committed bundle declares type {type.Type.Value} '{type.TypeKey}' differently from this build's registry.");
            }
        }

        return null;
    }

    /// <summary>
    /// Every type the BASELINE carries is present in the target under the same declaration, so the rows
    /// already in the catalog can be carried forward as they stand. A schema that moved underneath live rows
    /// is refused rather than migrated, because nothing here can know what the new field should hold.
    /// </summary>
    /// <param name="baseline">The catalog as it stands.</param>
    /// <param name="target">The committed target bundle.</param>
    /// <returns>The refusal naming the type, or null when every baseline type is compatible.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? BaselineIsSchemaCompatible(ContentBundle baseline, ContentBundle target)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(target);

        for (int i = 0; i < baseline.Types.Count; i++)
        {
            ContentBundleType existing = baseline.Types[i];
            ContentBundleType? wanted = TypeOf(target, existing.Type);
            if (wanted is null || !Matches(existing, wanted))
            {
                return FormattableString.Invariant(
                    $"Baseline type {existing.Type.Value} '{existing.TypeKey}' is not schema-compatible with the committed target, so its existing rows cannot be carried forward.");
            }
        }

        return null;
    }

    /// <summary>
    /// Where one committed row's identity stands in the baseline. It is <see cref="ContentUpgradeIdentityState.Present"/>
    /// only when the id and the key name the SAME baseline row, which is what makes a re-run a no-op and a
    /// half-applied upgrade a refusal instead of a silent overwrite.
    /// </summary>
    /// <param name="baseline">The catalog as it stands.</param>
    /// <param name="targetRow">The committed row, which carries its stable id.</param>
    /// <param name="conflict">What is wrong, on <see cref="ContentUpgradeIdentityState.Conflict"/> only.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ContentUpgradeIdentityState Identity(
        ContentBundle baseline,
        ContentBundleRow targetRow,
        out string? conflict)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(targetRow);

        conflict = null;
        if (targetRow.Id is not int id)
        {
            conflict = FormattableString.Invariant(
                $"Committed row type {targetRow.Type.Value} '{targetRow.Key}' does not carry its stable id, so nothing can say whether the catalog already holds it.");
            return ContentUpgradeIdentityState.Conflict;
        }

        ContentBundleRow? byId = FindById(baseline, targetRow.Type, id);
        ContentBundleRow? byKey = FindByKey(baseline, targetRow.Type, targetRow.Key);
        if (byId is null && byKey is null)
        {
            return ContentUpgradeIdentityState.Absent;
        }

        if (byId is not null && byKey is not null && ReferenceEquals(byId, byKey))
        {
            return ContentUpgradeIdentityState.Present;
        }

        conflict = FormattableString.Invariant(
            $"The committed identity type {targetRow.Type.Value} id {id} key '{targetRow.Key}' conflicts with the catalog: id {id} holds '{Describe(byId?.Key)}' and key '{targetRow.Key}' holds id {Describe(byKey?.Id)}. Nothing was changed.");
        return ContentUpgradeIdentityState.Conflict;
    }

    /// <summary>
    /// Every committed identity the plan would add is FREE: the catalog holds neither the id nor the key,
    /// and the id is within the type's declared ceiling.
    /// <para>
    /// <b>It asks whether the id can be WRITTEN, not what an allocator would issue.</b> An upgrade emits its
    /// adds carrying the committed ids, so nothing here has to predict a counter. Predicting one was wrong as
    /// well as unnecessary: a publish refused after step 3 burns the ids it reserved, reserve before issue
    /// working as designed, so a long-lived catalog's marks sit above its highest row id after a single
    /// failed operator publish and a prediction from the rows would pass while the publish issued higher
    /// numbers.
    /// </para>
    /// <para>
    /// A type with id FAMILIES is still refused outright. A family's members come from its aligned blocks and
    /// a planner that added one from the committed bundle alone would have to reproduce the block layout as
    /// well as the id, which nothing here is given.
    /// </para>
    /// </summary>
    /// <param name="baseline">The catalog as it stands.</param>
    /// <param name="additions">The committed rows the plan would add, each carrying its stable id.</param>
    /// <param name="registry">This build's registry, which declares each type's id ceiling.</param>
    /// <returns>The refusal naming the identity, or null when every one of them is free.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? IdentityIsFree(
        ContentBundle baseline,
        IReadOnlyList<ContentBundleRow> additions,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(registry);

        var checkedTypes = new HashSet<ushort>();
        for (int i = 0; i < additions.Count; i++)
        {
            ContentBundleRow row = additions[i];
            string? refusal = IsFree(baseline, row, registry);
            if (refusal is not null)
            {
                return refusal;
            }

            if (checkedTypes.Add(row.Type.Value) && HasFamilies(baseline, row.Type))
            {
                return FormattableString.Invariant(
                    $"Type {TypeName(registry, row.Type)} has id families, and a family's members come from its aligned blocks, so an upgrade will not add a row to it.");
            }
        }

        return null;
    }

    /// <summary>One live row of a bundle by its id, or null when the bundle carries none.</summary>
    /// <param name="bundle">The bundle to search.</param>
    /// <param name="type">The content type.</param>
    /// <param name="id">The definition id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
    public static ContentBundleRow? FindById(ContentBundle bundle, ContentTypeId type, int id)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            if (row.Type.Value == type.Value && row.Id == id)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>One live row of a bundle by its key, ordinally, or null when the bundle carries none.</summary>
    /// <param name="bundle">The bundle to search.</param>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
    public static ContentBundleRow? FindByKey(ContentBundle bundle, ContentTypeId type, ContentKey key)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            if (row.Type.Value == type.Value && row.Key.Equals(key))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The type's key as the registry names it, or its number when nothing registers it.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="type">The content type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static string TypeName(ContentTypeRegistry registry, ContentTypeId type)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.TryGet(type, out ContentTypeRegistration? registration)
            ? registration.TypeKey
            : type.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One committed row's id and key against the catalog and against the type's ceiling. The id half and
    /// the key half are asked separately, because a catalog that holds one and not the other is a half
    /// applied upgrade and the refusal has to say which half it found.
    /// </summary>
    static string? IsFree(ContentBundle baseline, ContentBundleRow row, ContentTypeRegistry registry)
    {
        string type = TypeName(registry, row.Type);
        if (row.Id is not int id)
        {
            return FormattableString.Invariant(
                $"Committed row {type} '{row.Key}' does not carry its stable id, so an upgrade cannot add it under the id the build names it by.");
        }

        if (FindById(baseline, row.Type, id) is ContentBundleRow held)
        {
            return FormattableString.Invariant(
                $"The catalog already holds {type} id {id} as '{held.Key}', so the committed row '{row.Key}' cannot be added under it. An id is never reused.");
        }

        if (FindByKey(baseline, row.Type, row.Key) is ContentBundleRow byKey)
        {
            return FormattableString.Invariant(
                $"The catalog already holds {type} '{row.Key}' as id {Describe(byKey.Id)}, and the committed bundle names it {id}, so this upgrade will not add it.");
        }

        if (registry.TryGet(row.Type, out ContentTypeRegistration? registration)
            && registration.MaxDefinitionId is int ceiling
            && id > ceiling)
        {
            return FormattableString.Invariant(
                $"Committed row {type} '{row.Key}' carries id {id}, over the ceiling of {ceiling} that type declares, so it cannot be written at all.");
        }

        return null;
    }

    static bool HasFamilies(ContentBundle baseline, ContentTypeId type)
    {
        for (int i = 0; i < baseline.Families.Count; i++)
        {
            if (baseline.Families[i].Type.Value == type.Value)
            {
                return true;
            }
        }

        return false;
    }

    static ContentBundleType? TypeOf(ContentBundle bundle, ContentTypeId type)
    {
        for (int i = 0; i < bundle.Types.Count; i++)
        {
            if (bundle.Types[i].Type.Value == type.Value)
            {
                return bundle.Types[i];
            }
        }

        return null;
    }

    static bool Matches(ContentBundleType left, ContentTypeRegistration right)
        => string.Equals(left.TypeKey, right.TypeKey, StringComparison.Ordinal)
            && left.DefaultVisibility == right.DefaultVisibility
            && left.ChunkSlots == right.ChunkSlots
            && left.MaxDefinitionId == right.MaxDefinitionId
            && SchemaMatches(left.Schema, right.Schema);

    static bool Matches(ContentBundleType left, ContentBundleType right)
        => string.Equals(left.TypeKey, right.TypeKey, StringComparison.Ordinal)
            && left.DefaultVisibility == right.DefaultVisibility
            && left.ChunkSlots == right.ChunkSlots
            && left.MaxDefinitionId == right.MaxDefinitionId
            && SchemaMatches(left.Schema, right.Schema);

    /// <summary>
    /// Two schemas agree field for field, IN ORDER. Order is part of the comparison because a row body is a
    /// positional walk, so two schemas holding the same fields in a different order encode different bytes.
    /// </summary>
    static bool SchemaMatches(ContentFieldSchema left, ContentFieldSchema right)
    {
        if (left.Fields.Count != right.Fields.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Fields.Count; i++)
        {
            if (left.Fields[i] != right.Fields[i])
            {
                return false;
            }
        }

        return true;
    }

    static string Describe(ContentKey? key) => key is ContentKey held ? held.ToString() : "nothing";

    static string Describe(int? id) => id is int held
        ? held.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "nothing";
}
