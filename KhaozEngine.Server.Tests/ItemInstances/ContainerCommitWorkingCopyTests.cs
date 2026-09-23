using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// The builder reads and writes a container through <see cref="IPagedContainerWorkingCopy"/> and nothing wider,
/// so a host that shares its containers copy on write keeps ownership
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1045).
/// <para>
/// The doubles here are NOT a <see cref="PagedItemContainer"/>: each holds one privately, so the only route the
/// builder has to it is the interface. A builder that reached around it could not build the same commit.
/// </para>
/// </summary>
public sealed class ContainerCommitWorkingCopyTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>A working copy that records every member it is reached through, and every write, BEFORE
    /// forwarding to the container only it holds, so a member that throws is still on the record.</summary>
    sealed class RecordingWorkingCopy(PagedItemContainer inner) : IPagedContainerWorkingCopy
    {
        public List<string> Writes { get; } = new();

        /// <summary>Every member reached, by name and in order, reads and writes alike.</summary>
        public List<string> Reached { get; } = new();

        public int Reads { get; private set; }

        public PagedItemContainer Inner => inner;

        public int PageCount => Read(nameof(PageCount), () => inner.PageCount);

        public Func<int, bool> Stackable => Read(nameof(Stackable), () => inner.Stackable);

        public bool IsAtCapacity => Read(nameof(IsAtCapacity), () => inner.IsAtCapacity);

        public ItemSlot SlotAt(int containerSlot) => Read(nameof(SlotAt), () => inner.SlotAt(containerSlot));

        public bool SetSlotAt(int containerSlot, ItemSlot value)
        {
            Write(nameof(SetSlotAt), FormattableString.Invariant($"set {containerSlot}"));
            return inner.SetSlotAt(containerSlot, value);
        }

        public ItemSlot TakeSlotAt(int containerSlot)
        {
            Write(nameof(TakeSlotAt), FormattableString.Invariant($"take {containerSlot}"));
            return inner.TakeSlotAt(containerSlot);
        }

        public bool IsPageDirty(int pageIndex) => Read(nameof(IsPageDirty), () => inner.Pages[pageIndex].IsDirty);

        public int PageContentVersion(int pageIndex) =>
            Read(nameof(PageContentVersion), () => inner.Pages[pageIndex].ContentVersion);

        public int CopyPageEntriesTo(int pageIndex, Span<PageSlotInput> destination)
        {
            Reached.Add(nameof(CopyPageEntriesTo));
            Reads++;
            return inner.Pages[pageIndex].CopyEntriesTo(destination);
        }

        public void MarkClean()
        {
            Write(nameof(MarkClean), "mark-clean");
            inner.MarkClean();
        }

        T Read<T>(string member, Func<T> read)
        {
            Reached.Add(member);
            Reads++;
            return read();
        }

        void Write(string member, string write)
        {
            Reached.Add(member);
            Writes.Add(write);
        }
    }

    /// <summary>
    /// A host's copy on write wrapper: it reads a SHARED container until the first write, copies once, and
    /// writes its own copy from then on. The copy seats every entry and stamp through the load doors, which is
    /// faithful here because the shared view starts clean (a dirty one is #1028's problem, not this one's).
    /// </summary>
    sealed class CopyOnWriteWorkingCopy(PagedItemContainer shared) : IPagedContainerWorkingCopy
    {
        PagedItemContainer? _own;

        public int Copies { get; private set; }

        /// <summary>How many times <see cref="MarkClean"/> was called, which is a write and so takes ownership.</summary>
        public int Cleans { get; private set; }

        public PagedItemContainer Current => _own ?? shared;

        public int PageCount => Current.PageCount;

        public Func<int, bool> Stackable => Current.Stackable;

        public bool IsAtCapacity => Current.IsAtCapacity;

        public ItemSlot SlotAt(int containerSlot) => Current.SlotAt(containerSlot);

        public bool SetSlotAt(int containerSlot, ItemSlot value) => Own().SetSlotAt(containerSlot, value);

        public ItemSlot TakeSlotAt(int containerSlot) => Own().TakeSlotAt(containerSlot);

        public bool IsPageDirty(int pageIndex) => Current.Pages[pageIndex].IsDirty;

        public int PageContentVersion(int pageIndex) => Current.Pages[pageIndex].ContentVersion;

        public int CopyPageEntriesTo(int pageIndex, Span<PageSlotInput> destination) =>
            Current.Pages[pageIndex].CopyEntriesTo(destination);

        public void MarkClean()
        {
            Cleans++;
            Own().MarkClean();
        }

        PagedItemContainer Own()
        {
            if (_own is not null) return _own;

            PagedItemContainer copy = Container(shared.PageCount, shared.Capacity);
            for (int page = 0; page < shared.PageCount; page++)
            {
                copy.Pages[page].SeatStamp(shared.Pages[page].ContentVersion);
                for (int slot = page * PageSlots; slot < (page + 1) * PageSlots; slot++)
                    if (!shared.SlotAt(slot).IsEmpty) copy.Seat(slot, shared.SlotAt(slot));
            }

            Copies++;
            return _own = copy;
        }
    }

    static PagedItemContainer SeededBank()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        SeatStack(bank, 5, Currency, 20);
        SeatStack(bank, 6, Potion, 9);
        return bank;
    }

    static void ApplyWork(ContainerCommitBuilder batch)
    {
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bank, 150, 1, Instance)));
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 6, 9)));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 7, Potion, 3)));
        Assert.True(batch.Apply(ContainerOperation.Craft(
            Bank, 150, Instance, Payload(7), CraftEventBody(0), currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1)));
    }

    static ContainerCommitBuilder OpenOver(IPagedContainerWorkingCopy copy, long tick = 4)
        => ContainerCommitBuilder.Open(
            StreamKey,
            ItemInstanceEvents.CraftActionKind,
            Scope,
            new Dictionary<string, IPagedContainerWorkingCopy> { [Bank] = copy },
            tick);

    static byte[] Fingerprint(JournalCommit commit) =>
        JournalCanonicalizer.CreateCommitFingerprint(commit).CanonicalBytes.ToArray();

    [Fact]
    public void The_builder_writes_only_through_the_working_copy_and_builds_the_same_commit()
    {
        PagedItemContainer plain = SeededBank();
        ContainerCommitBuilder direct = OpenBank(plain);
        ApplyWork(direct);
        JournalCommit expected = direct.Close(Mint(ServerId));

        var recording = new RecordingWorkingCopy(SeededBank());
        ContainerCommitBuilder batch = OpenOver(recording);
        ApplyWork(batch);
        JournalCommit actual = batch.Close(Mint(ServerId));

        // The same commit, byte for byte, from a builder that could reach the container ONLY through the
        // interface, so everything it read and wrote went through it.
        Assert.Equal(Fingerprint(expected), Fingerprint(actual));
        Assert.True(recording.Reads > 0);

        // And the writes are exactly the ones the four operations imply, in order: the move takes the sword
        // and seats it, the take empties the potions, the grant opens a slot, and the craft rewrites the
        // sword and pays one currency.
        Assert.Equal(new[] { "take 4", "set 150", "take 6", "set 7", "set 150", "set 5" }, recording.Writes);

        for (int slot = 0; slot < plain.SlotSpace; slot++)
            Assert.Equal(plain.SlotAt(slot), recording.Inner.SlotAt(slot));

        batch.MarkCommitted();
        Assert.Equal("mark-clean", recording.Writes[^1]);
        Assert.Equal(0, recording.Inner.DirtyPageCount);
    }

    [Fact]
    public void Opening_measuring_and_a_refused_operation_write_nothing()
    {
        // A batch reads the dirty pages at Open and on every Apply for its window arithmetic, and none of that
        // is a write, so a copy on write host keeps sharing until an operation actually joins.
        var recording = new RecordingWorkingCopy(SeededBank());
        ContainerCommitBuilder batch = OpenOver(recording, tick: 4);
        Assert.Equal(0, batch.ProjectionWriteCount);

        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 6, 1), tick: 5));
        Assert.Equal(ContainerBatchCloseReason.TickBoundary, batch.Window.CloseReason);

        // A slot outside the address space is refused by the builder's own range check before the working copy
        // is reached with it. The double's own page read throws the same exception type, so the exception
        // alone proves nothing: what proves the check is that nothing but PageCount, the check's own read, was
        // reached at all.
        ContainerCommitBuilder second = OpenOver(recording);
        recording.Reached.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => second.Apply(ContainerOperation.Take(Bank, 2 * PageSlots, 1)));
        Assert.True(second.Window.IsOpen);
        Assert.All(recording.Reached, member => Assert.Equal(nameof(IPagedContainerWorkingCopy.PageCount), member));

        Assert.Empty(recording.Writes);
    }

    [Fact]
    public void A_copy_on_write_host_shares_until_the_first_write_and_copies_once()
    {
        // The Grimhollow shape: the committed view is shared, and a builder over the host's own working copy
        // makes it copy ONCE, on the first write, rather than handing the builder a write door and deep
        // copying on every clone from then on.
        PagedItemContainer shared = SeededBank();
        var copy = new CopyOnWriteWorkingCopy(shared);
        ContainerCommitBuilder batch = OpenOver(copy);
        Assert.Equal(0, batch.ProjectionWriteCount);
        Assert.Equal(0, copy.Copies);

        ApplyWork(batch);
        Assert.Equal(1, copy.Copies);
        Assert.NotSame(shared, copy.Current);

        JournalCommit commit = batch.Close(Mint(ServerId));
        batch.MarkCommitted();
        Assert.Equal(1, copy.Copies);
        Assert.Equal(2, commit.ProjectionWrites.Count);

        // The shared view is exactly as it was: nothing written, nothing dirtied.
        PagedItemContainer untouched = SeededBank();
        for (int slot = 0; slot < shared.SlotSpace; slot++)
            Assert.Equal(untouched.SlotAt(slot), shared.SlotAt(slot));
        Assert.Equal(0, shared.DirtyPageCount);
        Assert.Equal(0, copy.Current.DirtyPageCount);

        // And the host's copy holds what a builder over the plain container would have left.
        PagedItemContainer plain = SeededBank();
        ContainerCommitBuilder direct = OpenBank(plain);
        ApplyWork(direct);
        Assert.Equal(Fingerprint(direct.Close(Mint(ServerId))), Fingerprint(commit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_container_the_batch_never_dirtied_is_never_copied_or_cleaned(bool takeParts)
    {
        // A stream holding a bag and a bank, and a click that touches only the bag. MarkClean is a write, so a
        // copy on write host takes ownership in it, and a batch that cleaned every container it was opened
        // over deep copied the bank on every commit to clear flags it never set.
        var bag = new CopyOnWriteWorkingCopy(SeededBank());
        var bank = new CopyOnWriteWorkingCopy(SeededBank());
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(
            StreamKey,
            ItemInstanceEvents.CraftActionKind,
            Scope,
            new Dictionary<string, IPagedContainerWorkingCopy> { [Bag] = bag, [Bank] = bank },
            tick: 4);
        AssertUntouched(bank);

        Assert.True(batch.Apply(ContainerOperation.Take(Bag, 6, 9)));
        Assert.Equal(1, bag.Copies);
        AssertUntouched(bank);

        IReadOnlyList<JournalProjectionWrite> writes;
        if (takeParts) Assert.True(batch.TryBuildParts(out _, out writes));
        else writes = batch.Close(Mint(ServerId)).ProjectionWrites;
        Assert.Equal(ContainerSectionNames.Format(Bag, 0), Assert.Single(writes).SectionName);
        AssertUntouched(bank);

        batch.MarkCommitted();
        AssertUntouched(bank);
        Assert.Equal(1, bag.Copies);
        Assert.Equal(1, bag.Cleans);
        Assert.Equal(0, bag.Current.DirtyPageCount);
    }

    static void AssertUntouched(CopyOnWriteWorkingCopy copy)
    {
        Assert.Equal(0, copy.Copies);
        Assert.Equal(0, copy.Cleans);
    }
}
