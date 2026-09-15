using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// What one pass changed, which is what a load path reports and what a dirty set is derived from.
/// </summary>
/// <param name="EntriesTouched">Entries whose bytes the pass replaced. Zero is the common answer, because a
/// rule that matches nothing is a scan rather than a rewrite.</param>
/// <param name="IdsRewritten">Content ids that MOVED, counted once per place they sit: an id that appears in
/// a material list and again inside a socket counts twice. A rule that resolves without moving an id, which
/// is a retire under the placeholder policy and a lowered stack cap, adds nothing here.</param>
/// <param name="BytesDelta">How much the page's payload bytes grew, which is negative when a replacement id
/// is narrower than the one it replaced.</param>
public readonly record struct InstanceRemapOutcome(int EntriesTouched, int IdsRewritten, int BytesDelta)
{
    /// <summary>Whether the pass changed anything at all, which is what marks the page dirty.</summary>
    public bool Changed => EntriesTouched > 0;
}

/// <summary>
/// Spec 5.5 step 2 over contracts 8.3: every rule whose <see cref="RemapRule.IntroducedIn"/> is strictly
/// greater than the page stamp, in <see cref="RemapRule.Sequence"/> order, in ONE pass.
/// <para>
/// <b>The walk is DERIVED from the property registry</b> rather than from a list of kinds. It visits the
/// entry's own definition id, every id a registered <see cref="InstanceReferenceTarget"/> names inside every
/// field, and, through a nesting slot, every id inside a socket's nested payload along with that socket's own
/// contained definition. Nothing is skipped for being nested, which is the property that stops an item
/// surviving three publishes invisibly and then quarantining on the day a player unsockets it. A game kind at
/// or above 1024 is remapped by declaring its shape and its targets and nothing else.
/// </para>
/// <para>
/// <b>It walks the same descriptors, in the same recursive order, as
/// <see cref="InstanceValidator"/>'s checks 6 and 7</b> (spec 12.2), so a kind cannot be
/// remapped-but-not-validated or validated-but-not-remapped. The two are kept in step by construction (both
/// read <see cref="InstancePropertyRegistration.Shape"/> and <see cref="InstancePropertyRegistration.References"/>,
/// both resolve a run's targets AFTER recursing into a nested payload the run carries) and by a test that
/// records the validator's content reads over one page before and after a rewrite.
/// </para>
/// <para>
/// <b>It RE-ENCODES rather than patching bytes in place</b>, because a replacement id can change a varint's
/// WIDTH: a nested payload carrying mod 91 is one byte shorter than the same payload carrying mod 4210. A hit
/// recomputes, innermost first, the nested payload's bytes, then the socket entry's nested length, then the
/// field's length, then the entry's payload length in the page. It also restores canonical order on a list
/// that declares one, because a replacement can move an affix's mod id past its neighbour and a list that
/// lost its order no longer stacks with its own twins (spec 4.6).
/// </para>
/// <para>
/// <b>ONE pass, no fixed-point loop.</b> Contracts 8.3 forbids a rule whose destination is an earlier rule's
/// source for the same type, so one pass is enough. A loop "just in case" would hide a publish validator bug
/// rather than surface it, so a set that breaks the rule is REFUSED here instead.
/// </para>
/// <para>
/// <b>It never writes the page to storage.</b> The rewrite is lazy and rides the next ordinary commit (spec
/// 5.5 step 3, contracts 10.3), because eagerly rewriting at boot is a write storm proportional to the whole
/// player base arriving exactly when the server is coldest. The cost is that a remapped page can be lost on a
/// crash, which means it is remapped again on the next load, and that is safe because the set is idempotent.
/// </para>
/// </summary>
public static partial class InstanceRemapPass
{
    /// <summary>
    /// The room one level of the rewrite writes into. A rewritten payload that does not fit is one the cap of
    /// contracts 9.6 would refuse anyway, so the bound and the cap answer the same question and this one is
    /// reached first.
    /// </summary>
    const int ScratchBytes = ItemInstancePayload.MaxInstancePayloadBytes + 16;

    /// <summary>Slot values held per level without touching the heap, the same bound the validator's walk
    /// uses. A registration declaring more slots than this gets a per-field array rather than a bigger stack
    /// frame.</summary>
    const int SlotBuffer = 16;

    /// <summary>The most entries a list that is re-sorted may hold. An affix count is a byte, so this is the
    /// ceiling plus one and a wider list is left alone rather than half sorted.</summary>
    const int MaxSortedEntries = 256;

    /// <summary>What a rewrite answers when it cannot produce bytes the decoder would accept.</summary>
    const int Abandon = -1;

    /// <summary>
    /// Applies the rule set to one page, allocating the entry span itself. The overload taking a span is what
    /// a load path with many pages uses, because it reuses one buffer across all of them.
    /// </summary>
    /// <param name="page">The page, already seated from its stored bytes.</param>
    /// <param name="rules">The full ordered rule set the active pack carries.</param>
    /// <param name="pageStamp">The stamp the page was stored under, which is normally
    /// <see cref="ItemContainerPage.ContentVersion"/>.</param>
    /// <param name="properties">The property kinds this build knows.</param>
    /// <param name="types">The content types the active pack registers, which is what turns a reference
    /// target's type KEY into the type id a rule carries.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageStamp"/> is negative.</exception>
    /// <exception cref="ArgumentException">The rule set is not idempotent.</exception>
    public static InstanceRemapOutcome Apply(
        ItemContainerPage page,
        RemapRuleSet rules,
        int pageStamp,
        InstancePropertyRegistry properties,
        ContentTypeRegistry types)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Apply(page, rules, pageStamp, properties, types, new PageSlotInput[page.EntryCount]);
    }

    /// <summary>
    /// Applies the rule set to one page and answers what changed.
    /// <para>
    /// A rule that changes nothing is a SCAN: the page is not dirtied, its stamp does not move, and no byte of
    /// it is replaced. A rule that DOES change something dirties the page and moves its in-memory stamp to the
    /// version the set brings it to, and never lowers it, which is spec 5.5's policy for a page stamped newer
    /// than the active version.
    /// </para>
    /// <para>
    /// An entry the pass cannot rewrite is left WHOLE, its definition id included. That covers a quarantined
    /// entry, whose wrapper preserves bytes verbatim rather than offering them to be read (spec 12.4), a
    /// payload that does not decode, and a rewrite whose result the decoder would refuse, which is what a rule
    /// naming an id the same item already carries produces. Moving the definition id alone would strand a
    /// payload whose stale ids no rule will ever visit again, because the stamp moves past the rule that named
    /// them. An abandoned entry is not counted anywhere yet
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/931">#931</see>), and bringing a
    /// QUARANTINED one back is the load path's unwrap step rather than the pass's
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/929">#929</see>).
    /// </para>
    /// </summary>
    /// <param name="page">The page, already seated from its stored bytes.</param>
    /// <param name="rules">The full ordered rule set the active pack carries.</param>
    /// <param name="pageStamp">The stamp the page was stored under.</param>
    /// <param name="properties">The property kinds this build knows.</param>
    /// <param name="types">The content types the active pack registers.</param>
    /// <param name="destination">At least <see cref="ItemContainerPage.EntryCount"/> long. It receives the
    /// page's entries AS THE PASS LEFT THEM, ready for the codec, so a commit needs no second walk.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageStamp"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short, or the rule set is
    /// not idempotent: its destination is an earlier rule's source for the same type, so applying it twice
    /// would not answer what applying it once answered. That is a fact about the PACK rather than about a
    /// stored byte, the publish validator is required to refuse it (contracts 8.3), and a pass that worked
    /// around it would hide the gap.</exception>
    public static InstanceRemapOutcome Apply(
        ItemContainerPage page,
        RemapRuleSet rules,
        int pageStamp,
        InstancePropertyRegistry properties,
        ContentTypeRegistry types,
        Span<PageSlotInput> destination)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentOutOfRangeException.ThrowIfNegative(pageStamp);

        int count = page.CopyEntriesTo(destination);

        // The cheap answers first, and the common one is the second: a page already at the active version
        // meets no applicable rule, because a rule applies to a stamp STRICTLY older than its own.
        if (count == 0 || pageStamp >= rules.ActiveStamp) return default;

        if (!rules.IsIdempotent(out RemapRule? offending))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Remap rule {offending!.Sequence} sends content type {offending.Type.Value} id {offending.FromId} to an id that is an earlier rule's source, so applying the set twice would not answer what applying it once answered (contracts 8.3). The publish validator refuses this set and the pass will not guess around it."),
                nameof(rules));
        }

        var resolver = new RemapResolver(rules, types, pageStamp);

        // Every buffer the walk needs, once per page rather than once per entry, so no stackalloc sits inside
        // a loop and a scan allocates nothing at all.
        Span<byte> payload = stackalloc byte[ScratchBytes];
        Span<byte> body = stackalloc byte[ScratchBytes];
        Span<byte> arena = stackalloc byte[ScratchBytes];
        Span<byte> nestedBody = stackalloc byte[ScratchBytes];
        Span<byte> sortedEntries = stackalloc byte[ScratchBytes];
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Span<PayloadField> nestedFields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Span<ulong> values = stackalloc ulong[SlotBuffer * 2];
        Span<int> nestedStarts = stackalloc int[SlotBuffer * 2];
        Span<int> starts = stackalloc int[MaxSortedEntries];
        Span<int> lengths = stackalloc int[MaxSortedEntries];
        Span<long> keys = stackalloc long[MaxSortedEntries];

        var walk = new RemapWalk(
            properties,
            resolver,
            new RemapLevel(body, fields, values[..SlotBuffer], arena, nestedStarts[..SlotBuffer]),
            new RemapLevel(nestedBody, nestedFields, values[SlotBuffer..], default, nestedStarts[SlotBuffer..]),
            sortedEntries,
            starts,
            lengths,
            keys);

        return Rewrite(page, rules, properties, resolver, walk, payload, destination, count);
    }

    static InstanceRemapOutcome Rewrite(
        ItemContainerPage page,
        RemapRuleSet rules,
        InstancePropertyRegistry properties,
        RemapResolver resolver,
        in RemapWalk walk,
        Span<byte> payload,
        Span<PageSlotInput> entries,
        int count)
    {
        int touched = 0;
        int rewritten = 0;
        int delta = 0;

        for (int index = 0; index < count; index++)
        {
            PageSlotInput entry = entries[index];

            // A quarantined entry carries a WRAPPER rather than a payload, and contracts 10.2 keeps those
            // bytes verbatim so the first load after the missing rule lands restores the item exactly. What
            // no step unwraps one is https://github.com/APKiwiOrg/KhaozEngine/issues/929.
            if (entry.Quarantined) continue;

            int before = resolver.Rewritten;
            int definitionId = resolver.Resolve(EngineContentTypes.ItemTypeKey, entry.DefinitionId);
            int written = 0;
            if (!entry.Payload.IsEmpty)
            {
                written = walk.RewritePayload(entry.Payload.Span, payload, level: 0);
                if (written < 0)
                {
                    resolver.Rewind(before);
                    continue;
                }
            }

            int moved = resolver.Rewritten - before;
            if (moved == 0) continue;

            ReadOnlySpan<byte> bytes = payload[..written];

            // Never publish bytes the decoder would refuse. A rule whose destination is an id the same item
            // already carries produces a duplicate the canonical form forbids, and the publish validator
            // cannot see it coming, because it cannot see stored payloads.
            if (!entry.Payload.IsEmpty && ItemInstancePayload.Validate(properties, bytes) is not null)
            {
                resolver.Rewind(before);
                continue;
            }

            var slot = new ItemSlot(
                new ItemStack(definitionId, entry.Count, entry.InstanceId),
                entry.Payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : bytes.ToArray(),
                Quarantined: false);

            // The page owns the dirty flag and the stamp, and it moves neither when the slot it ends up
            // holding is the slot it already held.
            if (!page.ApplyRemap(entry.Slot, slot, rules.ActiveStamp))
            {
                resolver.Rewind(before);
                continue;
            }

            touched++;
            rewritten += moved;
            delta += written - entry.Payload.Length;
        }

        if (touched > 0) _ = page.CopyEntriesTo(entries);
        return new InstanceRemapOutcome(touched, rewritten, delta);
    }

    /// <summary>
    /// One id at a time, which is the whole of what the rule set decides. It holds the type key lookup, so a
    /// page's worth of references costs one dictionary probe per TYPE rather than one per id.
    /// <para>
    /// A type key the active pack does not register resolves to nothing and the id is left alone: a rule
    /// carries a type ID, so no rule can name a type that was never registered. The validator is what notices
    /// that reference, and it fails closed on it.
    /// </para>
    /// </summary>
    sealed class RemapResolver
    {
        readonly RemapRuleSet _rules;
        readonly ContentTypeRegistry _types;
        readonly int _pageStamp;
        readonly Dictionary<string, ContentTypeId> _byKey = new(StringComparer.Ordinal);

        public RemapResolver(RemapRuleSet rules, ContentTypeRegistry types, int pageStamp)
        {
            _rules = rules;
            _types = types;
            _pageStamp = pageStamp;
        }

        /// <summary>How many ids have MOVED so far, which the entry loop reads as a change flag.</summary>
        public int Rewritten { get; private set; }

        /// <summary>Puts the count back, which an abandoned entry owes: its ids were resolved and then not
        /// written, so counting them would report a rewrite that did not happen.</summary>
        /// <param name="count">The count before the entry.</param>
        public void Rewind(int count) => Rewritten = count;

        /// <summary>The id the page should now carry, which is the id it was handed when no rule moves it.</summary>
        /// <param name="typeKey">The content type key the reference target names.</param>
        /// <param name="fromId">The id as the page holds it.</param>
        public int Resolve(string typeKey, int fromId)
        {
            if (fromId <= 0 || !Type(typeKey, out ContentTypeId type)) return fromId;

            // A kind that moves no id, which is a retire under the placeholder policy and a lowered stack cap,
            // answers TRUE with the id unchanged. That is a cue for a caller rather than a rewrite, so nothing
            // here counts it and nothing here writes for it.
            if (!_rules.TryResolve(type, fromId, _pageStamp, out int toId, out _, out _) || toId == fromId)
            {
                return fromId;
            }

            Rewritten++;
            return toId;
        }

        bool Type(string typeKey, out ContentTypeId type)
        {
            if (_byKey.TryGetValue(typeKey, out type)) return type.Value != 0;

            _ = _types.TryGetByKey(typeKey, out ContentTypeRegistration? registration);
            type = registration?.Type ?? default;
            _byKey.Add(typeKey, type);
            return type.Value != 0;
        }
    }
}
