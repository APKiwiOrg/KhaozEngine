using System;
using System.IO;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>
/// Spec 17 row 1 and spec 20 phase 1's acceptance in one place: contracts 9.8's forty-five byte worked
/// example reproduced BYTE FOR BYTE, in both directions, plus the four reference-game rows of spec 3.8 with
/// the payload and slot-entry byte budgets that table pins.
/// <para>
/// The goldens are checked in under <c>Payload/Goldens</c> and copied beside the assembly. They are the
/// durable artifact: a change that moves one of these files has changed a stored byte format, and spec 21
/// records most of this format as expensive to change once data exists.
/// </para>
/// <para>
/// Every fact builds its own registry, so nothing here writes process-global state and no collection
/// attribute is needed.
/// </para>
/// </summary>
public class PayloadGoldenTests
{
    /// <summary>The block of contracts 9.8 and spec 3.7, copied in as it stands.</summary>
    const string WorkedExample = "contracts-9-8-worked-example.bin";

    [Fact]
    public void The_contracts_9_8_worked_example_encodes_to_its_forty_five_bytes()
    {
        byte[] golden = Golden(WorkedExample);
        Assert.Equal(45, golden.Length);

        Assert.Equal(golden, WorkedExampleBuilder().ToArray());
    }

    [Fact]
    public void The_worked_example_decodes_to_the_five_fields_9_8_names()
    {
        byte[] golden = Golden(WorkedExample);
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(registry, golden, fields, out int count, out string? reason));
        Assert.Null(reason);
        Assert.Equal(5, count);

        // Kinds 2, 5, 130, 131 and 132, ascending, which is rule 9.3.1.
        Assert.Equal(InstancePropertyKind.ItemLevel, fields[0].Kind);
        Assert.Equal(InstancePropertyKind.Durability, fields[1].Kind);
        Assert.Equal(InstancePropertyKind.Rarity, fields[2].Kind);
        Assert.Equal(InstancePropertyKind.Affixes, fields[3].Kind);
        Assert.Equal(InstancePropertyKind.Sockets, fields[4].Kind);

        // Item level 68, durability 90 of 100, rarity 3, the eighteen byte affix field and the ten byte
        // socket field, which is 3 + 4 + 4 + 21 + 13 = 45.
        Assert.Equal(new byte[] { 0x44 }, Body(golden, fields[0]));
        Assert.Equal(new byte[] { 0x5A, 0x64 }, Body(golden, fields[1]));
        Assert.Equal(new byte[] { 0x03 }, Body(golden, fields[2]));
        Assert.Equal(18, fields[3].BodyLength);
        Assert.Equal(10, fields[4].BodyLength);
        Assert.Equal(21, fields[3].FieldLength);
        Assert.Equal(13, fields[4].FieldLength);
    }

    [Fact]
    public void The_worked_examples_affixes_are_ascending_by_mod_id_whatever_order_they_were_rolled_in()
    {
        // The three affixes handed in the order contracts 9.8 originally listed them, 4210 then 91 then
        // 260. The ENCODER is what makes the field canonical, so the bytes come out 91, 260, 4210.
        byte[] rolled = new ItemInstancePayloadBuilder()
            .AddAffixes(
                InstancePropertyKind.Affixes,
                new[]
                {
                    new InstanceAffix(4210, 3, 52428),
                    new InstanceAffix(91, 1, 13107),
                    new InstanceAffix(260, 2, 65535),
                })
            .ToArray();

        byte[] authored = new ItemInstancePayloadBuilder()
            .AddAffixes(
                InstancePropertyKind.Affixes,
                new[]
                {
                    new InstanceAffix(91, 1, 13107),
                    new InstanceAffix(260, 2, 65535),
                    new InstanceAffix(4210, 3, 52428),
                })
            .ToArray();

        Assert.Equal(authored, rolled);
        Assert.True(ItemInstancePayload.SequenceEqual(authored, rolled));
    }

    /// <summary>
    /// Spec 3.8's four rows, which are budgets 1 and 2. The payload column is the tagged field bytes and
    /// the slot entry column is the whole container entry of spec 4.4, which is what a page and therefore a
    /// commit actually costs.
    /// </summary>
    public static TheoryData<string, int, int> ReferenceRows() => new()
    {
        { "spec-3-8-osrs-coins.bin", 0, 7 },
        { "spec-3-8-tibia-wand.bin", 7, 15 },
        { "spec-3-8-mortal-online-longsword.bin", 20, 30 },
        { "spec-3-8-poe-greatsword.bin", 58, 69 },
    };

    [Theory]
    [MemberData(nameof(ReferenceRows))]
    public void The_four_reference_rows_hold_their_payload_and_slot_entry_budgets(
        string name,
        int payloadBytes,
        int slotEntryBytes)
    {
        byte[] golden = Golden(name);
        Assert.Equal(payloadBytes, golden.Length);

        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Assert.Null(ItemInstancePayload.Validate(registry, golden));
        Assert.Equal(golden, Rebuild(name).ToArray());
        Assert.Equal(slotEntryBytes, SlotEntry(name, golden.Length));
    }

    [Fact]
    public void The_OSRS_row_is_the_floor_and_beats_version_ones_fixed_ten_bytes()
    {
        // An OSRS shaped bank gets SMALLER when instances arrive, which is spec 3.8's compatibility
        // argument: seven bytes against version 1's fixed ten (ItemContainerCodec.EntryBytes = 2 + 4 + 4).
        Assert.Empty(Golden("spec-3-8-osrs-coins.bin"));
        Assert.Equal(7, SlotEntry("spec-3-8-osrs-coins.bin", 0));
        Assert.True(SlotEntry("spec-3-8-osrs-coins.bin", 0) < 10);
    }

    [Fact]
    public void The_PoE_row_is_the_worked_example_plus_identification_and_a_rare_name()
    {
        // Spec 3.8: "The contracts' 45 byte example plus five bytes of identification state and eight of
        // rare name." The two added fields sort into the middle and the end, which is why the whole payload
        // is not a concatenation.
        Assert.Equal(45 + 5 + 8, Golden("spec-3-8-poe-greatsword.bin").Length);
    }

    /// <summary>The worked example of contracts 9.8, built through the public builder.</summary>
    static ItemInstancePayloadBuilder WorkedExampleBuilder()
    {
        byte[] nested = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 55)
            .ToArray();

        return new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .AddByte(InstancePropertyKind.Rarity, 3)
            .AddAffixes(
                InstancePropertyKind.Affixes,
                new[]
                {
                    new InstanceAffix(4210, 3, 52428),
                    new InstanceAffix(91, 1, 13107),
                    new InstanceAffix(260, 2, 65535),
                })
            .AddSockets(new[] { new InstanceSocket(7, 833, 4201, nested) });
    }

    /// <summary>Each reference row's item, rebuilt from its spec 3.8 description.</summary>
    static ItemInstancePayloadBuilder Rebuild(string name) => name switch
    {
        // 500 coins: no fields at all, which is what a plain stack carries.
        "spec-3-8-osrs-coins.bin" => new ItemInstancePayloadBuilder(),

        // A wand of vortex, 18 charges of 20, upgrade tier 2. Kinds 4 and 8, no mod and no roll.
        "spec-3-8-tibia-wand.bin" => new ItemInstancePayloadBuilder()
            .AddScalars(InstancePropertyKind.Charges, 18, 20)
            .AddScalar(InstancePropertyKind.Tier, 2),

        // A crafted longsword: quality 87, durability 140 of 155, three materials in a 60/30/10 ratio.
        // The materials field stores the INPUT ids and their parts, never the stats derived from them.
        "spec-3-8-mortal-online-longsword.bin" => new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.Quality, 87)
            .AddScalars(InstancePropertyKind.Durability, 140, 155)
            .AddMaterials(
                new[]
                {
                    new InstanceMaterial(12, 60),
                    new InstanceMaterial(200, 30),
                    new InstanceMaterial(301, 10),
                }),

        "spec-3-8-poe-greatsword.bin" => PoeGreatsword(),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No reference row by that name."),
    };

    static ItemInstancePayloadBuilder PoeGreatsword()
    {
        byte[] nested = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 55)
            .ToArray();

        return new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .AddIdentification(identified: true, revealedMask: 0)
            .AddByte(InstancePropertyKind.Rarity, 3)
            .AddAffixes(
                InstancePropertyKind.Affixes,
                new[]
                {
                    new InstanceAffix(91, 1, 13107),
                    new InstanceAffix(260, 2, 65535),
                    new InstanceAffix(4210, 3, 52428),
                })
            .AddSockets(new[] { new InstanceSocket(7, 833, 4201, nested) })
            .AddRareName(7, new[] { 17, 34, 51 });
    }

    /// <summary>
    /// Spec 4.4's slot entry over a payload, counted rather than encoded: task 8 of this plan ships the
    /// codec and this is the arithmetic that pins its budget ahead of it.
    /// <para>
    /// Spec 3.8 writes the OSRS and Tibia breakdowns out in full and pins only the totals for the other two
    /// rows. The definition id, the count and the instance id widths below are what make 30 and 69 come
    /// out, so they are stated here rather than left implied.
    /// </para>
    /// </summary>
    static int SlotEntry(string name, int payloadLength) => name switch
    {
        // Slot 1, no flags, definition id 1 deliberately (the FLOOR), count 500 at two varint bytes, no
        // instance id, an empty payload.
        "spec-3-8-osrs-coins.bin" => EntryBytes(1, 0, 1, 500, 0, payloadLength),

        // A two byte definition id, a single wand, and instance id 4,201 at two varint bytes.
        "spec-3-8-tibia-wand.bin" => EntryBytes(1, 0, 4200, 1, 4201, payloadLength),

        // A two byte definition id and an instance id at four varint bytes.
        "spec-3-8-mortal-online-longsword.bin" => EntryBytes(1, 0, 4200, 1, 20_000_000, payloadLength),

        // A two byte definition id and an instance id at five varint bytes, which is the realistic width
        // for a node prefixed id on a live shard.
        "spec-3-8-poe-greatsword.bin" => EntryBytes(1, 0, 4200, 1, 4_000_000_000, payloadLength),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No reference row by that name."),
    };

    static int EntryBytes(
        int slot,
        uint entryFlags,
        int definitionId,
        int count,
        ulong instanceId,
        int payloadLength)
        => ContentVarint.Size((uint)slot)
            + ContentVarint.Size(entryFlags)
            + ContentVarint.Size((uint)definitionId)
            + ContentVarint.Size((uint)count)
            + ContentVarint.SizeUInt64(instanceId)
            + ContentVarint.Size((uint)payloadLength)
            + payloadLength;

    static byte[] Body(ReadOnlySpan<byte> payload, PayloadField field)
        => payload.Slice(field.BodyStart, field.BodyLength).ToArray();

    static byte[] Golden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", name));
}
