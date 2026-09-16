using System;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>Why a batch stopped taking operations, spec 6.4's five closers plus the open state.</summary>
public enum ContainerBatchCloseReason
{
    /// <summary>Still taking operations.</summary>
    Open = 0,

    /// <summary>The server tick moved. The window is one tick and never a timer, so nothing durable lives
    /// across a boundary an operator would have to reason about.</summary>
    TickBoundary = 1,

    /// <summary>An operation would touch a container this batch does not hold, which is a second atomic unit
    /// and must not be widened by an unrelated batch.</summary>
    SecondStream = 2,

    /// <summary>An operation sets <c>PresentAtCommit</c>. That is value moving between accounts and it never
    /// shares an identity with anything else.</summary>
    PresentAtCommit = 3,

    /// <summary>A client originated operation arrived. It heads its own batch, because
    /// <c>ResolveOperationAsync</c> is keyed on ONE id and merging two would leave one unresolvable.</summary>
    ClientOperation = 4,

    /// <summary>A journal limit would be exceeded: 128 events, 64 projection writes, the 64 KiB normalized
    /// intent or the 8 MiB aggregate commit.</summary>
    LimitReached = 5,
}

/// <summary>
/// The batch window of spec 6.4: one server tick, closing on the FIRST of five things, and FIRST WINS. Once a
/// reason is recorded it never changes, because the reason is what a caller acts on and a later closer
/// overwriting an earlier one would describe the wrong cause.
/// <para>
/// <b>It decides and it does not count.</b> The totals arrive as arguments from the builder that owns them,
/// so the window has no second copy of a number that could drift from the commit being built.
/// </para>
/// <para>
/// <b>The limit check is a BOUND rather than the exact commit size, and it never underestimates.</b> A page
/// that joins the batch contributes its whole current size and the operation's own growth is counted at the
/// entry it seats, so the window closes at or before the real cap. That is the direction a cap needs, because
/// a commit over the limit is refused by <c>JournalCommit</c> outright.
/// </para>
/// </summary>
public sealed class ContainerBatchWindow
{
    /// <summary>Opens a window on one stream and one tick.</summary>
    /// <param name="streamKey">The stream every operation in this batch writes.</param>
    /// <param name="tick">The server tick the batch belongs to.</param>
    /// <param name="limits">The journal limits the batch is bounded by, the engine maxima by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="streamKey"/> is null.</exception>
    public ContainerBatchWindow(string streamKey, long tick, JournalLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(streamKey);
        StreamKey = streamKey;
        Tick = tick;
        Limits = limits ?? JournalLimits.Maximum;
    }

    /// <summary>The stream this batch writes.</summary>
    public string StreamKey { get; }

    /// <summary>The server tick this batch belongs to.</summary>
    public long Tick { get; }

    /// <summary>The journal limits the batch is bounded by.</summary>
    public JournalLimits Limits { get; }

    /// <summary>Why the window closed, or <see cref="ContainerBatchCloseReason.Open"/> while it is still
    /// taking operations.</summary>
    public ContainerBatchCloseReason CloseReason { get; private set; }

    /// <summary>Whether the window is still taking operations.</summary>
    public bool IsOpen => CloseReason == ContainerBatchCloseReason.Open;

    /// <summary>How many operations have joined.</summary>
    public int OperationCount { get; private set; }

    /// <summary>Whether a client originated operation heads this batch, which is what makes its intent the
    /// client's own operation alone (spec 6.5).</summary>
    public bool HoldsClientOperation { get; private set; }

    /// <summary>Closes the window, keeping the FIRST reason recorded.</summary>
    /// <param name="reason">Why it closed. <see cref="ContainerBatchCloseReason.Open"/> is refused.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is
    /// <see cref="ContainerBatchCloseReason.Open"/>.</exception>
    public void Close(ContainerBatchCloseReason reason)
    {
        if (reason == ContainerBatchCloseReason.Open)
            throw new ArgumentException("Open is not a reason a window closes for.", nameof(reason));
        if (IsOpen) CloseReason = reason;
    }

    /// <summary>
    /// Whether one more operation may join, and what closes the window when it may not. A refusal ALWAYS
    /// closes the window: the operation is the next batch's, and the caller opens one for it.
    /// </summary>
    /// <param name="operation">The operation asking to join, already validated.</param>
    /// <param name="tick">The server tick it happened on.</param>
    /// <param name="holdsContainers">Whether this batch's stream holds every container the operation
    /// touches. False is a second stream.</param>
    /// <param name="addedEvents">Events the operation adds, which is always one.</param>
    /// <param name="addedProjectionWrites">Pages the operation would dirty that are not dirty already.</param>
    /// <param name="currentEvents">Events the batch already holds.</param>
    /// <param name="currentProjectionWrites">Pages the batch already writes.</param>
    /// <param name="projectedIntentBytes">The normalized intent's size once the operation joins.</param>
    /// <param name="projectedCommitBytes">An upper bound on the commit's owned bytes once it joins.</param>
    /// <returns>True when it joined, and the window's counters have moved.</returns>
    public bool TryAdmit(
        in ContainerOperation operation,
        long tick,
        bool holdsContainers,
        int addedEvents,
        int addedProjectionWrites,
        int currentEvents,
        int currentProjectionWrites,
        int projectedIntentBytes,
        int projectedCommitBytes)
    {
        if (!IsOpen) return false;

        // The order here is spec 6.4's own, and the first closer that fires is the one recorded.
        if (tick != Tick)
        {
            Close(ContainerBatchCloseReason.TickBoundary);
            return false;
        }

        if (!holdsContainers)
        {
            Close(ContainerBatchCloseReason.SecondStream);
            return false;
        }

        if (operation.PresentAtCommit && OperationCount > 0)
        {
            Close(ContainerBatchCloseReason.PresentAtCommit);
            return false;
        }

        // A client operation may HEAD a batch of the server work it causes, and never joins one already
        // under way and never meets a second client operation (spec 6.5).
        if (operation.Origin == ContainerOperationOrigin.Client && OperationCount > 0)
        {
            Close(ContainerBatchCloseReason.ClientOperation);
            return false;
        }

        if (currentEvents + addedEvents > Limits.EventsPerOperation
            || currentProjectionWrites + addedProjectionWrites > Limits.ProjectionWritesPerOperation
            || currentProjectionWrites + addedProjectionWrites > Limits.ProjectionSectionsPerStream
            || projectedIntentBytes > Limits.NormalizedIntentBytes
            || projectedCommitBytes > Limits.AggregateCommitBytes)
        {
            Close(ContainerBatchCloseReason.LimitReached);
            return false;
        }

        OperationCount++;
        if (operation.Origin == ContainerOperationOrigin.Client) HoldsClientOperation = true;

        // An operation that must not be presented early is admitted ALONE, so the window closes behind it
        // rather than in front of it.
        if (operation.PresentAtCommit) Close(ContainerBatchCloseReason.PresentAtCommit);
        return true;
    }
}
