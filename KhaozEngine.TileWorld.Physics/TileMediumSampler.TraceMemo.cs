using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    // Thread-local bypass leaves other readers and other tests alone. Reset affects only this
    // sampler's table on the calling thread, so a census can explicitly measure a cold trace.
    [ThreadStatic] internal static bool BypassTraceMemoForTests;
    internal void ResetTraceMemoForTests() => _traceMemo.ResetCurrentThread();
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
        var table = !BypassTraceMemoForTests && TryReadTraceControls(out state)
            ? _traceMemo.ForCurrentThread() : null;
        bool memoizable = table is not null;
        var key = memoizable ? new TileWaterTraceMemo.Key(query, physicsOrigin, spans.Length, contacts.Length,
            state) : default;
        // A hit preserves every output bit, current-caller references, caller buffers, refusals
        // and validation outside the kernel. Freshly allocated boundary ids replay by value.
        // Sticky FP status is excluded because compiled kernel tiers can leave different status
        // for the same inputs. Executed-work counters are excluded too. Controls stay in the key.
        if (memoizable && table!.TryRead(key, query.Body.CurrentSpace, identity, spans, contacts, out var cached))
        {
            if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoHits++;
            return cached;
        }
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoMisses++;
        var result = TileWaterPrismCoverage.Trace(_field.Prisms, _field.Faces, _field.Ground, _field.PointIndex,
            _field.PrismStarts, _field.FaceStarts, query, physicsOrigin,
            identity, spans, contacts);
        if (memoizable) table!.Store(key, result, spans, contacts);
        return result;
    }
}
