using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal sealed partial class FrameDiagnosticsRecorder
{
    readonly bool _ringWaitProbe;
    internal static bool ParseRecordingWaitProbe(string? value) => value switch
    {
        null or "0" => false,
        "1" => true,
        _ => throw new ArgumentException("KE_RING_WAIT_PROBE must be unset, 0 or 1."),
    };

    internal RecordingWaitProbeRequest NextRecordingWaitRequest => _ringWaitProbe && _phase == Phase.Sampling
        ? new(_shadowEpoch, _shadowSequence, true, _shadowMode) : default;

    internal RecordingWaitBoundary ReadRecordingWaitBoundary(IGpuRingWaitSource? source)
    {
        if (!_ringWaitProbe || _phase is not (Phase.Armed or Phase.Sampling)) return default;
        if (source is null) return new(RecordingWaitBoundaryStatus.Unsupported, null);
        try
        {
            var status = source.TryReadRingWaitTotals(out var totals);
            return status switch
            {
                GpuRingWaitSnapshotStatus.Available => new(RecordingWaitBoundaryStatus.Available, totals),
                GpuRingWaitSnapshotStatus.Contended => new(RecordingWaitBoundaryStatus.Contended, null),
                _ => new(RecordingWaitBoundaryStatus.Unavailable, null),
            };
        }
        catch { return new(RecordingWaitBoundaryStatus.Fault, null); }
    }

    RecordingWaitProbeRow RecordingWaitRow(in RecordingWaitCallbackResult callback, in FrameDiagnosticsBoundary boundary)
    {
        var request = NextRecordingWaitRequest;
        long expected = callback.PrefixExpected ? 1 : 0;
        bool paired = boundary.Window.PreparePhaseSubmissionCount - _previous.Window.PreparePhaseSubmissionCount == expected
            && boundary.Window.PreparePhaseCompositeCount - _previous.Window.PreparePhaseCompositeCount == expected
            && boundary.Window.PreparePhaseSplitActive == _previous.Window.PreparePhaseSplitActive;
        var status = RecordingWaitProbeAccounting.Validate(request, callback, _previous.Window.RenderSuppressed, paired);
        if (status == RecordingWaitProbeStatus.Valid && (callback.Main.Receipt!.Value.Frequency != _frequency
            || callback.PrefixExpected != _previous.Window.PreparePhaseSplitActive)) status = RecordingWaitProbeStatus.ModeChanged;
        return new(request, status, callback, _previous.RecordingWait, boundary.RecordingWait,
            RecordingWaitProbeAccounting.Reconcile(status, callback, _previous.RecordingWait, boundary.RecordingWait));
    }
}

internal static class RecordingWaitProbeAccounting
{
    internal static RecordingWaitProbeStatus Validate(RecordingWaitProbeRequest request,
        in RecordingWaitCallbackResult callback, bool suppressed, bool paired = true)
    {
        if (!request.Enabled) return RecordingWaitProbeStatus.Off;
        if (suppressed) return RecordingWaitProbeStatus.NotRendered;
        if (callback.AcknowledgementCount == 0) return RecordingWaitProbeStatus.IncompleteCallback;
        if (callback.AcknowledgementCount != 1) return RecordingWaitProbeStatus.AcknowledgementCardinality;
        if (callback.Request.Mode != request.Mode) return RecordingWaitProbeStatus.ModeChanged;
        if (callback.Request != request || request.Epoch == Guid.Empty || request.Sequence < 1 || callback.IdentityViolation)
            return RecordingWaitProbeStatus.IdentityMismatch;
        if (callback.DeclarationCount > 1) return RecordingWaitProbeStatus.DuplicateDeclaration;
        if (callback.DeclarationCount != 1) return RecordingWaitProbeStatus.MissingDeclaration;
        int prefix = callback.PrefixExpected ? 1 : 0;
        if (callback.PrefixObservations > prefix || callback.MainObservations > 1) return RecordingWaitProbeStatus.DuplicateRole;
        if (callback.PrefixObservations != prefix || callback.MainObservations != 1) return RecordingWaitProbeStatus.MissingRole;
        if (callback.Main.Status == RecordingWaitObservationStatus.Fault || callback.Prefix.Status == RecordingWaitObservationStatus.Fault)
            return RecordingWaitProbeStatus.Fault;
        if (callback.Main.Status != RecordingWaitObservationStatus.Available
            || (callback.PrefixExpected && callback.Prefix.Status != RecordingWaitObservationStatus.Available))
            return RecordingWaitProbeStatus.Unavailable;
        if (callback.Main.Receipt is not { } main || !ValidReceipt(main, callback.Main.PriorAttempt))
            return RecordingWaitProbeStatus.InvalidReceipt;
        if (callback.PrefixExpected)
        {
            if (callback.Prefix.Receipt is not { } p || !ValidReceipt(p, callback.Prefix.PriorAttempt)
                || !Compatible(main, p) || main.ListIdentity == p.ListIdentity || main.RecordingOrdinal == p.RecordingOrdinal)
                return RecordingWaitProbeStatus.InvalidReceipt;
        }
        else if (callback.Prefix.Status != RecordingWaitObservationStatus.NotApplicable || callback.Prefix.Receipt.HasValue)
            return RecordingWaitProbeStatus.InvalidReceipt;
        return paired ? RecordingWaitProbeStatus.Valid : RecordingWaitProbeStatus.PairingMismatch;
    }

    internal static bool ValidReceipt(in GpuRecordingWaitReceipt r, ulong prior) =>
        r.DeviceIdentity != Guid.Empty && r.ListIdentity != Guid.Empty && r.Attempt == unchecked(prior + 1)
        && r.Depth > 0 && r.Segment >= 0 && r.Segment < r.Depth && r.Frequency > 0 && r.Ticks >= 0
        && ((r.Count == 0 && r.Ticks == 0 && r.WaitCallCount == 0) || (r.Count == 1 && r.WaitCallCount > 0));

    internal static bool Compatible(in GpuRecordingWaitReceipt a, in GpuRecordingWaitReceipt b) =>
        a.DeviceIdentity == b.DeviceIdentity && a.Depth == b.Depth && a.Frequency == b.Frequency;
    internal static bool Compatible(in GpuRingWaitTotals a, in GpuRecordingWaitReceipt b) =>
        a.DeviceIdentity == b.DeviceIdentity && a.Depth == b.Depth && a.Frequency == b.Frequency;
    internal static bool ValidTotals(in GpuRingWaitTotals t) => t.DeviceIdentity != Guid.Empty && t.Depth > 0
        && t.Frequency > 0 && t.Count >= 0 && t.Ticks >= 0 && (t.Count != 0 || t.Ticks == 0);

    internal static RecordingWaitReconciliation Reconcile(RecordingWaitProbeStatus status,
        in RecordingWaitCallbackResult callback, RecordingWaitBoundary start, RecordingWaitBoundary end)
    {
        if (status == RecordingWaitProbeStatus.Off) return new(RecordingWaitReconciliationStatus.Off, null, null);
        if (status != RecordingWaitProbeStatus.Valid) return new(RecordingWaitReconciliationStatus.InvalidRoles, null, null);
        try
        {
            var main = callback.Main.Receipt!.Value; var prefix = callback.Prefix.Receipt;
            return Subtract(start, end, main, checked(main.Count + (prefix?.Count ?? 0)), checked(main.Ticks + (prefix?.Ticks ?? 0)));
        }
        catch (OverflowException) { return new(RecordingWaitReconciliationStatus.InvalidTotals, null, null); }
    }

    internal static RecordingWaitReconciliation Subtract(RecordingWaitBoundary start, RecordingWaitBoundary end,
        GpuRecordingWaitReceipt identity, long count, long ticks)
    {
        if (start.Status != RecordingWaitBoundaryStatus.Available || end.Status != RecordingWaitBoundaryStatus.Available
            || start.Totals is not { } a || end.Totals is not { } b)
            return new(RecordingWaitReconciliationStatus.MissingBoundary, null, null);
        if (!ValidTotals(a) || !ValidTotals(b)) return new(RecordingWaitReconciliationStatus.InvalidTotals, null, null);
        if (!Compatible(a, identity) || !Compatible(b, identity)) return new(RecordingWaitReconciliationStatus.IdentityMismatch, null, null);
        try
        {
            long c = checked(b.Count - a.Count - count), t = checked(b.Ticks - a.Ticks - ticks);
            if (c < 0 || t < 0) return new(RecordingWaitReconciliationStatus.NegativeRemainder, null, null);
            return new(RecordingWaitReconciliationStatus.Available, c, t);
        }
        catch (OverflowException) { return new(RecordingWaitReconciliationStatus.InvalidTotals, null, null); }
    }
}
