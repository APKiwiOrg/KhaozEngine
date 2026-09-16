using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Remap.RemapFixtures;

namespace KhaozEngine.Tests.ItemInstances.Remap;

/// <summary>
/// What each of contracts 8.2's four rule kinds does to an entry, at the entry's OWN definition and at a
/// nested socket's contained definition, plus the facts that pin the pass to the registry rather than to a
/// list of kinds.
/// <para>
/// The agreement fact is the one that keeps the pass and the validator in step: the validator's content
/// reads ARE its reference walk, so recording them over the same page before and after a rewrite says both
/// which ids it visits and what the pass made of them.
/// </para>
/// <para>
/// Nothing in this class writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class InstanceRemapKindTests
{
    [Fact]
    public void ReplacedBy_moves_the_entry_definition_AND_a_socketed_definition()
    {
        // Contracts 8.2 kind 1, the ordinary rename or re-base. Counts, positions and payloads carry over
        // unchanged, which is the half of the kind that is easy to lose in the re-encode.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, Socketed(Gem), count: 1);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(1, outcome.EntriesTouched);
        Assert.Equal(2, outcome.IdsRewritten);
        Assert.Equal(103, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(Socketed(103), PayloadAt(page, 0));

        // The contained INSTANCE id is not a content id and never moves: unsocketing restores that identity.
        Assert.Equal(Instance, page.SlotAt(0).Stack.InstanceId);
        Assert.True(page.IsDirty);
        Assert.Equal(3, page.ContentVersion);
    }

    [Fact]
    public void Retired_under_the_PLACEHOLDER_policy_moves_no_id_and_leaves_the_page_clean()
    {
        // Contracts 8.2 kind 2 policy 0x01: the reference is kept AS IS and the item is displayed through a
        // placeholder. The pass resolves the rule and has nothing to write, so the page is not dirtied and
        // the stamp does not move. The presentation is validator check 13's and the caller's, not the pass's.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId item = Type(types, ItemKey);
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, Socketed(Gem));
        byte[] original = PayloadAt(page, 0);

        RemapRuleSet rules = Rules(RetiredPlaceholder(1, 3, item, Gem));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        // The rule RESOLVED: it answered true with the id unchanged, which is a caller's cue and not a
        // rewrite.
        Assert.True(rules.TryResolve(item, Gem, 0, out int toId, out RemapRuleKind kind, out _));
        Assert.Equal(Gem, toId);
        Assert.Equal(RemapRuleKind.Retired, kind);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.IdsRewritten);
        Assert.Equal(Gem, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(original, PayloadAt(page, 0));
        Assert.False(page.IsDirty);
        Assert.Equal(0, page.ContentVersion);
    }

    [Fact]
    public void Retired_under_the_REPLACEMENT_policy_moves_both_ids_the_way_a_ReplacedBy_does()
    {
        // Contracts 8.2 kind 2 policy 0x02: bytes 1 to 4 are an int32 destination and the behaviour is kind
        // 1's. The destination is IN the payload rather than in ToId, which is the trap.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, Socketed(Gem));

        RemapRuleSet rules = Rules(RetiredTo(1, 3, Type(types, ItemKey), Gem, 104));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(1, outcome.EntriesTouched);
        Assert.Equal(2, outcome.IdsRewritten);
        Assert.Equal(104, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(Socketed(104), PayloadAt(page, 0));
        Assert.Equal(3, page.ContentVersion);
    }

    [Fact]
    public void MovedToLegacy_moves_both_ids_onto_the_legacy_copy()
    {
        // Contracts 8.2 kind 3: every existing item moves to the legacy copy, which can never be generated
        // again. To the pass it is kind 1 with a different reason, and the two coexist forever.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, Socketed(Gem));

        RemapRuleSet rules = Rules(MovedToLegacy(1, 3, Type(types, ItemKey), Gem, 900_001));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(1, outcome.EntriesTouched);
        Assert.Equal(2, outcome.IdsRewritten);
        Assert.Equal(900_001, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(Socketed(900_001), PayloadAt(page, 0));
    }

    [Fact]
    public void StackCapLowered_resolves_and_changes_no_byte_of_the_page()
    {
        // Contracts 8.2 kind 4: ToId is 0, the payload is the new cap and the id does not move. An existing
        // over-cap stack is LEGAL, so there is nothing here to rewrite and nothing to refuse. The cap itself
        // is unimplemented (https://github.com/APKiwiOrg/KhaozEngine/issues/924) and the pass resolves the
        // rule anyway, because the resolution is what a caller enforcing the cap will read.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId item = Type(types, ItemKey);
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, count: 40, instanceId: 0);
        Seat(page, 1, Gem, Socketed(Gem));
        byte[] original = PayloadAt(page, 1);

        RemapRuleSet rules = Rules(StackCapLowered(1, 3, item, Gem, 20));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.True(rules.TryResolve(item, Gem, 0, out int toId, out RemapRuleKind kind, out ReadOnlySpan<byte> payload));
        Assert.Equal(Gem, toId);
        Assert.Equal(RemapRuleKind.StackCapLowered, kind);
        Assert.Equal(4, payload.Length);
        Assert.True(rules.Rules[0].TryGetStackCap(out int newCap));
        Assert.Equal(20, newCap);

        Assert.False(outcome.Changed);
        Assert.Equal(40, page.SlotAt(0).Stack.Count);
        Assert.Equal(original, PayloadAt(page, 1));
        Assert.False(page.IsDirty);
        Assert.Equal(0, page.ContentVersion);
    }

    [Fact]
    public void Every_id_the_VALIDATOR_resolves_is_an_id_the_pass_rewrites_in_the_same_order()
    {
        // Spec 12.2: checks 6 and 7 are derived from the SAME InstanceReferenceTarget descriptors the pass
        // walks, in the same recursive order, over the same nested payloads. The validator's content reads
        // are that walk made observable, so a sweep before and after a rewrite pins both halves at once.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Deep());

        RemapRuleSet rules = Rules(
            Replaced(1, 4, Type(types, ItemKey), Sword, 1_100),
            Replaced(2, 4, Type(types, ItemKey), Gem, 1_102),
            Replaced(3, 4, Type(types, ModKey), Mod, 1_500),
            Replaced(4, 4, Type(types, ModKey), SecondMod, 1_501),
            Replaced(5, 4, Type(types, SocketTypeKey), SocketType, 1_700),
            Replaced(6, 4, Type(types, UniqueTemplateKey), UniqueTemplate, 1_900),
            Replaced(7, 4, Type(types, RarityKey), RarityRule, 1_800),
            Replaced(8, 4, Type(types, RareNameWordKey), RareNameWord, 1_950));

        IReadOnlyList<(ContentTypeId Type, int Id)> before = Reads(page, properties, types);
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);
        IReadOnlyList<(ContentTypeId Type, int Id)> after = Reads(page, properties, types);

        Assert.NotEmpty(before);
        Assert.Equal(before.Count, after.Count);
        for (int index = 0; index < before.Count; index++)
        {
            Assert.True(rules.TryResolve(before[index].Type, before[index].Id, 0, out int expected, out _, out _));
            Assert.Equal((before[index].Type, expected), after[index]);
        }

        // The validator reads the entry's definition TWICE, once for check 6 and once for check 12's cap
        // lookup, and the pass visits it once, which is the whole of the difference between the two counts.
        Assert.Equal(before.Count - 1, outcome.IdsRewritten);
    }

    [Fact]
    public void A_GAME_kind_at_or_above_1024_is_remapped_by_declaring_its_shape_alone()
    {
        // Spec 3.3: a game kind that carries a content id gets remap, drift detection and quarantine by
        // declaring a shape and its reference targets and nothing else. A hard-coded switch over the kinds
        // this release knows would give it none of the three, silently.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = WithGameKind();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, new ItemInstancePayloadBuilder().Add(GameKind, GameBody(Mod, 5)).ToArray());

        RemapRuleSet rules = Rules(Replaced(1, 2, Type(types, ModKey), Mod, 600));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(1, outcome.IdsRewritten);
        Assert.Equal(
            new ItemInstancePayloadBuilder().Add(GameKind, GameBody(600, 5)).ToArray(),
            PayloadAt(page, 0));
    }

    [Fact]
    public void An_UNREGISTERED_kind_is_kept_verbatim_through_a_rewrite()
    {
        // Contracts 9.4: a kind this build does not know is kept byte for byte, so a client built against
        // content build N reads, remaps and re-saves an item carrying a field only build N plus one knows.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        byte[] opaque = { 0x81, 0x02, 0x00, 0xFF, 0x7F };
        ItemContainerPage page = Page();
        Seat(
            page,
            0,
            Sword,
            new ItemInstancePayloadBuilder()
                .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(Mod, Tier, 17) })
                .Add(UnknownKind, opaque)
                .ToArray());

        RemapRuleSet rules = Rules(Replaced(1, 2, Type(types, ModKey), Mod, 600));
        _ = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(
            new ItemInstancePayloadBuilder()
                .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(600, Tier, 17) })
                .Add(UnknownKind, opaque)
                .ToArray(),
            PayloadAt(page, 0));
        Assert.Equal(opaque, Body(properties, PayloadAt(page, 0), UnknownKind));
    }

    [Fact]
    public void A_QUARANTINED_entry_is_left_verbatim_because_its_bytes_are_preserved_rather_than_read()
    {
        // Contracts 10.2 and spec 12.4: a quarantine wrapper preserves the ORIGINAL bytes, nothing
        // truncated, normalized or re-encoded, and that is what makes the first load after a missing rule
        // lands restore the item exactly. So the pass leaves the whole entry alone, its definition id
        // included, rather than half moving a record it cannot read.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        byte[] wrapper = QuarantineWrapper.Wrap(InstanceQuarantineReason.UnknownContentReference, 2, Socketed(Gem));
        ItemContainerPage page = Page();
        Seat(page, 0, Gem, wrapper, quarantined: true);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(Gem, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(wrapper, PayloadAt(page, 0));
        Assert.True(page.SlotAt(0).Quarantined);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void A_payload_that_does_not_DECODE_leaves_the_whole_entry_alone()
    {
        // A count of one affix with no affix behind it: structurally canonical, so the container door seats
        // it, and malformed against kind 131's registered shape, so nothing can be read out of it. The
        // definition id is left with it: moving that alone would strand a payload whose stale ids no rule
        // would ever visit again, because the page stamp moves past the rule that named them.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        byte[] malformed = new ItemInstancePayloadBuilder()
            .Add(InstancePropertyKind.Affixes, new byte[] { 1 })
            .ToArray();
        Assert.Null(ItemInstancePayload.Validate(malformed));
        Assert.NotNull(ItemInstancePayload.Validate(properties, malformed));

        ItemContainerPage page = Page();
        Seat(page, 0, Gem, malformed);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(Gem, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(malformed, PayloadAt(page, 0));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void A_rewrite_that_would_COLLIDE_two_mod_ids_is_abandoned_rather_than_written()
    {
        // The one shape the publish validator cannot rule out, because it cannot see stored payloads: a rule
        // whose destination is a mod the same item already carries. Contracts 9.9 allows a mod id at most
        // once, so the rewritten list would not decode. The pass writes nothing rather than writing bytes
        // the decoder refuses, which keeps the original recoverable when the content is fixed.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Affixed(new InstanceAffix(Mod, Tier, 17), new InstanceAffix(SecondMod, 2, 4_000)));
        byte[] original = PayloadAt(page, 0);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ModKey), Mod, SecondMod));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.IdsRewritten);
        Assert.Equal(original, PayloadAt(page, 0));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void A_replacement_that_does_not_fit_a_BYTE_slot_is_abandoned_and_one_that_fits_is_written()
    {
        // Kind 130's rarity id is a BYTE and is still a content reference, so the slot kind and the target
        // are independent. A rule moving it above 255 names an id this shape cannot hold, and there is no
        // wider form of the field to widen into.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId rarity = Type(types, RarityKey);
        byte[] payload = new ItemInstancePayloadBuilder().AddByte(InstancePropertyKind.Rarity, 7).ToArray();

        ItemContainerPage fits = Page();
        Seat(fits, 0, Sword, payload);
        _ = InstanceRemapPass.Apply(fits, Rules(Replaced(1, 3, rarity, 7, 9)), 0, properties, types);
        Assert.Equal(
            new ItemInstancePayloadBuilder().AddByte(InstancePropertyKind.Rarity, 9).ToArray(),
            PayloadAt(fits, 0));

        ItemContainerPage overflows = Page();
        Seat(overflows, 0, Sword, payload);
        InstanceRemapOutcome outcome =
            InstanceRemapPass.Apply(overflows, Rules(Replaced(1, 3, rarity, 7, 300)), 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(payload, PayloadAt(overflows, 0));
        Assert.False(overflows.IsDirty);
    }

    [Fact]
    public void Kind_7_material_ids_are_rewritten_because_the_registry_calls_them_item_references()
    {
        // An engine-band kind, in the 1 to 127 range, whose ids an earlier draft of spec 12.2 omitted from
        // its closed enumeration. Nothing about the walk knows what a material IS.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddMaterials(new[] { new InstanceMaterial(Gem, 2), new InstanceMaterial(Sword, 1) })
            .ToArray();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, payload);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        // The entry's own definition is a Sword and stays one, and the material order is AUTHORED and is
        // never sorted, unlike the affix list.
        Assert.Equal(1, outcome.IdsRewritten);
        Assert.Equal(Sword, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(
            new ItemInstancePayloadBuilder()
                .AddMaterials(new[] { new InstanceMaterial(103, 2), new InstanceMaterial(Sword, 1) })
                .ToArray(),
            PayloadAt(page, 0));
    }

    static ContentTypeId Type(ContentTypeRegistry types, string typeKey) => RemapFixtures.Type(types, typeKey);

    /// <summary>One game kind entry list: a count byte and one entry of two varints.</summary>
    static byte[] GameBody(int modId, int second)
    {
        Span<byte> body = stackalloc byte[16];
        body[0] = 1;
        int written = 1;
        written += ContentVarint.Write(body[written..], (uint)modId);
        written += ContentVarint.Write(body[written..], (uint)second);
        return body[..written].ToArray();
    }

    /// <summary>
    /// Every content row the VALIDATOR reads while sweeping the page, in order. The sweep must find nothing,
    /// because a quarantine would stop the walk at the first finding and the order is the whole question.
    /// </summary>
    static IReadOnlyList<(ContentTypeId Type, int Id)> Reads(
        ItemContainerPage page, InstancePropertyRegistry properties, ContentTypeRegistry types)
    {
        var snapshot = new RecordingSnapshot(types, Mod, SecondMod, 1_500, 1_501);
        byte[] bytes = Bytes(page);
        var entries = new PageEntry[ItemContainerPageCodec.ContainerPageSlots];
        Assert.True(
            ItemContainerPageCodec.TryDecode(
                bytes,
                ItemContainerPageCodec.ContainerPageSlots,
                entries,
                out PageHeader header,
                out int count,
                out string? reason),
            reason);

        InstanceValidationReport report = InstanceValidator.Validate(
            bytes, header, entries.AsSpan(0, count), properties, types, snapshot);
        Assert.Empty(report.Findings);
        return snapshot.Reads;
    }
}
