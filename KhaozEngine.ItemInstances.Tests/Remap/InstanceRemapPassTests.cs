using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Remap.RemapFixtures;

namespace KhaozEngine.Tests.ItemInstances.Remap;

/// <summary>
/// Spec 5.5 step 2 over contracts 8.1 through 8.6: the registry-derived remap pass, its idempotence, and
/// the depth and width facts an implementer gets wrong.
/// <para>
/// The idempotence fact is spec 17 row 8 and it comes FIRST here for the same reason it comes first in the
/// plan: it is what makes a crash between apply and commit safe, and therefore what makes the lazy rewrite
/// safe at all.
/// </para>
/// <para>
/// Nothing in this class writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class InstanceRemapPassTests
{
    [Fact]
    public void Applying_the_full_ordered_rule_set_TWICE_produces_the_same_bytes_as_ONCE()
    {
        // Spec 17 row 8 and contracts 8.3. The retry after a crash runs the same rules over ids the
        // interrupted pass already rewrote, at the SAME page stamp, because the stamp is what did not
        // commit.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        RemapRuleSet rules = Rules(
            Replaced(1, 8, Type(types, ItemKey), Sword, 4_200),
            Replaced(2, 8, Type(types, ModKey), Mod, 4_210),
            MovedToLegacy(3, 9, Type(types, ModKey), SecondMod, 600),
            Replaced(4, 9, Type(types, ItemKey), Gem, 103));

        ItemContainerPage once = ThreeEntries();
        InstanceRemapOutcome first = InstanceRemapPass.Apply(once, rules, 0, properties, types);
        byte[] afterOnce = Bytes(once);

        Assert.True(first.Changed);
        Assert.Equal(3, first.EntriesTouched);
        Assert.True(once.IsDirty);
        Assert.Equal(9, once.ContentVersion);

        InstanceRemapOutcome second = InstanceRemapPass.Apply(once, rules, 0, properties, types);

        Assert.False(second.Changed);
        Assert.Equal(0, second.EntriesTouched);
        Assert.Equal(0, second.IdsRewritten);
        Assert.Equal(0, second.BytesDelta);
        Assert.Equal(afterOnce, Bytes(once));

        // And the same statement the other way round: a page that met the set twice holds what a page that
        // met it once holds, byte for byte.
        ItemContainerPage twice = ThreeEntries();
        _ = InstanceRemapPass.Apply(twice, rules, 0, properties, types);
        _ = InstanceRemapPass.Apply(twice, rules, 0, properties, types);
        Assert.Equal(afterOnce, Bytes(twice));
    }

    [Fact]
    public void A_gem_socketed_into_a_sword_is_rewritten_by_the_same_rule_that_rewrites_it_in_a_bag()
    {
        // Spec 5.5 step 2: nothing is skipped for being nested. This is the property that stops an item
        // surviving three publishes invisibly and then quarantining on the day a player unsockets it.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Socketed(Gem));
        Seat(page, 1, Gem, count: 5, instanceId: 0);

        RemapRuleSet rules = Rules(Replaced(1, 4, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.Equal(2, outcome.EntriesTouched);
        Assert.Equal(2, outcome.IdsRewritten);
        Assert.Equal(Socketed(103), PayloadAt(page, 0));
        Assert.Equal(103, page.SlotAt(1).Stack.ItemId);
        Assert.Equal(5, page.SlotAt(1).Stack.Count);

        // The gem in the bag is the same rule's work, so the socketed one is not a special case anywhere.
        Assert.Equal(Sword, page.SlotAt(0).Stack.ItemId);
    }

    [Fact]
    public void A_ReplacedBy_that_WIDENS_a_varint_recomputes_the_two_lengths_above_it()
    {
        // The whole reason the pass RE-ENCODES rather than patching bytes in place. The nested payload is
        // padded to exactly 127 bytes, so the socket's NestedLength varint widens from one byte to two the
        // moment the mod id inside it grows by one, and the kind 132 length above it moves by two.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        const int narrowMod = 91;
        const int wideMod = 4_210;

        int pad = 127 - NestedAffix(narrowMod, 0).Length;
        byte[] before = NestedAffix(narrowMod, pad);
        Assert.Equal(127, before.Length);

        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Socketed(Gem, before));
        byte[] original = PayloadAt(page, 0);

        RemapRuleSet rules = Rules(Replaced(1, 3, Type(types, ModKey), narrowMod, wideMod));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        byte[] rewritten = PayloadAt(page, 0);
        Assert.Equal(Socketed(Gem, NestedAffix(wideMod, pad)), rewritten);
        Assert.Equal(1, outcome.IdsRewritten);
        Assert.Equal(2, outcome.BytesDelta);

        // Innermost first: the nested payload's own bytes, then the socket entry's NestedLength, then kind
        // 132's field Length. The entry's PayloadLength is the page codec's and moves with the payload.
        Assert.Equal(127, NestedLength(Body(properties, original, InstancePropertyKind.Sockets)));
        Assert.Equal(128, NestedLength(Body(properties, rewritten, InstancePropertyKind.Sockets)));
        Assert.Equal(
            Field(properties, original, InstancePropertyKind.Sockets).BodyLength + 2,
            Field(properties, rewritten, InstancePropertyKind.Sockets).BodyLength);
        Assert.Equal(original.Length + 2, rewritten.Length);
    }

    [Fact]
    public void A_rewrite_restores_canonical_affix_order_when_a_replacement_moves_a_mod_id()
    {
        // Contracts 9.9 holds an affix list ascending by mod id, and spec 4.6's stacking rule is a byte
        // compare over that order. A re-sort that did not happen would leave a page whose items no longer
        // stack with their own twins.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        var low = new InstanceAffix(Mod, Tier, 17);
        var high = new InstanceAffix(SecondMod, 2, 4_000);

        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Affixed(low, high));

        // 500 becomes 600, which is past 501, so the list is no longer ascending where it stands.
        RemapRuleSet rules = Rules(Replaced(1, 2, Type(types, ModKey), Mod, 600));
        _ = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        var moved = new InstanceAffix(600, Tier, 17);
        Assert.Equal(Affixed(high, moved), PayloadAt(page, 0));

        // The tier and the roll position travel WITH the mod id rather than staying at their old index,
        // which is the part a re-sort gets wrong.
        Assert.Null(ItemInstancePayload.Validate(properties, PayloadAt(page, 0)));
    }

    [Fact]
    public void A_rule_matching_nothing_is_a_SCAN_and_writes_zero_bytes()
    {
        // Almost every rule on almost every page. The page stays clean, the stamp stays where it was, and
        // the bytes are the bytes the store already holds.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Deep());
        byte[] original = PayloadAt(page, 0);

        RemapRuleSet rules = Rules(
            Replaced(1, 5, Type(types, ItemKey), 7_777, 7_778),
            Replaced(2, 5, Type(types, ModKey), 8_888, 8_889));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.EntriesTouched);
        Assert.Equal(0, outcome.IdsRewritten);
        Assert.Equal(0, outcome.BytesDelta);
        Assert.False(page.IsDirty);
        Assert.Equal(0, page.ContentVersion);
        Assert.Equal(original, PayloadAt(page, 0));
    }

    [Fact]
    public void A_rule_whose_IntroducedIn_is_at_or_below_the_page_stamp_does_not_apply()
    {
        // Contracts 8.3: a rule applies to a page whose stamp is STRICTLY older than IntroducedIn. Equal is
        // a page that already met it.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page(stamp: 5);
        Seat(page, 0, Sword, Affixed(new InstanceAffix(Mod, Tier, 17)));

        RemapRuleSet rules = Rules(
            Replaced(1, 5, Type(types, ModKey), Mod, 600),
            Replaced(2, 6, Type(types, ItemKey), Sword, 110));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 5, properties, types);

        Assert.Equal(1, outcome.IdsRewritten);
        Assert.Equal(110, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(Affixed(new InstanceAffix(Mod, Tier, 17)), PayloadAt(page, 0));
        Assert.Equal(6, page.ContentVersion);
    }

    [Fact]
    public void Rules_apply_in_Sequence_order_in_ONE_pass()
    {
        // Two rules naming the SAME source, which is the only shape contracts 8.3 leaves in which the order
        // is observable: a chain is forbidden outright, so a fixed-point loop would have nothing to chase.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId mod = Type(types, ModKey);

        ItemContainerPage first = Page();
        Seat(first, 0, Sword, Affixed(new InstanceAffix(Mod, Tier, 17)));
        _ = InstanceRemapPass.Apply(
            first,
            Rules(Replaced(1, 2, mod, Mod, 600), Replaced(2, 2, mod, Mod, 700)),
            0,
            properties,
            Types());
        Assert.Equal(Affixed(new InstanceAffix(600, Tier, 17)), PayloadAt(first, 0));

        ItemContainerPage second = Page();
        Seat(second, 0, Sword, Affixed(new InstanceAffix(Mod, Tier, 17)));
        _ = InstanceRemapPass.Apply(
            second,
            Rules(Replaced(1, 2, mod, Mod, 700), Replaced(2, 2, mod, Mod, 600)),
            0,
            properties,
            types);
        Assert.Equal(Affixed(new InstanceAffix(700, Tier, 17)), PayloadAt(second, 0));
    }

    [Fact]
    public void A_page_stamped_NEWER_than_the_active_version_is_not_an_error_and_is_not_lowered()
    {
        // Spec 5.5 and section 13 row 2: that page is not quarantined, its stamp is never rewound, and it is
        // already correct when the newer version returns.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page(stamp: 9);
        Seat(page, 0, Sword, Deep());
        byte[] original = PayloadAt(page, 0);

        RemapRuleSet rules = Rules(
            Replaced(1, 4, Type(types, ItemKey), Sword, 110),
            Replaced(2, 5, Type(types, ModKey), Mod, 600));
        Assert.Equal(5, rules.ActiveStamp);

        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 9, properties, types);

        Assert.False(outcome.Changed);
        Assert.False(page.IsDirty);
        Assert.Equal(9, page.ContentVersion);
        Assert.Equal(Sword, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(original, PayloadAt(page, 0));
    }

    [Fact]
    public void A_rule_set_whose_destination_is_an_earlier_rules_source_is_REFUSED_before_the_pass_runs()
    {
        // Contracts 8.3's publish-side guarantee, leaned on rather than re-derived: no rule's ToId is any
        // earlier rule's FromId for the same type, so ONE pass is enough. A set that breaks it would chain
        // on a second pass, so the pass refuses it rather than hiding a publish validator bug behind a
        // fixed-point loop.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId item = Type(types, ItemKey);
        RemapRuleSet chained = Rules(
            Replaced(1, 2, item, Sword, 200),
            Replaced(2, 2, item, 300, Sword));

        Assert.False(chained.IsIdempotent(out RemapRule? offending));
        Assert.Equal(2, offending!.Sequence);

        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Deep());
        byte[] original = PayloadAt(page, 0);

        Assert.Throws<ArgumentException>(() => InstanceRemapPass.Apply(page, chained, 0, properties, types));

        // Refused BEFORE the pass runs means the page is untouched, not half rewritten.
        Assert.False(page.IsDirty);
        Assert.Equal(0, page.ContentVersion);
        Assert.Equal(Sword, page.SlotAt(0).Stack.ItemId);
        Assert.Equal(original, PayloadAt(page, 0));

        // The degenerate case of the same walk: a rule whose destination is its own source.
        RemapRuleSet itself = Rules(Replaced(1, 2, item, Sword, Sword));
        Assert.False(itself.IsIdempotent(out _));
        Assert.Throws<ArgumentException>(() => InstanceRemapPass.Apply(page, itself, 0, properties, types));
    }

    [Fact]
    public void The_destination_span_holds_the_entries_as_the_pass_left_them()
    {
        // The pass never writes the page to storage: the rewrite is lazy and rides the next ordinary commit
        // (spec 5.5 step 3, contracts 10.3). What it hands the caller is the entries as they now stand, so
        // the commit builder needs no second walk to find them.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Socketed(Gem));
        Seat(page, 4, Gem, count: 3, instanceId: 0);

        var entries = new PageSlotInput[page.EntryCount];
        RemapRuleSet rules = Rules(Replaced(1, 4, Type(types, ItemKey), Gem, 103));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(page, rules, 0, properties, types, entries);

        Assert.Equal(2, outcome.EntriesTouched);
        Assert.Equal(0, entries[0].Slot);
        Assert.Equal(Sword, entries[0].DefinitionId);
        Assert.Equal(Socketed(103), entries[0].Payload.ToArray());
        Assert.Equal(4, entries[1].Slot);
        Assert.Equal(103, entries[1].DefinitionId);
        Assert.True(page.IsDirty);
    }

    [Fact]
    public void An_entry_the_pass_ABANDONS_is_COUNTED_and_its_slot_is_named()
    {
        // https://github.com/APKiwiOrg/KhaozEngine/issues/931. An abandoned entry used to contribute zero to
        // every field of the outcome, so it was indistinguishable from an entry no rule named, and it then
        // read downstream as a missing rule rather than as a rule that could not be applied.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Affixed(new InstanceAffix(Mod, Tier, 17)));
        Seat(page, 6, Sword, Affixed(new InstanceAffix(Mod, Tier, 17), new InstanceAffix(SecondMod, 2, 4_000)));

        var entries = new PageSlotInput[page.EntryCount];
        Span<int> abandoned = stackalloc int[page.EntryCount];
        VettedRemapRules rules = VettedRemapRules.Vet(Rules(Replaced(1, 3, Type(types, ModKey), Mod, SecondMod)));
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(
            page, rules, 0, properties, types, entries, abandoned);

        // Slot 0 moves: it carries the source and not the destination. Slot 6 carries both, so the rewritten
        // list would hold one mod twice and the pass writes nothing for it.
        Assert.Equal(1, outcome.EntriesTouched);
        Assert.Equal(1, outcome.EntriesAbandoned);
        Assert.Equal(6, abandoned[0]);
        Assert.Equal(Affixed(new InstanceAffix(SecondMod, Tier, 17)), PayloadAt(page, 0));
    }

    [Fact]
    public void A_page_no_rule_reaches_abandons_NOTHING()
    {
        // The count means "a rule could not be applied", so an entry no rule named must not reach it, and
        // neither must a payload that was already broken before any rule ran.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ItemContainerPage page = ThreeEntries();

        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(
            page, Rules(Replaced(1, 3, Type(types, ModKey), 4_242, SecondMod)), 0, properties, types);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.EntriesAbandoned);
    }

    [Fact]
    public void A_VETTED_set_is_walked_for_idempotence_once_rather_than_once_per_page()
    {
        // https://github.com/APKiwiOrg/KhaozEngine/issues/928. The check is about n^2 / 2 comparisons and the
        // set cannot change between a container's pages, so the load path vets once and hands the vetted
        // handle to every page. The TYPE is the assertion: there is no flag for a caller to get wrong, and
        // the refusal a broken set earns is the same from either door.
        ContentTypeRegistry types = Types();
        InstancePropertyRegistry properties = Properties();
        ContentTypeId item = Type(types, ItemKey);
        RemapRuleSet chained = Rules(Replaced(1, 2, item, Sword, 200), Replaced(2, 2, item, 300, Sword));

        Assert.Throws<ArgumentException>(() => VettedRemapRules.Vet(chained));

        VettedRemapRules vetted = VettedRemapRules.Vet(Rules(Replaced(1, 4, item, Gem, 103)));
        Assert.Equal(4, vetted.ActiveStamp);
        Assert.Single(vetted.Rules.Rules);

        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Socketed(Gem));
        Assert.True(InstanceRemapPass.Apply(page, vetted, 0, properties, types).Changed);
        Assert.Equal(Socketed(103), PayloadAt(page, 0));
    }

    static ContentTypeId Type(ContentTypeRegistry types, string typeKey) => RemapFixtures.Type(types, typeKey);

    /// <summary>A nested payload carrying one affix and a pad field of a chosen width, so a test can put the
    /// nested length exactly on a varint boundary.</summary>
    static byte[] NestedAffix(int modId, int pad)
        => new ItemInstancePayloadBuilder()
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(modId, 1, 300) })
            .Add(UnknownKind, new byte[pad])
            .ToArray();

    /// <summary>A page holding a socketed sword, an affixed sword and a plain stack of gems.</summary>
    static ItemContainerPage ThreeEntries()
    {
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Deep());
        Seat(page, 3, Sword, Affixed(new InstanceAffix(Mod, Tier, 17), new InstanceAffix(SecondMod, 2, 4_000)));
        Seat(page, 7, Gem, count: 9, instanceId: 0);
        return page;
    }
}
