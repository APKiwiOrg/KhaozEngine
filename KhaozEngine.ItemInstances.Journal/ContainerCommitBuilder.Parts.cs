using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The parts half: what this batch contributes to a commit, handed out whole so a host can compose it into a
/// commit that carries more than this batch (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1044">#1044</see>).
/// <para>
/// <b>A real host's commits are rarely container only.</b> A loot claim carries the loot source's stream beside
/// the bag, a harvest carries skill experience, and an ordinary click can carry coins or quest state. Such a
/// commit is ONE identity over several streams, and <see cref="Close"/> cannot build it, because it answers a
/// whole <see cref="JournalCommit"/> holding this batch alone.
/// </para>
/// <para>
/// <b>The limits are checked on the commit, never on a part.</b> A part is not a commit and cannot know what
/// joins it, so <see cref="TryBuildParts"/> validates no total. The window still bounds THIS batch's share as
/// operations join, against <see cref="ContainerCommitOptions.Limits"/>. The composed commit is checked on its
/// REAL total where it becomes whole: its own constructor holds it to the engine maxima, the host calls
/// <see cref="JournalCommit.Validate"/> with <c>Options.Limits</c> exactly as <see cref="Close"/> does, and the
/// store validates it again against its own limits on submission. A host that composes reserves room for what
/// it adds by opening the batch with those limits lowered by that much, because the window closes on the
/// batch's share and only the composed commit sees the whole.
/// </para>
/// </summary>
public sealed partial class ContainerCommitBuilder
{
    /// <summary>Whether an operation that joined sets <c>PresentAtCommit</c>, which a composed commit carries
    /// exactly as <see cref="Close"/> does, because value moving between accounts must not be presented
    /// before its commit completes whatever else rides with it.</summary>
    public bool PresentAtCommit => _presentAtCommit;

    /// <summary>
    /// Takes this batch's parts for a commit the caller composes, and closes the batch exactly as
    /// <see cref="Close"/> does.
    /// <para>
    /// The events are ONE per operation in order and are the events of <see cref="StreamKey"/> at
    /// <see cref="ContainerCommitOptions.ExpectedVersion"/>: they join whatever else the composed commit writes to
    /// that stream, in ONE <see cref="JournalStreamMutation"/>, because a commit names a stream once. The
    /// projection writes are ONE per dirty page. Everything else a commit needs from this batch is already
    /// public: <see cref="BuildIntent"/>, <see cref="PresentAtCommit"/>, and the identity rule of
    /// <see cref="Close"/>, under which a batch a client operation heads (<see cref="ContainerBatchWindow.HoldsClientOperation"/>)
    /// commits under that operation's own id and intent so a resubmit still resolves replayed.
    /// </para>
    /// <para>
    /// Nothing here checks a limit on a total, and the type doc says why. <see cref="MarkCommitted"/> is owed
    /// once the COMPOSED commit has landed, exactly as it is after <see cref="Close"/>.
    /// </para>
    /// </summary>
    /// <param name="events">One event per operation in order, or empty when the answer is false.</param>
    /// <param name="projectionWrites">One write per dirty page, or empty when the answer is false.</param>
    /// <returns>True when the parts were taken and the batch is closed. False when the batch holds no
    /// operation: it has nothing to contribute, a dirty page never causes a commit of its own, and the batch is
    /// left exactly as it was.</returns>
    /// <exception cref="InvalidOperationException">The batch is already closed or was abandoned.</exception>
    /// <exception cref="ArgumentException">A dirty page cannot be named as a section. Nothing is recorded, so
    /// the batch is still open, exactly as a <see cref="Close"/> that throws leaves it.</exception>
    public bool TryBuildParts(
        out IReadOnlyList<JournalEvent> events,
        out IReadOnlyList<JournalProjectionWrite> projectionWrites)
    {
        if (!TryCollectParts(out JournalEvent[] collected, out JournalProjectionWrite[] writes))
        {
            events = Array.Empty<JournalEvent>();
            projectionWrites = Array.Empty<JournalProjectionWrite>();
            return false;
        }

        End();
        events = collected;
        projectionWrites = writes;
        return true;
    }

    /// <summary>The one place the parts are built, which <see cref="Close"/> and <see cref="TryBuildParts"/>
    /// both take them from. It records nothing, so a throw leaves the batch where it was.</summary>
    bool TryCollectParts(out JournalEvent[] events, out JournalProjectionWrite[] projectionWrites)
    {
        ThrowIfEnded();
        if (_operations.Count == 0)
        {
            events = Array.Empty<JournalEvent>();
            projectionWrites = Array.Empty<JournalProjectionWrite>();
            return false;
        }

        projectionWrites = BuildProjectionWrites();
        events = _events.ToArray();
        return true;
    }

    void ThrowIfEnded()
    {
        if (_closed) throw new InvalidOperationException("This batch is closed.");
        if (_faulted) throw new InvalidOperationException("This batch was abandoned when an operation threw.");
    }

    void End()
    {
        _closed = true;
        Window.Close(ContainerBatchCloseReason.Closed);
    }

    JournalProjectionWrite[] BuildProjectionWrites()
    {
        var writes = new List<JournalProjectionWrite>(ProjectionWriteCount);
        foreach (string name in _names)
        {
            IPagedContainerWorkingCopy container = _containers[name];
            for (int page = 0; page < container.PageCount; page++)
            {
                if (!container.IsPageDirty(page)) continue;

                writes.Add(new JournalProjectionWrite(
                    StreamKey,
                    ContainerSectionNames.Format(name, page),
                    Options.ProjectionSchema,
                    Options.ProjectionSchemaVersion,
                    EncodePage(container, page)));
            }
        }

        return writes.ToArray();
    }

    /// <summary>The STORED page, through the raw encoder, because a projection section is read back by the
    /// server and never by a viewer.</summary>
    byte[] EncodePage(IPagedContainerWorkingCopy container, int pageIndex)
    {
        int count = container.CopyPageEntriesTo(pageIndex, _entries);
        return ItemContainerPageCodec.Encode(
            pageIndex,
            ItemContainerPage.FirstSlotOf(pageIndex),
            ItemContainerPageCodec.ContainerPageSlots,
            container.PageContentVersion(pageIndex),
            _entries.AsSpan(0, count));
    }
}
