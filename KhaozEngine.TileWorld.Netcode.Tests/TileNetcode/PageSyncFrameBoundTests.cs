using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.ItemInstances;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Spec 17 row 17, and budgets 7 and 8 of spec 16. The one place both halves of spec 7.5 exist at once:
/// the delta builder is <c>KhaozEngine.ItemInstances</c>'s, because only the page codec can write the entry
/// body of 4.4, and the fragmenter and the game message envelope are this package's, because only it owns
/// the frame. The composition is the SERVER's, so the facts here model the server's choice and nothing in
/// either package knows about the other.
/// <para>What these protect is the tick. <see cref="TileProtocol.EncodeGameMessage"/> THROWS above
/// <see cref="TileProtocol.MaxGameMessageBytes"/>, a delta is sent from inside the per-viewer serve loop,
/// and nothing here catches around it, so a delta that measured wrong takes the tick down for every player
/// on the server rather than dropping one message. The combat path already paid for this exact shape
/// (<c>TileWorldServer.Tick.cs</c>).</para>
/// </summary>
public class PageSyncFrameBoundTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;
    const byte ContainerId = 3;
    const byte PageIndex = 0;
    const int FirstSlot = 0;
    const byte StreamId = 11;

    /// <summary>Kinds are the GAME's to assign, because <see cref="TileProtocol"/> reserves the ushort kind
    /// space to the game and the engine only caps the frame. These two are this test's own.</summary>
    const ushort DeltaKind = 0x0301;
    const ushort ChunkKind = 0x0302;

    const int PoeDefinitionId = 4200;
    const long PoeFirstCounter = 4_000_000_000;

    static InstancePropertyRegistry Registry() => InstancePropertyRegistry.CreateV1();

    /// <summary>Spec 3.8's rare, 58 bytes. Rebuilt here rather than read from a golden, because the goldens
    /// are <c>KhaozEngine.ItemInstances.Tests</c>'s and a second copy of a stored byte format is exactly what
    /// a golden exists to prevent. The length assertion is what catches a drift between the two.</summary>
    static byte[] Rare { get; } = new ItemInstancePayloadBuilder()
        .AddScalar(InstancePropertyKind.ItemLevel, 68)
        .AddScalars(InstancePropertyKind.Durability, 90, 100)
        .AddIdentification(identified: true, revealedMask: 0)
        .AddByte(InstancePropertyKind.Rarity, 3)
        .AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(91, 1, 13107), new InstanceAffix(260, 2, 65535), new InstanceAffix(4210, 3, 52428)])
        .AddSockets([new InstanceSocket(7, 833, 4201, new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 55)
            .ToArray())])
        .AddRareName(7, [17, 34, 51])
        .ToArray();

    /// <summary>The full page of a hundred rares, 6,908 bytes: budget 3, and what budget 7 fragments.</summary>
    static byte[] FullPage()
    {
        var entries = new PageSlotInput[PageSlots];
        for (int slot = 0; slot < PageSlots; slot++)
            entries[slot] = new PageSlotInput(
                slot, 0, PoeDefinitionId, 1, InstanceIdAllocator.Pack(0, PoeFirstCounter + slot), Rare);
        return ItemContainerPageCodec.Encode(PageIndex, FirstSlot, PageSlots, contentVersion: 100, entries);
    }

    static ContainerPageChange RareAt(int slot) =>
        ContainerPageChange.Occupied(
            new PageSlotInput(slot, 0, PoeDefinitionId, 1, InstanceIdAllocator.Pack(0, PoeFirstCounter + slot), Rare),
            identified: true,
            revealedMask: ulong.MaxValue);

    static ContainerPageChange[] Rares(int howMany)
    {
        var changes = new ContainerPageChange[howMany];
        for (int slot = 0; slot < howMany; slot++) changes[slot] = RareAt(slot);
        return changes;
    }

    /// <summary>
    /// The server's choice, which is the composition neither package can make on its own: try the delta, and
    /// when it abandons send the WHOLE PAGE through the fragmenter. Never a second delta frame, because two
    /// deltas for one page would have to be applied in order by a client that may have missed the first,
    /// which is the reassembly problem the fragmenter already solves once.
    /// </summary>
    static byte[][] Serve(ReadOnlySpan<ContainerPageChange> changes, byte[] page, out bool asDelta)
    {
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        int written = ContainerPageDelta.TryBuild(
            buffer, Registry(), PropertyVisibility.OwnerOnly, ContainerId, PageIndex, FirstSlot, PageSlots, changes);
        if (written >= 0)
        {
            asDelta = true;
            return [TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, DeltaKind, buffer[..written])];
        }

        asDelta = false;
        return Frames(TileFragmentedMessage.Fragment(StreamId, sequence: 1, page));
    }

    static byte[][] Frames(byte[][] chunks)
    {
        var frames = new byte[chunks.Length][];
        for (int i = 0; i < chunks.Length; i++)
            frames[i] = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, ChunkKind, chunks[i]);
        return frames;
    }

    [Fact]
    public void Fourteen_changed_rare_slots_produce_ONE_delta_frame()
    {
        byte[][] frames = Serve(Rares(14), FullPage(), out bool asDelta);

        Assert.True(asDelta);
        Assert.Single(frames);
        Assert.Equal(ContainerPageDelta.GameMessageEnvelopeBytes + ContainerPageDelta.HeaderBytes + (14 * 70), frames[0].Length);
        Assert.True(frames[0].Length <= TileProtocol.MaxGameMessageBytes);
    }

    [Fact]
    public void Fifteen_changed_rare_slots_produce_a_FRAGMENTED_page_send()
    {
        byte[] page = FullPage();
        byte[][] frames = Serve(Rares(15), page, out bool asDelta);

        Assert.False(asDelta);
        Assert.Equal(TileFragmentedMessage.ChunkCount(page.Length), frames.Length);
        Assert.All(frames, frame => Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes));

        // And the client reassembles exactly the page the server encoded, which is what makes abandoning the
        // delta a complete answer rather than a dropped update.
        var reassembler = new TileFragmentReassembler(0);
        ReadOnlyMemory<byte> assembled = default;
        foreach (byte[] chunk in TileFragmentedMessage.Fragment(StreamId, sequence: 1, page))
            reassembler.TryComplete(chunk, out assembled, out _);
        Assert.Equal(page, assembled.ToArray());
    }

    [Fact]
    public void A_100_slot_reorder_produces_a_fragmented_page_send()
    {
        byte[] page = FullPage();
        byte[][] frames = Serve(Rares(PageSlots), page, out bool asDelta);

        Assert.False(asDelta);
        Assert.Equal(7, frames.Length);
        Assert.All(frames, frame => Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes));
    }

    [Fact]
    public void A_cold_open_of_a_full_rare_page_is_at_most_8_KB_in_at_most_8_frames()
    {
        // Spec 16 budget 7: at most 8 KB and 8 frames per page. The page is 6,908 bytes (budget 3) and the
        // fragment headers are five bytes a chunk, so the wire cost is 6,943 in seven frames, with one
        // spare frame of headroom.
        byte[] page = FullPage();
        Assert.Equal(6908, page.Length);

        byte[][] chunks = TileFragmentedMessage.Fragment(StreamId, sequence: 0, page);
        byte[][] frames = Frames(chunks);

        Assert.Equal(7, frames.Length);
        Assert.True(frames.Length <= 8, $"a cold open of a full rare page is {frames.Length} frames");

        int chunkBytes = chunks.Sum(chunk => chunk.Length);
        Assert.Equal(6943, chunkBytes);
        Assert.True(chunkBytes <= 8 * 1024, $"a cold open of a full rare page is {chunkBytes} bytes");
        Assert.All(frames, frame => Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes));
    }

    [Fact]
    public void A_single_craft_costs_73_bytes_in_one_frame()
    {
        // Spec 16 budget 8: one frame, at most 96 bytes. A crafted rare changes ONE slot, so the client needs
        // that slot rather than the 6.9 KB page, which is the whole reason the delta exists beside the
        // fragmenter.
        byte[][] frames = Serve([RareAt(7)], FullPage(), out bool asDelta);

        Assert.True(asDelta);
        Assert.Single(frames);
        Assert.Equal(73 + ContainerPageDelta.GameMessageEnvelopeBytes, frames[0].Length);
        Assert.True(TileProtocol.TryDecodeGameMessage(
            frames[0], TileProtocol.ServerFrameGameMessage, out ushort kind, out ReadOnlySpan<byte> payload));
        Assert.Equal(DeltaKind, kind);
        Assert.Equal(73, payload.Length);
        Assert.True(payload.Length <= 96);
    }

    [Fact]
    public void No_path_encodes_a_game_message_above_MaxGameMessageBytes()
    {
        // The tick protecting fact, and the reason the builder measures rather than truncates. Exhaustive
        // over the delta sizes that STRADDLE the boundary: the per change cost is varied one byte at a time
        // through the count field's varint width and two at a time through the payload, and the change count
        // is walked past the point each cost crosses the budget.
        var buffer = new byte[ContainerPageDelta.MaxBytes];
        int sent = 0;
        int abandoned = 0;
        int longest = 0;

        for (int materials = 0; materials <= 6; materials++)
        {
            byte[] payload = Padded(materials);
            foreach (int count in (int[])[1, 200, 20_000, 300_000_000])
            {
                int unit = Build(buffer, [Change(0, payload, count)]) - ContainerPageDelta.HeaderBytes;
                Assert.True(unit > 0);

                int expected = ContainerPageDelta.HeaderBytes;
                for (int changes = 1; changes <= 160; changes++)
                {
                    expected += Build(buffer, [Change(changes - 1, payload, count)]) - ContainerPageDelta.HeaderBytes;
                    int written = Build(buffer, Changes(changes, payload, count));
                    if (expected > ContainerPageDelta.MaxBytes)
                    {
                        Assert.Equal(-1, written);
                        abandoned++;
                        continue;
                    }

                    Assert.Equal(expected, written);
                    byte[] frame = TileProtocol.EncodeGameMessage(
                        TileProtocol.ServerFrameGameMessage, DeltaKind, buffer.AsSpan(0, written));
                    Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes,
                        $"{changes} changes of {unit} bytes encoded a {frame.Length} byte frame");
                    sent++;
                    longest = Math.Max(longest, written);
                }
            }
        }

        Assert.True(sent > 0);
        Assert.True(abandoned > 0);
        Assert.True(longest >= ContainerPageDelta.MaxChangeBytes,
            $"the boundary was never pressed: the longest delta sent was {longest} bytes");

        // The fragmenter half, over the lengths that straddle a chunk boundary and the page itself.
        foreach (int length in (int[])[
            0, 1,
            TileFragmentedMessage.MaxChunkPayloadBytes - 1,
            TileFragmentedMessage.MaxChunkPayloadBytes,
            TileFragmentedMessage.MaxChunkPayloadBytes + 1,
            (2 * TileFragmentedMessage.MaxChunkPayloadBytes) - 1,
            2 * TileFragmentedMessage.MaxChunkPayloadBytes,
            (2 * TileFragmentedMessage.MaxChunkPayloadBytes) + 1,
            6908])
        {
            byte[][] frames = Frames(TileFragmentedMessage.Fragment(StreamId, sequence: 2, new byte[length]));
            Assert.All(frames, frame => Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes,
                $"a {length} byte payload fragmented into a {frame.Length} byte frame"));
        }
    }

    [Fact]
    public void No_client_to_server_message_carries_an_instance_payload()
    {
        // Spec 7.6's invariant, which is 15.3 in practice: no client to server message in this design carries
        // an instance payload, and every one of them names an item by id. The full version is spec 17 row 13
        // and belongs to the phase that ships the craft messages, so this covers the two that exist.
        Assert.Equal(2, ContainerPageSyncRequest.Bytes);
        Span<byte> request = stackalloc byte[ContainerPageSyncRequest.Bytes];
        Assert.Equal(2, new ContainerPageSyncRequest(ContainerId, PageIndex).Write(request));
        Assert.Equal(ContainerId, request[0]);
        Assert.Equal(PageIndex, request[1]);

        // The take request is unchanged and names the drop by NET ID, which the server resolves against its
        // own ground item list. Nothing in the frame describes the item.
        byte[] take = TileProtocol.EncodeCommand(4, TileCommand.InteractEntity(netId: 9_000_001, TileMoveMode.Walk));
        Assert.Equal(24, take.Length);
        Assert.True(TileProtocol.TryDecodeCommand(take, planeCount: 4, out _, out TileCommand decoded));
        Assert.Equal(TileCommandKind.InteractEntity, decoded.Kind);
        Assert.Equal(9_000_001L, decoded.Target);

        // Architecture shaped rather than byte shaped: every member of both messages is a number or an enum,
        // so there is no field a payload could ride in even if a later encoder wanted one.
        foreach (Type message in (Type[])[typeof(ContainerPageSyncRequest), typeof(TileCommand)])
        {
            foreach (PropertyInfo property in message.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(
                    IsNameOrNumber(property.PropertyType),
                    $"{message.Name}.{property.Name} is a {property.PropertyType.Name}, which is payload shaped");
            }
        }
    }

    [Fact]
    public void The_delta_budget_is_the_netcodes_own_frame_cap()
    {
        // ContainerPageDelta COPIES the frame cap and the envelope width, because KhaozEngine.ItemInstances
        // is a Foundation package and KhaozEngine.TileWorld.Netcode is a server one, so the delta builder
        // cannot reference the protocol that caps it. This test project is the only one that sees both, so
        // this is the only place the copy can be held equal.
        Assert.Equal(TileProtocol.MaxGameMessageBytes, ContainerPageDelta.MaxGameMessageBytes);

        // The envelope is [tag:1][kind:2][flags:1] and its width is internal to TileProtocol, so it is read
        // back out of what the fragmenter already derives from it rather than written down a third time.
        int envelope = TileProtocol.MaxGameMessageBytes
            - TileFragmentedMessage.MaxChunkPayloadBytes
            - TileFragmentedMessage.HeaderBytes;
        Assert.Equal(ContainerPageDelta.GameMessageEnvelopeBytes, envelope);

        // The budget is deliberately four bytes TIGHTER than the encoder's own throw, which caps the payload
        // rather than the datagram (issue 923). Spec 7.5 subtracts the envelope, so the delta is sized to fit
        // a 1,024 byte datagram and not merely a 1,024 byte payload.
        Assert.Equal(
            ContainerPageDelta.MaxChangeBytes,
            TileProtocol.MaxGameMessageBytes - envelope - ContainerPageDelta.HeaderBytes);
    }

    /// <summary>Whether a member could only ever be a name or a number: an enum, a number, or a value type
    /// built out of those. A reference type is the payload shape this invariant refuses, because bytes reach
    /// a message through an array, a string or a class and never through an int.</summary>
    static bool IsNameOrNumber(Type type)
    {
        if (type.IsEnum || type.IsPrimitive) return true;
        if (!type.IsValueType) return false;

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .All(property => IsNameOrNumber(property.PropertyType))
            && type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .All(field => IsNameOrNumber(field.FieldType));
    }

    /// <summary>The rare's payload with a controlled number of material entries on it, which grows the
    /// projected body two bytes at a time without leaving the canonical format.</summary>
    static byte[] Padded(int materials)
    {
        var entries = new InstanceMaterial[materials];
        for (int i = 0; i < materials; i++) entries[i] = new InstanceMaterial(i + 1, (ushort)(i + 1));
        var builder = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 68);
        if (materials > 0) builder = builder.AddMaterials(entries);
        return builder.ToArray();
    }

    static ContainerPageChange Change(int slot, byte[] payload, int count) =>
        ContainerPageChange.Occupied(
            new PageSlotInput(slot, 0, PoeDefinitionId, count, InstanceIdAllocator.Pack(0, PoeFirstCounter), payload),
            identified: true,
            revealedMask: ulong.MaxValue);

    static ContainerPageChange[] Changes(int howMany, byte[] payload, int count)
    {
        var changes = new ContainerPageChange[howMany];
        for (int slot = 0; slot < howMany; slot++) changes[slot] = Change(slot, payload, count);
        return changes;
    }

    static int Build(byte[] destination, ReadOnlySpan<ContainerPageChange> changes)
        => ContainerPageDelta.TryBuild(
            destination,
            Registry(),
            PropertyVisibility.OwnerOnly,
            ContainerId,
            PageIndex,
            FirstSlot,
            slotCount: 256,
            changes);
}
