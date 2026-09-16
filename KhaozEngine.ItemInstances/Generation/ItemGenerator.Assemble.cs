using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Steps 9 to 13 of spec 9.4: sort the affix list, roll the rare name, seat the sockets, assemble the
/// payload and take an instance id. The assembly goes through <see cref="ItemInstancePayloadBuilder"/> and
/// never patches bytes, which is what makes canonical form free and what stops a second encoder existing.
/// </summary>
public sealed partial class ItemGenerator
{
    /// <summary>
    /// Step 9: the affix list ascending by mod id, which is what makes kind 131 canonical and therefore
    /// what makes spec 4.6's byte comparison the stacking rule. An insertion sort, because the list is at
    /// most a handful of entries and a comparison delegate would allocate on a path that must not.
    /// <para>
    /// The builder sorts too, so the field is canonical whichever way it is handed in. That is deliberate
    /// belt and braces rather than a duplicate: the builder is the CANONICAL FORM's guarantee and this is
    /// the generator holding its own scratch in the order it writes it.
    /// </para>
    /// </summary>
    void SortAffixes(int count)
    {
        for (int outer = 1; outer < count; outer++)
        {
            InstanceAffix moving = _affixes[outer];
            int inner = outer - 1;
            while (inner >= 0 && _affixes[inner].ModId > moving.ModId)
            {
                _affixes[inner + 1] = _affixes[inner];
                inner--;
            }

            _affixes[inner + 1] = moving;
        }
    }

    /// <summary>
    /// Step 10: one draw per name POSITION over that position's words, weighted against the base's tags
    /// through their <c>rare_name_word_weight</c> rows. A position whose pool is empty still draws and
    /// discards, for the same reason an empty affix pool does.
    /// </summary>
    int RollRareName(int signature, int rarityIndex)
    {
        if (rarityIndex < 0)
        {
            return 0;
        }

        int positions = Math.Min(_content.NamePositionsAt(rarityIndex), _nameWords.Length);
        int placed = 0;
        for (int position = 1; position <= positions; position++)
        {
            ReadOnlySpan<int> cumulative = _content.NameCumulative(signature, position);
            if (cumulative.Length == 0)
            {
                _ = _random.NextInt(0, DiscardBound);
                continue;
            }

            int draw = _random.NextInt(0, cumulative[^1]);
            _nameWords[placed++] = _content.NameWords(signature, position)[LowerBound(cumulative, draw)];
        }

        return placed;
    }

    /// <summary>
    /// Step 11: the sockets, in AUTHORED order, every one empty. <b>There is NO draw here in v1</b>: a
    /// rolled socket count is a craft primitive rather than a generation step, so nothing here consumes a
    /// draw the owner has not asked for. Socket order is never sorted, unlike the affix list, so two
    /// otherwise identical items whose gems sit in different sockets do not stack, which is correct because
    /// they are different items.
    /// </summary>
    int SeatSockets(ReadOnlySpan<int> socketTypes)
    {
        int count = Math.Min(socketTypes.Length, _sockets.Length);
        for (int index = 0; index < count; index++)
        {
            _sockets[index] = new InstanceSocket(socketTypes[index], 0, 0, default);
        }

        return count;
    }

    /// <summary>
    /// Step 12: the payload, encoded canonically through the builder. The field ORDER here is irrelevant,
    /// because the builder holds its fields strictly ascending by kind whatever order they arrive in, so
    /// this method reads as spec 9.4 step 12's own list rather than as a byte layout.
    /// <para>
    /// <b>Kind 128 is written explicitly, state 0 and revealed mask 0.</b> Spec 9.4 step 12's field list
    /// does not name it and spec 12.7 requires it: an item carrying no <c>Identification</c> field at all is
    /// INDISTINGUISHABLE from an identified one under the visibility function, whose identified argument the
    /// caller would then have to guess. The generator is what decides a new item is unidentified, so the
    /// generator writes the field.
    /// </para>
    /// </summary>
    byte[] Assemble(
        in GenerationContext context,
        int baseIndex,
        int uniqueTemplateId,
        int rarityId,
        int affixCount,
        int wordCount,
        int socketCount)
    {
        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, (uint)context.ItemLevel);
        if (context.Quality > 0)
        {
            _ = builder.AddScalar(InstancePropertyKind.Quality, (uint)context.Quality);
        }

        int durability = Math.Min(_content.DurabilityAt(baseIndex), ushort.MaxValue);
        if (durability > 0)
        {
            _ = builder.AddScalars(InstancePropertyKind.Durability, (uint)durability, (uint)durability);
        }

        _ = builder.AddIdentification(identified: false, revealedMask: 0);
        if (uniqueTemplateId > 0)
        {
            _ = builder.AddScalar(InstancePropertyKind.UniqueTemplate, (uint)uniqueTemplateId);
        }

        if (rarityId > 0)
        {
            _ = builder.AddByte(InstancePropertyKind.Rarity, (byte)rarityId);
        }

        if (affixCount > 0)
        {
            _ = builder.AddAffixes(InstancePropertyKind.Affixes, _affixes.AsSpan(0, affixCount));
        }

        if (socketCount > 0)
        {
            _ = builder.AddSockets(_sockets.AsSpan(0, socketCount));
        }

        if (wordCount > 0)
        {
            _ = builder.AddRareName(rarityId, _nameWords.AsSpan(0, wordCount));
        }

        return builder.ToArray();
    }
}
