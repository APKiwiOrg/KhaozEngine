using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// The containers, definitions and payloads the commit builder facts run over.
/// <para>
/// Everything is built through the REAL types: a <see cref="PagedItemContainer"/> with its own doors, payloads
/// the payload codec encoded, and the quarantine wrapper's own verifier. A fact about what a batch writes is
/// only a fact if the pages it writes are pages the codec would accept back.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no test using it needs a collection attribute.
/// </para>
/// </summary>
internal static class ContainerCommitFixtures
{
    /// <summary>The stream every batch writes.</summary>
    public const string StreamKey = "player:7";

    /// <summary>The container most facts act on.</summary>
    public const string Bank = "bank";

    /// <summary>A second container on the SAME stream, which is what a two container move needs.</summary>
    public const string Bag = "bag";

    /// <summary>A container name no batch here is opened over, which is a second stream by definition.</summary>
    public const string Vault = "vault";

    /// <summary>The authenticated scope a batch is admitted under.</summary>
    public const string Scope = "world/account";

    /// <summary>A definition the game does not stack, which is what an owned item is.</summary>
    public const int Sword = 100;

    /// <summary>A definition the game stacks.</summary>
    public const int Potion = 1_000;

    /// <summary>A crafting currency, stacked.</summary>
    public const int Currency = 1_001;

    /// <summary>The instance id a fact uses unless it needs its own.</summary>
    public const long Instance = 7_001;

    /// <summary>The game's stacking rule: everything from 1,000 up stacks, and nothing below it does.</summary>
    public static Func<int, bool> Stackable => static definitionId => definitionId >= 1_000;

    /// <summary>A fresh empty container.</summary>
    /// <param name="pageCount">How many pages of a hundred slots.</param>
    /// <param name="capacity">The occupied-slot gate of spec 5.7.</param>
    public static PagedItemContainer Container(int pageCount = 2, int capacity = 10_000)
        => new(pageCount, capacity, Stackable, static _ => true, QuarantineWrapper.Verify);

    /// <summary>The container set a batch is opened over, by name.</summary>
    public static Dictionary<string, PagedItemContainer> Containers(params (string Name, PagedItemContainer Container)[] entries)
    {
        var set = new Dictionary<string, PagedItemContainer>(StringComparer.Ordinal);
        foreach ((string name, PagedItemContainer container) in entries) set.Add(name, container);
        return set;
    }

    /// <summary>One canonical instance payload, which the container's door accepts and the stacking rule can
    /// compare byte for byte.</summary>
    public static byte[] Payload(int itemLevel = 42)
    {
        var builder = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, (ulong)itemLevel);
        byte[] bytes = new byte[builder.Length];
        ItemInstancePayload.Encode(builder, bytes);
        return bytes;
    }

    /// <summary>Seats an owned item through the LOAD door, so the container starts clean rather than dirty.</summary>
    public static void SeatItem(PagedItemContainer container, int slot, int definitionId, long instanceId, byte[]? payload = null)
        => container.Seat(slot, new ItemSlot(new ItemStack(definitionId, 1, instanceId), payload ?? Payload(), false));

    /// <summary>Seats a plain stack through the LOAD door.</summary>
    public static void SeatStack(PagedItemContainer container, int slot, int definitionId, int count)
        => container.Seat(slot, new ItemSlot(new ItemStack(definitionId, count), default, false));

    /// <summary>A readable craft audit body. The test chooses an after level that matches the operation it
    /// hands to the builder, so the stored event cannot claim a different payload from the working copy.</summary>
    public static byte[] CraftEventBody(int ordinal, int? afterLevel = null, int? beforeLevel = null)
        => new ItemCraftedEvent(1, Instance, 1,
            Payload(beforeLevel ?? (ordinal == 0 ? 42 : ordinal)),
            Payload(afterLevel ?? ordinal + 1)).ToArray();

    /// <summary>A batch opened over one bank, with the options a fact usually wants.</summary>
    public static ContainerCommitBuilder OpenBank(
        PagedItemContainer bank,
        long tick = 4,
        ContainerCommitOptions? options = null,
        string actionKind = ItemInstanceEvents.CraftActionKind)
        => ContainerCommitBuilder.Open(
            StreamKey, actionKind, Scope, Containers((Bank, bank)), tick, options);

    /// <summary>A deterministic server operation id, so a fact reads the same on every run.</summary>
    public static Func<Guid> Mint(Guid operationId) => () => operationId;

    /// <summary>The operation id a server minted batch takes unless a fact needs its own.</summary>
    public static Guid ServerId { get; } = new("11111111-2222-3333-4444-555555555555");
}
