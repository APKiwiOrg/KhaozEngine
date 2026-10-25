using System;

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
    internal long Bytes { get; private set; }
    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ledger.Add(_window, bytes, _retained);
        bool newReceipt = _retained && Bytes == 0 && bytes > 0;
        Bytes += bytes;
        _ledger.ReceiptChanged(newReceipt ? 1 : 0);
    }
    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _ledger.Remove(_window, bytes, _retained);
        bool ended = _retained && Bytes > 0 && Bytes == bytes;
        Bytes -= bytes;
        _ledger.ReceiptChanged(ended ? -1 : 0);
    }
    internal MapPreparationReservation Transfer(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_retained || bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { Bytes = bytes };
        _ledger.Transfer(bytes); Bytes -= bytes;
        _ledger.ReceiptChanged(bytes > 0 ? 1 : 0);
        return owner;
    }
    internal MapPreparationReservation SplitRetained(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_retained || bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { Bytes = bytes };
        Bytes -= bytes;
        _ledger.ReceiptChanged(bytes > 0 && Bytes > 0 ? 1 : 0);
        return owner;
    }
    internal void MoveWorkspaceTo(MapPreparationReservation destination, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed || destination._disposed, this);
        if (_retained || destination._retained || _ledger != destination._ledger || _window != destination._window || bytes < 0 || bytes > Bytes)
            throw new InvalidOperationException("Workspace can move only between live owners in the same preparation view.");
        Bytes -= bytes; destination.Bytes = checked(destination.Bytes + bytes);
    }
    internal Dependency Borrow(MapPreparationReservation product) => new(this, product);
    internal void ReattributeTo(MapPreparationReservation destination)
    {
        ObjectDisposedException.ThrowIf(_disposed || destination._disposed, this);
        if (!_retained || destination._retained || _ledger != destination._ledger)
            throw new InvalidOperationException("Selective export requires retained inventory in the same ledger.");
        _ledger.Reattribute(_window, destination._window, Bytes);
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
    internal void Absorb(MapPreparationReservation product)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (product._disposed || product.Bytes == 0) return;
        if (_retained || !product._retained)
            throw new InvalidOperationException("Only retained inventory can transfer into workspace.");
        if (_ledger != product._ledger)
        {
            // A captured stage can enter another attempt. Refusal leaves its original owner intact.
            Reserve(product.Bytes);
            product.Dispose();
            return;
        }
        if (_window != product._window) _ledger.Reattribute(product._window, _window, product.Bytes);
        _ledger.ReturnToWorkspace(product.Bytes);
        Bytes += product.Bytes; product.Bytes = 0;
        _ledger.ReceiptChanged(-1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Release(Bytes); _disposed = true;
    }
}
