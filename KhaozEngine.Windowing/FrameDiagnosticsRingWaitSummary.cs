using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsRingWaitSummary(bool Enabled, int TotalRows, int ValidRoleRows,
    int AvailableBoundaries, int ReconciledIntervals, bool RoleCoverageComplete, bool BoundaryCoverageComplete,
    bool IntervalCoverageComplete, bool FullyAttributed, RecordingWaitContribution? Prefix, RecordingWaitContribution? Main,
    RecordingWaitReconciliation Window, double? PrefixMs, double? MainMs, double? UnattributedMs)
{
    internal static FrameDiagnosticsRingWaitSummary Create(FrameDiagnosticsSample[] samples, bool enabled)
    {
        int valid = 0, boundaries = 0, intervals = 0, prefixes = 0;
        long prefixCount = 0, prefixTicks = 0, mainCount = 0, mainTicks = 0;
        bool sameIdentity = true, unique = true, validBoundaryTotals = true;
        bool prefixOverflow = false, mainOverflow = false;
        GpuRecordingWaitReceipt? identity = null;
        var attempts = new HashSet<(Guid, ulong)>();
        var ordinals = new HashSet<(Guid, ulong)>();
        RecordingWaitProbeRequest? first = null;
        bool? split = null;
        foreach (var sample in samples)
        {
            var row = sample.RecordingWait;
            var callback = row.Callback;
            if (valid == 0 && first is null) first = row.Request;
            if (row.Status == RecordingWaitProbeStatus.Valid
                && RecordingWaitProbeAccounting.Validate(row.Request, callback, sample.CompletedFrameWindow.RenderSuppressed) == RecordingWaitProbeStatus.Valid)
            {
                if (row.Request.Epoch != first!.Value.Epoch || row.Request.Sequence != valid + 1
                    || row.Request.Mode != first.Value.Mode || (split.HasValue && split != callback.PrefixExpected)) unique = false;
                split ??= callback.PrefixExpected;
                valid++;
                var main = callback.Main.Receipt!.Value;
                identity ??= main;
                sameIdentity &= RecordingWaitProbeAccounting.Compatible(identity.Value, main);
                unique &= attempts.Add((main.ListIdentity, main.Attempt)) && ordinals.Add((main.DeviceIdentity, main.RecordingOrdinal));
                try { mainCount = checked(mainCount + main.Count); mainTicks = checked(mainTicks + main.Ticks); }
                catch (OverflowException) { mainOverflow = true; }
                if (callback.PrefixExpected)
                {
                    var prefix = callback.Prefix.Receipt!.Value;
                    prefixes++;
                    sameIdentity &= RecordingWaitProbeAccounting.Compatible(identity.Value, prefix);
                    unique &= attempts.Add((prefix.ListIdentity, prefix.Attempt)) && ordinals.Add((prefix.DeviceIdentity, prefix.RecordingOrdinal));
                    try { prefixCount = checked(prefixCount + prefix.Count); prefixTicks = checked(prefixTicks + prefix.Ticks); }
                    catch (OverflowException) { prefixOverflow = true; }
                }
            }
            if (row.Reconciliation.Status == RecordingWaitReconciliationStatus.Available) intervals++;
        }
        RecordingWaitBoundary start = samples.Length == 0 ? default : samples[0].RecordingWait.Start;
        RecordingWaitBoundary end = samples.Length == 0 ? default : samples[^1].RecordingWait.End;
        if (start.Status == RecordingWaitBoundaryStatus.Available) boundaries++;
        RecordingWaitBoundary previous = start;
        foreach (var sample in samples)
        {
            var row = sample.RecordingWait;
            if (row.Start != previous) sameIdentity = false;
            if (row.End.Status == RecordingWaitBoundaryStatus.Available) boundaries++;
            CheckBoundary(row.Start); CheckBoundary(row.End);
            previous = row.End;
        }
        bool rolesComplete = enabled && samples.Length > 0 && valid == samples.Length && unique;
        RecordingWaitReconciliation window;
        if (!enabled) window = new(RecordingWaitReconciliationStatus.Off, null, null);
        else if (start.Status != RecordingWaitBoundaryStatus.Available || end.Status != RecordingWaitBoundaryStatus.Available)
            window = new(RecordingWaitReconciliationStatus.MissingBoundary, null, null);
        else if (!rolesComplete || identity is null) window = new(RecordingWaitReconciliationStatus.InvalidRoles, null, null);
        else if (prefixOverflow || mainOverflow || !validBoundaryTotals) window = new(RecordingWaitReconciliationStatus.InvalidTotals, null, null);
        else if (!sameIdentity) window = new(RecordingWaitReconciliationStatus.IdentityMismatch, null, null);
        else
        {
            try
            {
                window = RecordingWaitProbeAccounting.Subtract(start, end, identity.Value,
                checked(prefixCount + mainCount), checked(prefixTicks + mainTicks));
            }
            catch (OverflowException) { window = new(RecordingWaitReconciliationStatus.InvalidTotals, null, null); }
        }
        long frequency = identity?.Frequency ?? 0;
        bool reconciled = window.Status == RecordingWaitReconciliationStatus.Available;
        return new(enabled, samples.Length, valid, boundaries, intervals, rolesComplete,
            enabled && boundaries == samples.Length + 1, enabled && intervals == samples.Length,
            rolesComplete && reconciled && boundaries == samples.Length + 1 && intervals == samples.Length, prefixes == 0 || prefixOverflow ? null : new(prefixCount, prefixTicks),
            valid == 0 || mainOverflow ? null : new(mainCount, mainTicks), window,
            prefixes == 0 || prefixOverflow ? null : prefixTicks * 1000d / frequency,
            valid == 0 || mainOverflow ? null : mainTicks * 1000d / frequency,
            reconciled ? window.Ticks * 1000d / frequency : null);

        void CheckBoundary(RecordingWaitBoundary boundary)
        {
            if (boundary.Status != RecordingWaitBoundaryStatus.Available) return;
            if (boundary.Totals is not { } totals || !RecordingWaitProbeAccounting.ValidTotals(totals))
            { validBoundaryTotals = false; return; }
            if (identity.HasValue && !RecordingWaitProbeAccounting.Compatible(totals, identity.Value)) sameIdentity = false;
        }
    }
}
