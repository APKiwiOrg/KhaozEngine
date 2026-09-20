using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>skill_curve</c> content type: ONE ROW PER SKILL, carrying that skill's own knobs.
/// </summary>
/// <remarks>
/// A row per SKILL rather than a column per skill on one row, because the alternative grows the schema every
/// time a skill gains a knob and leaves every other skill carrying an absent field for it. A knob that is not
/// any one skill's belongs on <c>game_tuning</c>.
/// <para>
/// A skill with no knob worth tuning authors a row carrying nothing but its own number, or no row at all,
/// which are the same thing to a reader. That is why <see cref="XpPerDamageField"/> is the ONE optional field
/// across all thirteen types: it is a knob most skills have no use for, and an absent value has to be
/// distinguishable from an authored zero.
/// </para>
/// <para>
/// <see cref="SkillField"/> carries the game's own durable skill number rather than a name. A committed
/// progression record carries the raw number, so it is the one spelling of a skill that cannot drift.
/// </para>
/// </remarks>
public static class SkillCurveContentType
{
    /// <summary>Id slots per chunk, the engine's floor. One row per skill is tens of rows.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The skill this row's knobs belong to, as the game's own durable number.</summary>
    public const string SkillField = "skill";

    /// <summary>Experience paid per point of damage dealt. OPTIONAL: most skills carry no such rate.</summary>
    public const string XpPerDamageField = "xp_per_damage";

    internal const int SkillIndex = 0;
    internal const int XpPerDamageIndex = 1;
    internal const int FieldCount = 2;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(SkillField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(XpPerDamageField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
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
            GameContentTypeIds.SkillCurve,
            GameContentTypeIds.SkillCurveKey,
            new Codec(new ContentTypeId(GameContentTypeIds.SkillCurve), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The curve row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the skill curve schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The three rules the schema alone cannot say: one row per skill, a skill the game knows, and a knob
    /// that pays something when it is set at all.
    /// </summary>
    /// <remarks>
    /// The ONE thing it needs from a game is whether a skill number means anything, which arrives as a
    /// predicate rather than a roster: a skill is a raw durable number here, and which numbers exist is not
    /// this package's to hold. Everything else is arithmetic.
    /// <para>
    /// It ACCUMULATES, so one pass over a whole roster reports every defect rather than the earliest.
    /// </para>
    /// <para>
    /// An UNSET knob is legal and is the ordinary case, which is why the rule is about the value and not
    /// about presence: the absence convention writes the same zero form for an unset field and an authored
    /// zero, and the decoder resolves an optional zero as absent, so a row that reaches here carrying a
    /// value carried an authored one.
    /// </para>
    /// <para>
    /// <b>A message names the raw NUMBER.</b> A name for it would be the game's vocabulary, and the number
    /// is what the row actually carries and what an author edits.
    /// </para>
    /// </remarks>
    /// <param name="isKnownSkill">
    /// Whether the game stands for this skill number at all. False is
    /// <see cref="GameContentFindings.SkillCurveUnknownSkill"/>. A game whose skill numbers are a
    /// byte-backed enum checks the range BEFORE it casts, because a cast from a long is unchecked and would
    /// fold 256 onto the first constant rather than refusing it.
    /// </param>
    public sealed class Validator(Func<long, bool> isKnownSkill) : IContentValidator
    {
        readonly Func<long, bool> _isKnownSkill = isKnownSkill
            ?? throw new ArgumentNullException(nameof(isKnownSkill));

        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var firstBySkill = new Dictionary<long, int>();
            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                ContentFieldValue rate = row.Fields[XpPerDamageIndex];
                if (!rate.IsAbsent && rate.Number <= 0)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.SkillCurveXpPerDamageNotPositive,
                        FormattableString.Invariant(
                            $"Curve '{row.Key}' pays {rate.Number} experience per point of damage. A rate of nothing is what leaving the knob unset already means, so authoring one says the skill trains and then refuses to.")));
                }

                ContentFieldValue skill = row.Fields[SkillIndex];
                if (skill.IsAbsent)
                {
                    // A required field left empty is the engine's KEC0005, and a row that names no skill
                    // cannot duplicate one.
                    continue;
                }

                if (!_isKnownSkill(skill.Number))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.SkillCurveUnknownSkill,
                        FormattableString.Invariant(
                            $"Curve '{row.Key}' carries skill {skill.Number}, which nothing in the game stands for. The number is the game's own durable skill value, so a row may only carry one the roster already knows.")));
                    continue;
                }

                if (!firstBySkill.TryAdd(skill.Number, row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.SkillCurveDuplicateSkill,
                        FormattableString.Invariant(
                            $"Curve '{row.Key}' carries the knobs of skill {skill.Number}, which curve {firstBySkill[skill.Number]} already carries. One skill reads one row, so two leave every knob on it ambiguous.")));
                }
            }
        }
    }
}
