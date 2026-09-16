using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One rolled item, spec 9.4's record plus the two members its own prose requires. It is the RESOLVED
/// outcome and it carries no seed, no state and no draw index, which is contracts 14.3 and is also the only
/// shape that survives the journal's replay model: a replay returns the original receipt rather than
/// re-running anything, so a result carrying a seed would have to be re-rolled to mean anything.
/// <para>
/// <b><see cref="AffixCount"/> and <see cref="RequestedAffixCount"/> are not an embellishment.</b> Spec 9.4
/// step 8 says an item whose pool empties "ends with fewer affixes than the count asked for, which is a
/// legal outcome and is REPORTED IN THE RESULT rather than retried", and the record spec 9.4 declares has
/// nowhere to report it. A caller that cannot tell a three affix roll from a six affix roll that ran dry
/// cannot log the difference, and running dry is the signal that a pack's pool is thinner than its rarity
/// rules assume.
/// </para>
/// </summary>
/// <param name="BaseId">The <c>item</c> row the item was made from.</param>
/// <param name="InstanceId">The durable id, or 0 when the payload is empty and the item is a plain
/// stack (spec 3.6).</param>
/// <param name="Payload">The canonical payload, encoded through <c>ItemInstancePayloadBuilder</c>.</param>
/// <param name="RarityId">The <c>rarity_rule</c> row seated, or 0 for an item with no rarity at all.</param>
/// <param name="ContentVersion">The version the roll read, contracts 7.1, which is what makes "what did
/// this item look like when it dropped" answerable against the right catalog rather than today's.</param>
/// <param name="AffixCount">How many affixes were PLACED, which is what the payload carries.</param>
/// <param name="RequestedAffixCount">How many the rarity rule asked for. Below <paramref name="AffixCount"/>
/// it never goes, and above it the pool ran dry.</param>
public readonly record struct GenerationResult(
    int BaseId,
    long InstanceId,
    ReadOnlyMemory<byte> Payload,
    int RarityId,
    int ContentVersion,
    int AffixCount,
    int RequestedAffixCount);
