using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    // Thread-local bypass leaves other readers and other tests alone. Reset affects only this
    // sampler's table on the calling thread, so a census can explicitly measure a cold trace.
    [ThreadStatic] internal static bool BypassTraceMemoForTests;
    readonly TileWaterTraceResultMemo _resultMemo = new();
    internal void ResetTraceMemoForTests()
    {
        _traceMemo.ResetCurrentThread();
        _resultMemo.ResetCurrentThread();
    }
    internal WeakReference? CaptureResultMemoTableForTests() => _resultMemo.CaptureCurrentTableForTests();
    internal WeakReference? CaptureTraceMemoTableForTests() => _traceMemo.CaptureCurrentTableForTests();
    // A controls-only reader seam, isolated to the calling test thread and restored by its owner.
    internal delegate bool ControlRead(out ulong token);
    [ThreadStatic] internal static ControlRead? ControlReaderForTests;
    internal static bool TryReadTraceControls(out ulong token) => ControlReaderForTests is { } reader
        ? reader(out token) : TileWaterFloatingPointState.TryRead(out token);
    MovementCoverageResult TracePrisms(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.PrismTraces++;
        ulong state = 0;
        bool controls = !BypassTraceMemoForTests && TryReadTraceControls(out state);
        var table = controls ? _traceMemo.ForCurrentThread() : null;
        bool memoizable = table is not null;
        var key = controls ? new TileWaterTraceMemo.Key(query, physicsOrigin, spans.Length, contacts.Length,
            state) : default;
        // A hit preserves every output bit, current-caller references, caller buffers, refusals
        // and validation outside the kernel. Joined-triangle boundary ids replay by string value.
        // Sticky FP status is excluded because compiled kernel tiers can leave different status
        // for the same inputs. Executed-work counters are excluded too. Controls stay in the key.
        if (memoizable && table!.TryRead(key, query.Body.CurrentSpace, identity, spans, contacts, out var cached))
        {
            if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoHits++;
            return cached;
        }
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoMisses++;
        var result = TraceResult(false, query, physicsOrigin, identity, spans, contacts, key, controls);
        if (memoizable) table!.Store(key, result, spans, contacts);
        return result;
    }

    MovementCoverageResult TraceFlat(in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        // Fresh dry queries pay lookup cost without avoiding geometry. Keep their cheap one-span
        // construction out of the eight wet-result slots, including paths outside the water extent.
        if (_field.FlatColumns.Length == 0 || !new TileWaterPathBounds(query, origin).Meets(Extent.Minimum, Extent.Maximum))
            return ComputeTrace(true, query, origin, identity, spans, contacts);
        ulong state = 0;
        bool controls = !BypassTraceMemoForTests && TryReadTraceControls(out state);
        var key = controls ? new TileWaterTraceMemo.Key(query, origin, spans.Length, contacts.Length, state) : default;
        return TraceResult(true, query, origin, identity, spans, contacts, key, controls);
    }

    MovementCoverageResult TraceResult(bool flat, in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts,
        in TileWaterTraceMemo.Key key, bool controls)
    {
        var table = controls && key.Space.WorldId.Length <= TileWaterTraceMemo.SpaceIdCharacterLimit &&
            key.Space.LocalId.Length <= TileWaterTraceMemo.SpaceIdCharacterLimit ? _resultMemo.ForCurrentThread() : null;
        if (table is not null)
        {
            if (table.TryRead(key, query.Body.CurrentSpace, identity, spans, contacts, out var cached))
            {
                if (TileWaterQueryWork.Enabled) TileWaterQueryWork.ResultHits++;
                return cached;
            }
            if (TileWaterQueryWork.Enabled) TileWaterQueryWork.ResultMisses++;
        }
        else if (TileWaterQueryWork.Enabled) TileWaterQueryWork.ResultBypasses++;
        var result = ComputeTrace(flat, query, origin, identity, spans, contacts);
        // Small prism results and numeric refusals keep their existing home and replacement age.
        if (table is not null && (flat || result.SpansWritten > TileWaterTraceMemo.SpanLimit ||
            result.ContactsWritten > TileWaterTraceMemo.ContactLimit))
        {
            bool stored = table.Store(key, result, spans, contacts);
            if (TileWaterQueryWork.Enabled)
            {
                if (stored) TileWaterQueryWork.ResultStores++;
                else TileWaterQueryWork.ResultBypasses++;
            }
        }
        return result;
    }

    MovementCoverageResult ComputeTrace(bool flat, in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) => flat
        ? TileWaterColumnCoverage.Trace(_field.FlatColumns, _field.FlatBoundaries, _field.FlatExterior, query,
            origin, identity, spans, contacts, _field.FlatIndex)
        : TileWaterPrismCoverage.Trace(_field.Prisms, _field.Faces, _field.Ground, _field.PointIndex,
            _field.PrismStarts, _field.FaceStarts, query, origin, identity, spans, contacts);
}
