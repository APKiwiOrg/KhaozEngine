using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One payload shared by live views. Disposed shells hold no backing columns.</summary>
internal sealed class MapFoliageColumnOwner
{
    readonly object _gate = new();
    MapFoliageInstanceBatch? _batch;
    MapPreparationReservation? _receipt;
    IDisposable? _cpu;
    MapFoliageSharedMetadata? _metadata;
    object[]? _references;
    long _directBytes;
    int _owners = 1;
    internal const long ControlBytes = 104;
    internal MapFoliageColumnOwner(MapFoliageInstanceBatch batch, MapPreparationReservation? receipt = null, IDisposable? cpu = null,
        MapFoliageSharedMetadata? metadata = null, object[]? references = null, long directBytes = 0)
    { _batch = batch; _receipt = receipt; _cpu = cpu; _metadata = metadata; _references = references; _directBytes = directBytes; }
    internal Reader Retain()
    {
        lock (_gate)
        {
            if (_batch is null) throw new ObjectDisposedException(nameof(MapFoliageColumnOwner));
            checked { _owners++; }
            return new(this);
        }
    }
    internal void Release()
    {
        var released = Detach();
        if (!released.Last) return;
        released.Receipt?.Dispose(); released.Cpu?.Dispose();
        released.Metadata?.SettleRelease(released.Objects, released.Direct);
    }
    (bool Last, MapPreparationReservation? Receipt, IDisposable? Cpu, MapFoliageSharedMetadata? Metadata, long Objects, long Direct) Detach()
    {
        lock (_gate)
        {
            if (_owners <= 0) throw new InvalidOperationException("Column ownership was already returned.");
            if (--_owners != 0) return default;
            long objects = _metadata?.RemoveReferences(_references!) ?? 0;
            var result = (true, _receipt, _cpu, _metadata, objects, _directBytes);
            _batch = null; _receipt = null; _cpu = null; _metadata = null; _references = null; _directBytes = 0;
            return result;
        }
    }
    internal sealed class Reader(MapFoliageColumnOwner owner) : IDisposable, IGroundCoverInstanceSource
    {
        MapFoliageColumnOwner? _owner = owner;
        internal MapFoliageInstanceBatch Batch => (_owner ?? throw new ObjectDisposedException(nameof(Reader)))._batch
            ?? throw new ObjectDisposedException(nameof(MapFoliageColumnOwner));
        public int Count => Batch.Count;
        public GroundCoverInstanceColumns Columns => Batch.Columns;
        internal long PlanBytes => _owner!._receipt is null ? checked(Batch.Work.RetainedBytes + ControlBytes) : 0;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
    internal sealed class ModelIds(Reader source) : IReadOnlyList<string>
    {
        public int Count => source.Columns.ModelCount;
        public string this[int index] => source.Columns.ModelId(index);
        public IEnumerator<string> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
