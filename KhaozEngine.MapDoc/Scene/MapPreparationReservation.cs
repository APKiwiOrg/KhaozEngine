using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One operation or product owns its capacity until explicit release.</summary>
internal sealed class MapPreparationReservation : IDisposable
{
    readonly MapPreparationLedger _ledger;
    MapPreparationLedger.Window? _window;
    readonly bool _retained;
    internal MapPreparationReservation(MapPreparationLedger ledger, MapPreparationLedger.Window? window, bool retained)
    { _ledger = ledger; _window = window; _retained = retained; }
    bool _disposed;
    long _bytes;
    MapPreparationDependencies? _dependencies;
    internal long Bytes => checked(_bytes + (_dependencies?.Bytes ?? 0));
    internal object WindowIdentity => (object?)_window ?? _ledger;
    internal long InventoryBytes(HashSet<object> seen, bool includeDependencies = true) =>
        checked(_bytes + (includeDependencies ? _dependencies?.UniqueBytes(seen) ?? 0 : 0));
    internal void DependOn(MapSceneAdmission admission, object identity, long bytes) =>
        (_dependencies ??= new()).Add(admission, identity, bytes, windowOwner: this);
    internal void Share(MapSceneAdmission admission, object identity) =>
        (_dependencies ??= new()).Add(admission, identity, _bytes, ownership: this);
    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ledger.Add(_window, bytes, _retained);
        bool newReceipt = _retained && _bytes == 0 && bytes > 0;
        _bytes += bytes;
        _ledger.ReceiptChanged(newReceipt ? 1 : 0);
    }
    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > _bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _ledger.Remove(_window, bytes, _retained);
        bool ended = _retained && _bytes > 0 && _bytes == bytes;
        _bytes -= bytes;
        _ledger.ReceiptChanged(ended ? -1 : 0);
    }
    internal MapPreparationReservation Transfer(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long direct = checked(bytes - (_dependencies?.Bytes ?? 0));
        if (_retained || direct < 0 || direct > _bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { _bytes = direct, _dependencies = _dependencies };
        _dependencies = null;
        _ledger.Transfer(direct); _bytes -= direct;
        _ledger.ReceiptChanged(direct > 0 ? 1 : 0);
        return owner;
    }
    internal MapPreparationReservation SplitRetained(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_retained || bytes < 0 || bytes > _bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { _bytes = bytes };
        _bytes -= bytes;
        _ledger.ReceiptChanged(bytes > 0 && _bytes > 0 ? 1 : 0);
        return owner;
    }
    internal void MoveWorkspaceTo(MapPreparationReservation destination, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed || destination._disposed, this);
        if (_retained || destination._retained || _ledger != destination._ledger || _window != destination._window || bytes < 0 || bytes > _bytes)
            throw new InvalidOperationException("Workspace can move only between live owners in the same preparation view.");
        _bytes -= bytes; destination._bytes = checked(destination._bytes + bytes);
    }
    internal Dependency Borrow(MapPreparationReservation product) => new(this, product);
    internal void ReattributeTo(MapPreparationReservation destination)
    {
        ObjectDisposedException.ThrowIf(_disposed || destination._disposed, this);
        if (!_retained || destination._retained || _ledger != destination._ledger)
            throw new InvalidOperationException("Selective export requires retained inventory in the same ledger.");
        var references = _dependencies?.Rebind(destination);
        try { _ledger.Reattribute(_window, destination._window, _bytes); }
        catch { references?.Dispose(); throw; }
        _dependencies?.Dispose(); _dependencies = references;
        _window = destination._window;
    }

    /// <summary>Admits a dependency in the destination view without consuming its earlier owner on failure.</summary>
    internal sealed class Dependency : IDisposable
    {
        readonly MapPreparationReservation _destination, _product;
        readonly MapPreparationReservation? _foreign;
        readonly IDisposable? _reference;
        bool _absorbed;
        internal Dependency(MapPreparationReservation destination, MapPreparationReservation product)
        {
            ObjectDisposedException.ThrowIf(destination._disposed || product._disposed, product);
            if (destination._retained || !product._retained)
                throw new InvalidOperationException("Only retained inventory can be borrowed by workspace.");
            _destination = destination; _product = product;
            if (destination._ledger == product._ledger)
                _reference = destination._ledger.ReferenceWindow(product.Bytes, product._window, null);
            else
            {
                _foreign = new(destination._ledger, destination._window, false);
                _foreign.Reserve(product.Bytes);
            }
        }
        internal void Absorb()
        {
            if (_absorbed) return;
            if (_foreign is not null)
            {
                long bytes = _foreign.Bytes;
                _product.Dispose();
                _foreign.MoveWorkspaceTo(_destination, bytes);
            }
            else
            {
                _reference?.Dispose();
                _destination.Absorb(_product);
            }
            _absorbed = true;
        }
        public void Dispose() { _reference?.Dispose(); _foreign?.Dispose(); }
    }
    internal void Absorb(MapPreparationReservation product, bool retainDependencies = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (product._disposed || product.Bytes == 0) return;
        if (_retained || !product._retained)
            throw new InvalidOperationException("Only retained inventory can transfer into workspace.");
        if (_ledger != product._ledger)
        {
            // A captured stage can enter another attempt. Refusal leaves its original owner intact.
            Reserve(retainDependencies ? product.Bytes : product._bytes);
            product.Dispose();
            return;
        }
        if (_window != product._window)
        {
            if (retainDependencies) product.ReattributeTo(this);
            else _ledger.Reattribute(product._window, _window, product._bytes);
        }
        long direct = product._bytes;
        _ledger.ReturnToWorkspace(direct);
        _bytes = checked(_bytes + direct); product._bytes = 0;
        if (retainDependencies) product._dependencies?.MoveTo(_dependencies ??= new());
        else product._dependencies?.Dispose();
        product._dependencies = null;
        _ledger.ReceiptChanged(direct > 0 ? -1 : 0);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Release(_bytes); _dependencies?.Dispose(); _dependencies = null; _disposed = true;
    }
}
