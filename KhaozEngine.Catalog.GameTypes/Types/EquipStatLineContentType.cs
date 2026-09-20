using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>equip_stat_line</c> content type: one stat value of one equip profile, in the order a panel
/// draws them.
/// </summary>
/// <remarks>
/// A LINE rather than a column per stat, so a world adding a stat authors a row instead of changing a
/// schema every reader then has to agree about again. Which stats exist is the game's: this type names one
/// by key reference to the engine's own <c>stat</c> type and says nothing about what it does.
/// <para>
/// <see cref="ValueField"/> is a plain <see cref="ContentFieldKind.Int"/> and not a scaled one. The stat row
/// owns the scale, so a scale here would be a second copy of it and the two would disagree.
/// </para>
/// <para>
/// It is a child type with a key reference to its parent rather than a repeated field on
/// <c>equip_profile</c>, by the rule that a repeating child structure is its own content type and never a
/// blob field. A blob would cost the generic editor, the field-level audit and the field-level diff.
/// </para>
/// </remarks>
public static class EquipStatLineContentType
{
    /// <summary>
    /// Id slots per chunk. Lines outnumber profiles by roughly the stats a piece of equipment touches, so
    /// this type takes four times the floor its parent takes.
    /// </summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The profile this line belongs to.</summary>
    public const string ProfileField = "profile";

    /// <summary>The stat this line moves.</summary>
    public const string StatField = "stat";

    /// <summary>The amount, in the stat's own scaled units.</summary>
    public const string ValueField = "value";

    /// <summary>The authored order, which is the order the equipment panel draws the lines in.</summary>
    public const string SortField = "sort";

    internal const int ProfileIndex = 0;
    internal const int StatIndex = 1;
    internal const int ValueIndex = 2;
    internal const int SortIndex = 3;
    internal const int FieldCount = 4;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ProfileField,
            ContentFieldKind.KeyReference,
            GameContentTypeIds.EquipProfileKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            StatField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.StatTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(ValueField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// Registers the type on <paramref name="registry"/>, in the game band, under the id and key
    /// <see cref="GameContentTypeIds"/> declares for it and this type's own visibility and chunk slots.
    /// </summary>
    /// <remarks>
    /// The visibility and the chunk slot count are the TYPE's rather than a caller's: a wrong visibility
    /// moves rows between the two manifests and a wrong slot count moves every content address, and neither
    /// fails loudly.
    /// </remarks>
    /// <param name="registry">A registry that is not frozen and carries neither this id nor this key.</param>
    /// <param name="validator">
    /// The type's own validator, or null for none. <see cref="Validator"/> is the one this package ships
    /// for it, and a game passes that, one of its own, a wrapper over both, or null.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or already carries this id or this key.
    /// </exception>
    public static void Register(ContentTypeRegistry registry, IContentValidator? validator)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentFieldSchema schema = CreateSchema();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            GameContentTypeIds.EquipStatLine,
            GameContentTypeIds.EquipStatLineKey,
            new Codec(new ContentTypeId(GameContentTypeIds.EquipStatLine), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The stat line row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the stat line schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The two rules the schema alone cannot say: one line per profile and stat, and a draw order that does
    /// not tie within a profile.
    /// </summary>
    /// <remarks>
    /// It takes NO options: which stats exist is the <c>stat</c> type's and neither rule reads a number this
    /// package gives meaning to. It runs STANDALONE and it COMPOSES, because the engine registry holds one
    /// validator per type and a cross-type sweep wraps this one rather than replacing it. It ACCUMULATES, so
    /// one pass reports every defect instead of the earliest.
    /// <para>
    /// A duplicate pair would make the equipped bonus depend on row order, and a tied sort would make the
    /// panel's line order depend on it. Both are silent, which is why they are refused at publish.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var firstByPair = new Dictionary<(int Profile, int Stat), int>();
            var firstBySort = new Dictionary<(int Profile, int Sort), int>();
            foreach (ContentRow row in candidate.Rows(type))
            {
                if (row.IsRetired)
                {
                    continue;
                }

                int profile = (int)GameRowNumbers.At(row, ProfileIndex);
                if (profile == 0)
                {
                    // No profile is no content, which the engine's own required-field check owns. Two lines
                    // belonging to nothing are not two lines of one profile.
                    continue;
                }

                int stat = (int)GameRowNumbers.At(row, StatIndex);
                if (stat != 0 && !firstByPair.TryAdd((profile, stat), row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.EquipStatLineDuplicatePair,
                        FormattableString.Invariant(
                            $"Stat line '{row.Key}' moves stat {stat} on profile {profile}, which line {firstByPair[(profile, stat)]} already moves. Two lines for one pair make the bonus depend on row order.")));
                }

                int sort = (int)GameRowNumbers.At(row, SortIndex);
                if (!firstBySort.TryAdd((profile, sort), row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.EquipStatLineDuplicateSort,
                        FormattableString.Invariant(
                            $"Stat line '{row.Key}' sorts at {sort} within profile {profile}, which line {firstBySort[(profile, sort)]} already takes. A tie makes the drawn order depend on row order.")));
                }
            }
        }
    }
}
