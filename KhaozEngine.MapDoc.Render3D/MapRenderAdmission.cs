using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One aggregate allowance. Accepted, pending and retiring allocations never earn speculative credit.</summary>
public sealed class MapRenderAdmission
{
    readonly object _gate = new();
    readonly MapRenderBudgets _budgets;
    internal MapRenderLifetime Lifetime { get; }
    public long CpuBytes => Lifetime.CpuBytes;
    public long GpuBytes => Lifetime.GpuBytes;
    internal long AvailableCpuBytes { get { lock (_gate) return _budgets.MaxResidentCpuBytes - CpuBytes; } }
    public MapRenderAdmission(MapRenderBudgets budgets) : this(budgets, new()) { }
    internal MapRenderAdmission(MapRenderBudgets budgets, MapRenderLifetime lifetime)
    { ArgumentNullException.ThrowIfNull(budgets); budgets.Validate(); _budgets = budgets; Lifetime = lifetime; }
    public MapUploadReservation? TryReserve(SceneUploadDemand demand) => TryReserve(demand, out _);
    public MapUploadReservation? TryReserve(SceneUploadDemand demand, out MapSceneCapacity? capacity)
    {
        ArgumentNullException.ThrowIfNull(demand);
        if (string.IsNullOrEmpty(demand.InputDigest) || demand.OwnedGpuBytes < 0 || demand.PeakStagingGpuBytes < 0 ||
            demand.PeakCpuBytes < 0 || demand.MaxRetiredGpuBytes < 0 || demand.MaxRetiredGpuBytes > demand.OwnedGpuBytes || demand.TransferBytes < 0)
            throw new ArgumentException("Invalid renderer upload demand.", nameof(demand));
        lock (_gate)
        {
            capacity = null;
            long available = _budgets.MaxResidentGpuBytes - GpuBytes;
            if (demand.OwnedGpuBytes > available || demand.PeakStagingGpuBytes > available - demand.OwnedGpuBytes)
                capacity = new(nameof(MapRenderBudgets.MaxResidentGpuBytes), (decimal)GpuBytes + demand.OwnedGpuBytes + demand.PeakStagingGpuBytes, _budgets.MaxResidentGpuBytes);
            else if (demand.PeakCpuBytes > _budgets.MaxResidentCpuBytes - CpuBytes)
                capacity = new(nameof(MapRenderBudgets.MaxResidentCpuBytes), (decimal)CpuBytes + demand.PeakCpuBytes, _budgets.MaxResidentCpuBytes);
            if (capacity is not null) return null;
            long gpu = demand.OwnedGpuBytes + demand.PeakStagingGpuBytes;
            Lifetime.Gpu(gpu); Lifetime.Cpu(demand.PeakCpuBytes);
            return new(this, demand, gpu);
        }
    }
    internal CpuLease? TryReserveCpu(long bytes) => TryReserveCpu(bytes, out _);
    internal CpuLease? TryReserveCpu(long bytes, out MapSceneCapacity? capacity)
    {
        lock (_gate)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            capacity = null;
            if (bytes > _budgets.MaxResidentCpuBytes - CpuBytes)
            { capacity = new(nameof(MapRenderBudgets.MaxResidentCpuBytes), (decimal)CpuBytes + bytes, _budgets.MaxResidentCpuBytes); return null; }
            Lifetime.Cpu(bytes); return new(this, bytes);
        }
    }
    internal CpuLease ReserveCpu(long bytes)
    {
        var lease = TryReserveCpu(bytes, out var capacity);
        return lease ?? throw new MapSceneCapacityException(capacity!.LimitName, capacity.Requested, capacity.Allowance);
    }
    internal void ReleaseUpload(long cpu, long gpu, bool claimed)
    {
        lock (_gate)
        { Lifetime.Cpu(-cpu); Lifetime.Gpu(-gpu); if (claimed) Lifetime.Released(); }
    }
    internal sealed class CpuLease : IDisposable
    {
        sealed class Shared(MapRenderAdmission owner, long bytes)
        {
            internal readonly object Gate = new();
            internal MapRenderAdmission Owner => owner;
            internal long Bytes = bytes;
            internal int Owners = 1;
        }
        readonly Shared _shared;
        bool _disposed;
        internal CpuLease(MapRenderAdmission owner, long bytes) => _shared = new(owner, bytes);
        CpuLease(Shared shared) => _shared = shared;
        internal long Bytes { get { lock (_shared.Gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _shared.Bytes; } } }
        internal int OwnerCount { get { lock (_shared.Gate) return _shared.Owners; } }
        internal CpuLease Retain()
        {
            lock (_shared.Gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                checked { _shared.Owners++; }
                return new(_shared);
            }
        }
        internal CpuLease Split(long value)
        {
            lock (_shared.Gate)
            {
                RequireExclusive();
                if (value < 0 || value > _shared.Bytes) throw new ArgumentOutOfRangeException(nameof(value));
                _shared.Bytes -= value; return new(_shared.Owner, value);
            }
        }
        internal void ReduceTo(long value)
        {
            lock (_shared.Gate)
            {
                RequireExclusive();
                if (value < 0 || value > _shared.Bytes) throw new ArgumentOutOfRangeException(nameof(value));
                _shared.Owner.Lifetime.Cpu(value - _shared.Bytes); _shared.Bytes = value;
            }
        }
        void RequireExclusive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shared.Owners != 1) throw new InvalidOperationException("A retained CPU charge cannot be resized.");
        }
        public void Dispose()
        {
            lock (_shared.Gate)
            {
                if (_disposed) return; _disposed = true;
                if (--_shared.Owners == 0) _shared.Owner.Lifetime.Cpu(-_shared.Bytes);
            }
        }
    }
}
