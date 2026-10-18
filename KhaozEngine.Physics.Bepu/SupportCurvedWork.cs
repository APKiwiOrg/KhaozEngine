using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Scratch-owned certification work for a complete request. Graph restores never refund events.</summary>
internal sealed class SupportCurvedWork
{
    // Pool one existing source graph's worth of work per existing raw member slot.
    internal const int MaximumWork = 1_048_576;
    readonly int _limit;
    SupportCurvedWorkObservation _snapshot;

    internal SupportCurvedWork(int limit = MaximumWork)
    {
        if (limit <= 0 || limit > MaximumWork) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        Begin();
    }

    internal void Begin() => _snapshot = new(0, _limit, 0, 0, 0, 0, 0, 0, 0, 0, 0, default, default);
    internal bool Failed => _snapshot.Failure != SupportCurvedWorkFailure.None;
    internal SupportCurvedWorkObservation Snapshot => _snapshot;

    internal bool TryCharge(SupportCurvedWorkKind kind)
    {
        if (Failed) return false;
        if (_snapshot.Consumed == _limit) { Fail(SupportCurvedWorkFailure.WorkLimit); return false; }
        _snapshot = kind switch
        {
            SupportCurvedWorkKind.New => _snapshot with { New = _snapshot.New + 1 },
            SupportCurvedWorkKind.Sign => _snapshot with { Sign = _snapshot.Sign + 1 },
            SupportCurvedWorkKind.Split => _snapshot with { Split = _snapshot.Split + 1 },
            SupportCurvedWorkKind.Compile => _snapshot with { Compile = _snapshot.Compile + 1 },
            SupportCurvedWorkKind.RootCell => _snapshot with { RootCells = _snapshot.RootCells + 1 },
            SupportCurvedWorkKind.Refine => _snapshot with { Refine = _snapshot.Refine + 1 },
            SupportCurvedWorkKind.AdmissionCell => _snapshot with { AdmissionCells = _snapshot.AdmissionCells + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        _snapshot = _snapshot with { Consumed = _snapshot.Consumed + 1 };
        return true;
    }

    internal void Fail(SupportCurvedWorkFailure reason)
    {
        if (!Failed) _snapshot = _snapshot with { Failure = reason };
    }

    internal void ObserveStorage(int rootHighWater, int admissionHighWater)
    {
        if (Failed) return;
        _snapshot = _snapshot with
        {
            RootStorageHighWater = Math.Max(_snapshot.RootStorageHighWater, rootHighWater),
            AdmissionStorageHighWater = Math.Max(_snapshot.AdmissionStorageHighWater, admissionHighWater),
        };
    }
}
