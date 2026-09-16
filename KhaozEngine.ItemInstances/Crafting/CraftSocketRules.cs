using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The rules a NESTED payload has to answer, seated at the working copy's socket write rather than in the
/// one primitive that happens to call it.
/// <para>
/// <b>A game operation builds its own socket list.</b> Spec 10.5 gives it the working copy and the same
/// refusal vocabulary, so a rule living in <c>CraftPrimitives.Socket</c> alone is a rule an operation walks
/// straight past: contracts 9.5's one level limit, the socket type's own <c>max_nested_bytes</c> and
/// standing rule 2 over the nested affix lists were all reachable only through that one caller. They are
/// door rules here, which is what spec 10.5 claims for all four of the powers already.
/// </para>
/// <para>
/// <b>Only the bytes a write BRINGS are judged.</b> A socket whose nested payload is byte identical to the
/// one already stored at that index is skipped, because it was judged when it was written and re-judging it
/// against content that has moved since would make a socket type whose budget shrank turn every later write
/// on that item into a refusal, including the unsocket that would have fixed it.
/// </para>
/// <para>
/// <b>The decode is the REGISTRY bound one.</b> The structural
/// <see cref="ItemInstancePayload.IsCanonical(ReadOnlyMemory{byte})"/> treats every kind as unknown and
/// never reads a body, so a nested payload whose own fields are malformed passes it. The registry decode
/// reads each body against its registered shape, and the field list it answers is what names the offending
/// kind in the refusal.
/// </para>
/// </summary>
static class CraftSocketRules
{
    /// <summary>
    /// Every incoming socket's nested payload against the three rules, in the order
    /// <c>CraftPrimitives.Socket</c> fixed: the type's budget, then the decode and the one level limit, then
    /// the frozen entries inside.
    /// </summary>
    /// <param name="copy">The craft in progress, which is what records the refusal.</param>
    /// <param name="sockets">The socket list the write wants to seat.</param>
    /// <returns>The refusal, or null when every nested payload is allowed.</returns>
    internal static CraftRefusal? CheckNested(ref CraftWorkingCopy copy, scoped ReadOnlySpan<InstanceSocket> sockets)
    {
        int held = copy.SocketCount;
        InstanceSocket[] current = held == 0 ? [] : new InstanceSocket[held];
        _ = copy.ReadSockets(current);

        for (int index = 0; index < sockets.Length; index++)
        {
            ReadOnlySpan<byte> incoming = sockets[index].Nested.Span;
            if (incoming.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<byte> before = index < held ? current[index].Nested.Span : default;
            if (ItemInstancePayload.SequenceEqual(incoming, before))
            {
                continue;
            }

            if (Fits(ref copy, sockets[index].SocketTypeId, incoming) is CraftRefusal oversized)
            {
                return oversized;
            }

            if (Reads(ref copy, incoming) is CraftRefusal unreadable)
            {
                return unreadable;
            }

            if (!before.IsEmpty && Frozen(ref copy, incoming, before) is CraftRefusal frozen)
            {
                return frozen;
            }
        }

        return null;
    }

    /// <summary>
    /// The socket type's own nested budget. A <c>max_nested_bytes</c> of 0 MEANS the whole payload budget
    /// and never no nesting at all, and a socket type id of 0 is the one and only no restriction, which is
    /// what turns spec 3.5's per socket arithmetic into a publish time fact and a refusal at the moment of
    /// socketing rather than a throw at the moment of encoding.
    /// </summary>
    static CraftRefusal? Fits(ref CraftWorkingCopy copy, int socketTypeId, scoped ReadOnlySpan<byte> nested)
    {
        int budget = ItemInstancePayload.MaxInstancePayloadBytes;
        if (socketTypeId > 0
            && copy.Snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.SocketTypeTypeId), socketTypeId, out ContentRow? type))
        {
            long authored = InstanceContentChecks.Number(type, SocketTypeContentType.MaxNestedBytesIndex) ?? 0;
            if (authored != SocketTypeContentType.FullPayloadBudget)
            {
                budget = (int)Math.Clamp(authored, 0, ItemInstancePayload.MaxInstancePayloadBytes);
            }
        }

        return nested.Length <= budget
            ? null
            : copy.Refuse(new CraftRefusal(CraftRefusalKind.NestedPayloadTooLong, budget));
    }

    /// <summary>
    /// The registry bound decode, plus the one level limit of contracts 9.5 asked of the payload about to go
    /// IN. The limit is derived from the registered SHAPE rather than from kind 132, so a game kind that
    /// nests gets the same answer, and it is structural rather than a convention because recursion is the
    /// one way a 45 byte payload becomes a denial of service.
    /// </summary>
    static CraftRefusal? Reads(ref CraftWorkingCopy copy, scoped ReadOnlySpan<byte> nested)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(copy.Registry, nested, fields, out int count, out _))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.PayloadMalformed, 0));
        }

        for (int index = 0; index < count; index++)
        {
            if (copy.Registry.TryGet(fields[index].Kind, out InstancePropertyRegistration? registration)
                && registration.Shape.Nests)
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.NestedPayloadNests, fields[index].Kind));
            }
        }

        return null;
    }

    /// <summary>
    /// Standing rules 2 and 3 over the nested payload's own kind 131 and kind 133 lists, compared against
    /// the lists the slot already held.
    /// </summary>
    static CraftRefusal? Frozen(
        ref CraftWorkingCopy copy,
        scoped ReadOnlySpan<byte> incoming,
        scoped ReadOnlySpan<byte> before)
        => Compare(ref copy, incoming, before, InstancePropertyKind.Affixes)
            ?? Compare(ref copy, incoming, before, InstancePropertyKind.Enchantments);

    /// <summary>
    /// One of the two entry lists, read off both sides and handed to the standing rule. Both sides go
    /// through the SHIPPED decoder, which is a working copy opened over those bytes, so there is no second
    /// reader of the entry layout to keep in step with the first.
    /// </summary>
    static CraftRefusal? Compare(
        ref CraftWorkingCopy copy,
        scoped ReadOnlySpan<byte> incoming,
        scoped ReadOnlySpan<byte> before,
        ushort kind)
    {
        CraftWorkingCopy after = CraftWorkingCopy.Open(copy.Registry, copy.Snapshot, copy.DefinitionId, incoming);
        CraftWorkingCopy stored = CraftWorkingCopy.Open(copy.Registry, copy.Snapshot, copy.DefinitionId, before);
        Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        Span<InstanceAffix> held = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int entryCount = after.ReadAffixes(kind, entries);
        int heldCount = stored.ReadAffixes(kind, held);
        return CraftStandingRules.CheckAffixWrite(ref copy, entries[..entryCount], held[..heldCount]);
    }
}
