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
    MovementCoverageResult TracePrisms(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.PrismTraces++;
        ulong state = 0, status = 0;
        var table = !BypassTraceMemoForTests && TileWaterFloatingPointState.TryRead(out state, out status)
            ? _traceMemo.ForCurrentThread() : null;
        bool memoizable = table is not null;
        var key = memoizable ? new TileWaterTraceMemo.Key(query, physicsOrigin, spans.Length, contacts.Length,
            state) : default;
        if (memoizable && table!.TryRead(key, query.Body.CurrentSpace, identity, status, spans, contacts, out var cached))
        {
            if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoHits++;
            return cached;
        }
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.MemoMisses++;
        var result = TileWaterPrismCoverage.Trace(_field.Prisms, _field.Faces, _field.Ground, _field.PointIndex,
            _field.PrismStarts, _field.FaceStarts, query, physicsOrigin,
            identity, spans, contacts);
        if (memoizable && TileWaterFloatingPointState.TryRead(out ulong afterControls, out ulong afterStatus))
        {
            if (TileWaterQueryWork.Enabled)
            {
                TileWaterQueryWork.KernelStatusForTests = afterStatus;
                TileWaterQueryWork.KernelStatusCapturedForTests = true;
            }
            if (afterControls == state) table!.Store(key, result, spans, contacts, afterStatus);
        }
        return result;
    }
}
