using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Spec 6.4's batch: N logical operations applied to an in-memory working copy, emitted as ONE
/// <see cref="JournalCommit"/> with ONE operation identity, ONE event per operation in order, and ONE
/// projection write per page the batch leaves dirty.
/// <para>
/// <b>The audit trail is not collapsed, only the projection is.</b> Twenty crafts in one held action are
/// twenty <c>item-crafted</c> events and one page write, which is budget 4 and the whole point of the
/// section: 1,380 KB and twenty commits become 9.4 KB and one.
/// </para>
/// <para>
/// <b>This is option A of spec 6.2, and it changes NOTHING in the journal.</b> One identity per commit is
/// what <c>JournalCommit</c> already takes, a commit already carries several sections of one stream, and the
/// store, both provider schemas and the store conformance suite are untouched. Option B, merging identities
/// inside the executor, is what would have needed all three, and spec 6.3 prices it so the owner can choose
/// it knowingly.
/// </para>
/// <para>
/// <b>The dirty set is the page list, so a lazy remap rewrite rides the batch for free.</b> Close writes
/// every page the containers report dirty, which is the pages the operations changed plus the pages a load
/// time remap changed. That is what makes the lazy rewrite cost nothing: it never CAUSES a commit, it only
/// joins one.
/// </para>
/// <para>
/// <b>Close does not clear the dirty flags and <see cref="MarkCommitted"/> does.</b> A batch that dies before
/// its commit lands, or whose commit fails terminally, leaves the pages owing the next commit a rewrite,
/// which is exactly the state the consumer resyncs from.
/// </para>
/// <para>
/// <b>The batch owns what it is opened over, from <c>Open</c> until <see cref="MarkCommitted"/>.</b> It holds
/// each container by reference and every <c>Apply</c> writes through it. It reads and writes through
/// <see cref="IPagedContainerWorkingCopy"/> and nothing wider, so a host that shares its containers copy on
/// write opens the batch over its own implementation, runs its ownership check inside the three write
/// members, and keeps every read shared
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1045">#1045</see>). <see cref="MarkCommitted"/>
/// calls <c>MarkClean</c> only on a container holding a dirty page, so a container the batch left clean is
/// never written at all.
/// </para>
/// <para>
/// Nothing here is thread safe, exactly like the containers it edits: one owner, one tick, one batch.
/// </para>
/// </summary>
public sealed partial class ContainerCommitBuilder
{
    readonly Dictionary<string, IPagedContainerWorkingCopy> _containers;
    readonly string[] _names;
    readonly List<ContainerOperation> _operations = new();
    readonly List<JournalEvent> _events = new();
    readonly PageSlotInput[] _entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
    int _eventBytes;
    int _pageBytes;
    bool _presentAtCommit;
    bool _closed;
    bool _faulted;

    ContainerCommitBuilder(
        string streamKey,
        string actionKind,
        string scope,
        Dictionary<string, IPagedContainerWorkingCopy> containers,
        string[] names,
        long tick,
        ContainerCommitOptions options)
    {
        StreamKey = streamKey;
        ActionKind = actionKind;
        Scope = scope;
        Options = options;
        _containers = containers;
        _names = names;
        Window = new ContainerBatchWindow(streamKey, tick, options.Limits);
        _pageBytes = MeasureDirtyPages();
    }

    /// <summary>The stream every operation in this batch writes.</summary>
    public string StreamKey { get; }

    /// <summary>The action kind the commit is admitted under, a durable string.</summary>
    public string ActionKind { get; }

    /// <summary>The authenticated scope the commit is admitted under.</summary>
    public string Scope { get; }

    /// <summary>The journal facts this batch was opened with.</summary>
    public ContainerCommitOptions Options { get; }

    /// <summary>The window and its five closers.</summary>
    public ContainerBatchWindow Window { get; }

    /// <summary>The operations that joined, in the order they were applied, which is the order their events
    /// are written in.</summary>
    public IReadOnlyList<ContainerOperation> Operations => _operations;

    /// <summary>How many events the commit will carry, which is one per operation.</summary>
    public int EventCount => _events.Count;

    /// <summary>How many projection writes the commit will carry, which is every page the containers report
    /// dirty right now.</summary>
    public int ProjectionWriteCount
    {
        get
        {
            int dirty = 0;
            foreach (string name in _names) dirty += DirtyPageCount(_containers[name]);
            return dirty;
        }
    }

    /// <summary>
    /// Opens a batch on one stream, over the containers that stream holds, as the containers themselves. The
    /// batch owns them until <see cref="MarkCommitted"/>. A host that shares its containers copy on write
    /// opens over its own <see cref="IPagedContainerWorkingCopy"/> instead.
    /// </summary>
    /// <param name="streamKey">The stream. Every page this batch writes is a section of it.</param>
    /// <param name="actionKind">The durable action kind, <c>ItemInstanceEvents.CraftActionKind</c> for a
    /// held craft.</param>
    /// <param name="scope">The authenticated scope the operation is admitted under.</param>
    /// <param name="containers">The stream's containers by name, which is the name their sections are filed
    /// under.</param>
    /// <param name="tick">The server tick this batch belongs to. The window is one tick.</param>
    /// <param name="options">The journal facts of <see cref="ContainerCommitOptions"/>, defaulted when
    /// null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="containers"/> is empty or holds a null container
    /// or a name the section scheme refuses.</exception>
    public static ContainerCommitBuilder Open(
        string streamKey,
        string actionKind,
        string scope,
        IReadOnlyDictionary<string, PagedItemContainer> containers,
        long tick = 0,
        ContainerCommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var doors = new Dictionary<string, IPagedContainerWorkingCopy>(containers.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, PagedItemContainer> pair in containers) doors.Add(pair.Key, pair.Value);
        return Open(streamKey, actionKind, scope, doors, tick, options);
    }

    /// <summary>
    /// Opens a batch on one stream, over the working copies of the containers that stream holds. Every read and
    /// every write the batch makes goes through <see cref="IPagedContainerWorkingCopy"/>, and the batch holds
    /// each one by reference from here until <see cref="MarkCommitted"/>.
    /// </summary>
    /// <param name="streamKey">The stream. Every page this batch writes is a section of it.</param>
    /// <param name="actionKind">The durable action kind, <c>ItemInstanceEvents.CraftActionKind</c> for a
    /// held craft.</param>
    /// <param name="scope">The authenticated scope the operation is admitted under.</param>
    /// <param name="containers">The stream's containers by name, which is the name their sections are filed
    /// under. An operation naming a container outside this set is an operation on a SECOND stream: this
    /// batch has no working copy for it and cannot widen itself to reach it.</param>
    /// <param name="tick">The server tick this batch belongs to. The window is one tick.</param>
    /// <param name="options">The journal facts of <see cref="ContainerCommitOptions"/>, defaulted when
    /// null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="containers"/> is empty or holds a null container
    /// or a name the section scheme refuses.</exception>
    public static ContainerCommitBuilder Open(
        string streamKey,
        string actionKind,
        string scope,
        IReadOnlyDictionary<string, IPagedContainerWorkingCopy> containers,
        long tick = 0,
        ContainerCommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(streamKey);
        ArgumentNullException.ThrowIfNull(actionKind);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(containers);
        if (containers.Count == 0)
            throw new ArgumentException("A batch is opened over at least one container.", nameof(containers));

        var copy = new Dictionary<string, IPagedContainerWorkingCopy>(containers.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, IPagedContainerWorkingCopy> pair in containers)
        {
            if (pair.Value is null)
                throw new ArgumentException($"Container '{pair.Key}' is null.", nameof(containers));

            // Format is the one place a section name is made, so asking it here means a batch cannot be
            // opened over a name that would only fail at Close.
            _ = ContainerSectionNames.Format(pair.Key, 0);
            copy.Add(pair.Key, pair.Value);
        }

        string[] names = new string[copy.Count];
        copy.Keys.CopyTo(names, 0);
        Array.Sort(names, StringComparer.Ordinal);
        return new ContainerCommitBuilder(
            streamKey, actionKind, scope, copy, names, tick, options ?? new ContainerCommitOptions());
    }

    /// <summary>Applies one operation on this batch's own tick.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>True when it joined. False means the window closed and the operation belongs to the NEXT
    /// batch, which the caller opens for it. <see cref="ContainerBatchWindow.CloseReason"/> says why.</returns>
    public bool Apply(in ContainerOperation operation) => Apply(operation, Window.Tick);

    /// <summary>
    /// Applies one operation against the working copy, or refuses it and closes the window.
    /// <para>
    /// A refused operation changes NOTHING: the working copy is untouched, no event is written, and the
    /// caller opens the next batch for it. An operation the working copy cannot perform is a different thing
    /// entirely and THROWS, because a game refuses an illegal action before the journal ever sees it (spec
    /// 10.6), so reaching this door with one is a caller bug. A batch that threw is abandoned.
    /// </para>
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="tick">The server tick it happened on. A different tick closes the window.</param>
    /// <returns>True when it joined.</returns>
    /// <exception cref="InvalidOperationException">The batch is closed or was abandoned after a throw.</exception>
    /// <exception cref="ArgumentException">The operation is malformed, or the working copy cannot perform
    /// it.</exception>
    public bool Apply(in ContainerOperation operation, long tick)
    {
        ThrowIfEnded();
        operation.Validate();
        if (!Window.IsOpen) return false;

        bool holdsContainers = _containers.ContainsKey(operation.Container)
            && _containers.ContainsKey(operation.DestinationContainerOrOwn);
        int addedWrites = 0;
        int projectedIntent = 0;
        int projectedCommit = 0;
        if (holdsContainers)
        {
            addedWrites = CountUndirtiedPages(operation);
            projectedIntent = ProjectedIntentBytes(operation);
            projectedCommit = ProjectedCommitBytes(operation, projectedIntent);
        }

        if (!Window.TryAdmit(
            operation,
            tick,
            holdsContainers,
            addedEvents: 1,
            addedWrites,
            _events.Count,
            ProjectionWriteCount,
            projectedIntent,
            projectedCommit))
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = ApplyToWorkingCopy(operation);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _operations.Add(operation);
        _events.Add(new JournalEvent(operation.EventType, 1, payload));
        _eventBytes += payload.Length;
        _pageBytes = MeasureDirtyPages();
        return true;
    }

    /// <summary>
    /// Closes the batch and builds its commit: ONE identity, ONE event per operation in order, ONE projection
    /// write per dirty page, ONE result. It is the convenience over <see cref="TryBuildParts"/> for a commit
    /// that carries this batch and nothing else: the same events and the same projection writes, under an
    /// identity and a result, validated against <see cref="ContainerCommitOptions.Limits"/>.
    /// <para>
    /// <b>Whose identity it is decides what the intent holds</b> (spec 6.5). A SERVER minted batch carries the
    /// canonical ORDERED operation list, and its id comes from <paramref name="identityFactory"/>. A CLIENT
    /// headed batch carries the client operation's own canonical intent ALONE, under the client's own id, and
    /// the server work riding behind it contributes NO intent bytes: it is carried by the events and by the
    /// page bytes. That is what makes a resubmit which omits the server work hash identically and resolve
    /// <c>Replayed</c> rather than <c>OperationConflict</c>, which matters because a conflict would tell the
    /// player a COMMITTED action failed and a re-click would apply it twice.
    /// </para>
    /// </summary>
    /// <param name="identityFactory">Mints a server batch's durable operation id. It is not called for a
    /// client headed batch, which already has one.</param>
    /// <param name="result">The resolved state the consumer presents.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identityFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The batch is already closed, was abandoned, or holds no
    /// operation. A batch of nothing has nothing to commit, and a dirty page never causes a commit of its
    /// own: it only joins one.</exception>
    /// <remarks>
    /// The batch is flagged closed and the window is closed LAST, after the commit is built and validated, so
    /// a throw on the way there leaves the batch exactly where it was: still open, pages still dirty, and
    /// closable again once the caller has fixed what threw.
    /// </remarks>
    public JournalCommit Close(Func<Guid> identityFactory, ReadOnlyMemory<byte> result = default)
    {
        ArgumentNullException.ThrowIfNull(identityFactory);
        if (!TryCollectParts(out JournalEvent[] events, out JournalProjectionWrite[] projectionWrites))
            throw new InvalidOperationException("A batch holding no operation has nothing to commit.");

        Guid operationId = Window.HoldsClientOperation ? _operations[0].OperationId : identityFactory();
        var identity = new JournalOperationIdentity(operationId, Scope, ActionKind, BuildIntent());
        var commit = new JournalCommit(
            identity,
            new[] { new JournalStreamMutation(StreamKey, Options.ExpectedVersion, events) },
            projectionWrites,
            Options.ResultSchema,
            Options.ResultSchemaVersion,
            result.ToArray(),
            _presentAtCommit,
            Options.QueueBehindAdmitted);
        commit.Validate(Options.Limits);

        // Recorded LAST, once there is a commit to show for it. Flagging the batch closed first left a throw
        // out of the writes holding a batch that could never be closed again, with its pages still dirty and
        // no fault flag, so the caller held something that had committed nothing and could do nothing.
        End();
        return commit;
    }

    /// <summary>
    /// Clears every page's dirty flag, which the batch owes them once its commit has LANDED. It calls
    /// <see cref="IPagedContainerWorkingCopy.MarkClean"/> on each container holding a dirty page and on no
    /// other, because the call is a write: a copy on write host takes ownership in it, and a bank the batch
    /// never dirtied would be copied to clear flags it never had. Nothing calls it for you: a commit that failed
    /// terminally leaves the pages dirty on purpose, so the next ordinary commit carries them again and the
    /// consumer's resync has something to agree with.
    /// </summary>
    public void MarkCommitted()
    {
        foreach (string name in _names)
        {
            IPagedContainerWorkingCopy container = _containers[name];
            if (DirtyPageCount(container) != 0) container.MarkClean();
        }
    }

    /// <summary>
    /// The normalized intent this batch would hash under, which is spec 6.5's split written out:
    /// <c>[Count: varint][ per operation: [Kind: varint][Parameters] ]</c> for a server minted batch, and the
    /// head operation's own canonical encoding ALONE for a client headed one.
    /// </summary>
    public byte[] BuildIntent()
    {
        if (Window.HoldsClientOperation) return _operations[0].ToCanonicalArray();

        int size = ContentVarint.Size((uint)_operations.Count);
        foreach (ContainerOperation operation in _operations) size += operation.CanonicalByteCount;

        byte[] intent = new byte[size];
        int written = ContentVarint.Write(intent, (uint)_operations.Count);
        foreach (ContainerOperation operation in _operations)
            written += operation.WriteCanonical(intent.AsSpan(written));
        return intent;
    }

    int MeasureDirtyPages()
    {
        int bytes = 0;
        foreach (string name in _names)
        {
            IPagedContainerWorkingCopy container = _containers[name];
            for (int page = 0; page < container.PageCount; page++)
                if (container.IsPageDirty(page)) bytes += MeasurePage(container, page);
        }

        return bytes;
    }

    int MeasurePage(IPagedContainerWorkingCopy container, int pageIndex)
    {
        int count = container.CopyPageEntriesTo(pageIndex, _entries);
        return ItemContainerPageCodec.EncodedSize(
            pageIndex,
            ItemContainerPage.FirstSlotOf(pageIndex),
            ItemContainerPageCodec.ContainerPageSlots,
            container.PageContentVersion(pageIndex),
            _entries.AsSpan(0, count));
    }

    static int DirtyPageCount(IPagedContainerWorkingCopy container)
    {
        int dirty = 0;
        for (int page = 0; page < container.PageCount; page++)
            if (container.IsPageDirty(page)) dirty++;
        return dirty;
    }
}
