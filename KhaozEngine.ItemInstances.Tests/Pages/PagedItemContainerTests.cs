using System;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Spec 5.2, 5.3 and 5.7: the page geometry, the two things that dirty a page, and Ruinborne's capacity
/// model restated as engine behaviour. Capacity is a GATE rather than a size, so the address space and the
/// number of occupied slots a grant may leave behind are two different numbers here and one number in
/// <c>ItemContainer</c>.
/// <para>
/// Nothing in this class writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class PagedItemContainerTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;
    const int Sword = 4200;
    const int Potion = 995;

    const BindingFlags Surface =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static bool Stacks(int definitionId) => definitionId != 0;

    static bool NeverStacks(int definitionId)
    {
        _ = definitionId;
        return false;
    }

    static PagedItemContainer NewContainer(int pageCount, int capacity, Func<int, bool>? stackable = null) =>
        new(pageCount, capacity, stackable ?? Stacks, ItemInstancePayload.IsCanonical, QuarantineWrapper.Verify);

    static ItemSlot Slot(int definitionId, int count, long instanceId = 0, byte[]? payload = null) =>
        new(new ItemStack(definitionId, count, instanceId), payload ?? Array.Empty<byte>(), Quarantined: false);

    static byte[] Level(ulong itemLevel) =>
        new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, itemLevel).ToArray();

    [Fact]
    public void A_grant_that_opens_a_NEW_slot_is_refused_at_or_above_capacity()
    {
        // Rule 1 of 5.7. Two pages of address space, three slots of gate.
        PagedItemContainer container = NewContainer(pageCount: 2, capacity: 3, NeverStacks);

        Assert.Equal(3, container.Add(Sword, 5));
        Assert.Equal(3, container.Occupancy);
        Assert.Equal(0, container.Add(Potion, 1));
        Assert.Equal(0, container.Add(Slot(Sword, 1, 11, Level(68))));

        // The address space is untouched by the refusal: capacity is a gate and never a size.
        Assert.Equal(2 * PageSlots, container.SlotSpace);
        Assert.Equal(2 * PageSlots - 3, container.FreeSlots);
    }

    [Fact]
    public void A_grant_that_merges_entirely_into_existing_stacks_is_allowed_at_any_occupancy()
    {
        // Rule 2 of 5.7. Capacity 1, one occupied slot, and a grant that needs no new slot still lands.
        PagedItemContainer container = NewContainer(pageCount: 1, capacity: 1);

        Assert.Equal(10, container.Add(Potion, 10));
        Assert.Equal(1, container.Occupancy);

        Assert.Equal(90, container.Add(Potion, 90));
        Assert.Equal(100, container.SlotAt(0).Stack.Count);
        Assert.Equal(1, container.Occupancy);

        // The same occupancy refuses a grant that would open a slot, which is what makes this rule 2 rather
        // than a hole in rule 1.
        Assert.Equal(0, container.Add(Sword, 1));
    }

    [Fact]
    public void Lowering_capacity_below_occupancy_is_legal_and_trims_nothing()
    {
        // Rule 3 of 5.7, the surprising one, and the generalisation of contracts 8.2 kind 4's over-cap
        // stack policy: the container loads intact, is never trimmed, and is refused new slots until
        // occupancy falls.
        PagedItemContainer container = NewContainer(pageCount: 1, capacity: 10, NeverStacks);
        Assert.Equal(5, container.Add(Sword, 5));
        container.MarkClean();

        container.Capacity = 2;

        Assert.Equal(2, container.Capacity);
        Assert.Equal(5, container.Occupancy);
        for (int slot = 0; slot < 5; slot++) Assert.Equal(Slot(Sword, 1), container.SlotAt(slot));

        // Lowering the gate is not an operation on a slot, so it dirties nothing.
        Assert.Equal(0, container.DirtyPageCount);
        Assert.Equal(0, container.Add(Potion, 1));

        // It only shrinks. Once occupancy falls below the gate, a new slot opens again.
        for (int slot = 0; slot < 4; slot++) container.TakeSlotAt(slot);
        Assert.Equal(1, container.Occupancy);
        Assert.Equal(1, container.Add(Potion, 1));
    }

    [Fact]
    public void Capacity_is_never_read_from_content()
    {
        // Rule 4 of 5.7. Capacity is per-owner progression rather than balance data, so it is an integer
        // the game sets and nothing here can reach a content row to find one. A behavioural test cannot
        // prove that negative and the type's own surface can: no member of the container names a content
        // type, so there is nowhere for a capacity to arrive from except the setter.
        foreach (ConstructorInfo constructor in typeof(PagedItemContainer).GetConstructors(Surface))
            foreach (ParameterInfo parameter in constructor.GetParameters())
                Assert.False(NamesContent(parameter.ParameterType), $"the constructor takes {parameter.ParameterType}");

        foreach (MethodInfo method in typeof(PagedItemContainer).GetMethods(Surface))
        {
            Assert.False(NamesContent(method.ReturnType), $"{method.Name} returns {method.ReturnType}");
            foreach (ParameterInfo parameter in method.GetParameters())
                Assert.False(NamesContent(parameter.ParameterType), $"{method.Name} takes {parameter.ParameterType}");
        }

        foreach (PropertyInfo property in typeof(PagedItemContainer).GetProperties(Surface))
            Assert.False(NamesContent(property.PropertyType), $"{property.Name} is {property.PropertyType}");

        PagedItemContainer container = NewContainer(pageCount: 1, capacity: 3);
        container.Capacity = 4;
        Assert.Equal(4, container.Capacity);
    }

    [Fact]
    public void A_hole_survives_a_load_a_save_and_a_remap_and_costs_zero_bytes()
    {
        PagedItemContainer container = NewContainer(pageCount: 1, capacity: PageSlots);
        container.Seat(0, Slot(Potion, 5));
        container.Seat(1, Slot(Sword, 1, 11, Level(68)));
        container.Seat(2, Slot(Sword, 1, 12, Level(70)));
        Assert.Equal(3, container.Occupancy);

        container.TakeSlotAt(1);
        ItemContainerPage page = container.Pages[0];
        Assert.Equal(2, page.EntryCount);

        Span<PageSlotInput> entries = new PageSlotInput[PageSlots];
        int count = page.CopyEntriesTo(entries);
        byte[] saved = ItemContainerPageCodec.Encode(
            page.PageIndex, page.FirstSlot, page.SlotCount, page.ContentVersion, entries[..count]);

        // ZERO bytes: the page carrying a hole at slot 1 is byte for byte the page that never held anything
        // there, because the entries are sparse and nothing renumbers them.
        PageSlotInput[] never =
        [
            new(0, 0, Potion, 5, 0, Array.Empty<byte>()),
            new(2, 0, Sword, 1, 12, Level(70)),
        ];
        Assert.Equal(ItemContainerPageCodec.Encode(0, 0, PageSlots, page.ContentVersion, never), saved);

        Span<PageEntry> decoded = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(
            saved, PageSlots, decoded, out PageHeader header, out int decodedCount, out string? reason));
        Assert.Null(reason);

        PagedItemContainer reloaded = NewContainer(pageCount: 1, capacity: PageSlots);
        reloaded.Pages[0].SeatStamp(header.ContentVersion);
        for (int index = 0; index < decodedCount; index++)
        {
            PageEntry entry = decoded[index];
            reloaded.Seat(entry.Slot, new ItemSlot(
                new ItemStack(entry.DefinitionId, entry.Count, entry.InstanceId),
                saved.AsSpan(entry.PayloadStart, entry.PayloadLength).ToArray(),
                entry.Quarantined));
        }

        Assert.True(reloaded.SlotAt(1).IsEmpty);
        Assert.Equal(2, reloaded.Occupancy);
        Assert.False(reloaded.Pages[0].IsDirty);

        // A remap rewrites slot 2 and the hole is still a hole afterwards: nothing on this path compacts,
        // so the dense renumber Ruinborne's repair refuses to do cannot happen here by accident.
        ItemSlot target = reloaded.SlotAt(2);
        Assert.True(reloaded.Pages[0].ApplyRemap(
            2, target with { Stack = target.Stack with { ItemId = 4300 } }, activeContentVersion: 12));
        Assert.True(reloaded.SlotAt(1).IsEmpty);
        Assert.Equal(0, reloaded.SlotAt(1).Stack.ItemId);
        Assert.Equal(2, reloaded.Pages[0].EntryCount);
        Assert.Equal(2, reloaded.Pages[0].CopyEntriesTo(entries));
        Assert.Equal(0, entries[0].Slot);
        Assert.Equal(2, entries[1].Slot);
    }

    [Fact]
    public void Reading_a_page_never_dirties_it()
    {
        // Spec 5.3: exactly two things dirty a page, an operation that CHANGED a slot and a remap that
        // changed an id. Reading is neither, and a write that changes nothing is neither.
        PagedItemContainer container = NewContainer(pageCount: 2, capacity: 200);
        container.Seat(0, Slot(Potion, 5));
        Assert.Equal(0, container.DirtyPageCount);

        _ = container.SlotAt(0);
        _ = container.Pages[0].SlotAt(0);
        _ = container.Occupancy;
        _ = container.FreeSlots;
        _ = container.CountOf(Potion);
        _ = container.Pages[0].EntryCount;
        _ = container.Pages[0].ContentVersion;
        Span<PageSlotInput> entries = new PageSlotInput[PageSlots];
        _ = container.Pages[0].CopyEntriesTo(entries);
        Assert.Equal(0, container.DirtyPageCount);
        Assert.False(container.Pages[0].IsDirty);

        // An operation that changes a slot dirties THAT page and no other.
        container.SetSlotAt(150, Slot(Sword, 1, 11, Level(68)));
        Assert.False(container.Pages[0].IsDirty);
        Assert.True(container.Pages[1].IsDirty);

        Span<ItemContainerPage> dirty = new ItemContainerPage[container.PageCount];
        Assert.Equal(1, container.DirtyPageCount);
        Assert.Equal(1, container.CopyDirtyPagesTo(dirty));
        Assert.Same(container.Pages[1], dirty[0]);

        // Writing the same slot value back changes nothing, so it dirties nothing.
        container.MarkClean();
        container.SetSlotAt(150, Slot(Sword, 1, 11, Level(68)));
        Assert.Equal(0, container.DirtyPageCount);

        // A remap that changes nothing is the same: almost every rule on almost every page is a scan.
        Assert.False(container.Pages[1].ApplyRemap(150, container.SlotAt(150), activeContentVersion: 12));
        Assert.Equal(0, container.DirtyPageCount);
        Assert.Equal(0, container.Pages[1].ContentVersion);
    }

    [Fact]
    public void Slot_743_is_page_7_slot_43()
    {
        Assert.Equal(100, PageSlots);
        Assert.Equal(7, ItemContainerPage.PageOf(743));
        Assert.Equal(43, ItemContainerPage.SlotWithin(743));
        Assert.Equal(700, ItemContainerPage.FirstSlotOf(7));

        PagedItemContainer container = NewContainer(pageCount: 10, capacity: 1000);
        Assert.Equal(1000, container.SlotSpace);
        container.SetSlotAt(743, Slot(Sword, 1, 11, Level(68)));

        Assert.Same(container.Pages[7], container.PageForSlot(743));
        Assert.True(container.Pages[7].IsDirty);
        Assert.Equal(container.SlotAt(743), container.Pages[7].SlotAt(743));
        Assert.Equal(700, container.Pages[7].FirstSlot);

        // The page addresses the slot by its ABSOLUTE number and the codec writes it relative to the page's
        // first slot, which is what makes the section name arithmetic a human can do in their head.
        Span<PageSlotInput> entries = new PageSlotInput[PageSlots];
        Assert.Equal(1, container.Pages[7].CopyEntriesTo(entries));
        Assert.Equal(743, entries[0].Slot);

        byte[] page = ItemContainerPageCodec.Encode(7, 700, PageSlots, 0, entries[..1]);
        Span<PageEntry> decoded = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out PageHeader header, out _, out _));
        Assert.Equal(7, header.PageIndex);
        Assert.Equal(700, header.FirstSlot);
        Assert.Equal(743, decoded[0].Slot);
    }

    [Fact]
    public void Every_page_declares_the_full_geometry_so_a_30_slot_bag_is_ONE_page_with_capacity_30()
    {
        // The #916 decision. Spec 5.7 makes slot space PageCount times ContainerPageSlots, which is an
        // address space rather than a count of what fits, so spec 5.2's 30 slot bag is one FULL page whose
        // capacity gate is 30. A page declaring fewer slots than the geometry is therefore a codec level
        // anomaly rather than a container this type can build, which is what keeps the page arithmetic of
        // Slot_743_is_page_7_slot_43 true for every container in the fleet.
        PagedItemContainer bag = NewContainer(pageCount: 1, capacity: 30, NeverStacks);

        Assert.Equal(PageSlots, bag.SlotSpace);
        Assert.Equal(PageSlots, bag.Pages[0].SlotCount);

        for (int i = 0; i < 30; i++) Assert.Equal(1, bag.Add(Sword + i, 1));
        Assert.Equal(0, bag.Add(Potion, 1));
        Assert.Equal(30, bag.Occupancy);

        Span<PageSlotInput> entries = new PageSlotInput[PageSlots];
        int count = bag.Pages[0].CopyEntriesTo(entries);
        byte[] page = ItemContainerPageCodec.Encode(0, 0, bag.Pages[0].SlotCount, 0, entries[..count]);

        Span<PageEntry> decoded = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out PageHeader header, out _, out _));
        Assert.Equal(PageSlots, header.SlotCount);
    }

    /// <summary>Whether a type is a content type, which is the whole of <c>KhaozEngine.Catalog</c> and every
    /// generic argument of anything from it.</summary>
    static bool NamesContent(Type type)
    {
        if (type.Assembly == typeof(ItemRow).Assembly) return true;
        foreach (Type argument in type.GetGenericArguments())
            if (NamesContent(argument)) return true;
        return type.HasElementType && NamesContent(type.GetElementType()!);
    }
}
