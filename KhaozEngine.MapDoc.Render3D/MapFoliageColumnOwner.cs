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
    int _owners = 1;
    internal MapFoliageColumnOwner(MapFoliageInstanceBatch batch, MapPreparationReservation? receipt = null, IDisposable? cpu = null)
    { _batch = batch; _receipt = receipt; _cpu = cpu; }
    internal Reader Retain()
    {
        lock (_gate)
        {
            if (_batch is null) throw new ObjectDisposedException(nameof(MapFoliageColumnOwner));
            checked { _owners++; } return new(this);
        }
    }
    internal void Release()
    {
        MapPreparationReservation? receipt; IDisposable? cpu;
        lock (_gate)
        {
            if (_owners <= 0) throw new InvalidOperationException("Column ownership was already returned.");
            if (--_owners != 0) return;
            _batch = null; receipt = _receipt; cpu = _cpu; _receipt = null; _cpu = null;
        }
        receipt?.Dispose(); cpu?.Dispose();
    }
    internal sealed class Reader(MapFoliageColumnOwner owner) : IDisposable, IGroundCoverInstanceSource
    {
        MapFoliageColumnOwner? _owner = owner;
        internal MapFoliageInstanceBatch Batch => (_owner ?? throw new ObjectDisposedException(nameof(Reader)))._batch
            ?? throw new ObjectDisposedException(nameof(MapFoliageColumnOwner));
        public int Count => Batch.Count;
        public GroundCoverInstanceColumns Columns => Batch.Columns;
        internal long PlanBytes => _owner!._receipt is null ? Batch.Work.RetainedBytes : 0;
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
