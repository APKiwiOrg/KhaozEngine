using System;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One operation or product owns its capacity until explicit release.</summary>
internal sealed class MapPreparationReservation : IDisposable
{
    readonly MapPreparationLedger _ledger;
    readonly MapPreparationLedger.Window? _window;
    readonly bool _retained;
    internal MapPreparationReservation(MapPreparationLedger ledger, MapPreparationLedger.Window? window, bool retained)
    { _ledger = ledger; _window = window; _retained = retained; }
    bool _disposed;
    internal long Bytes { get; private set; }
    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ledger.Add(_window, bytes, _retained);
        Bytes += bytes;
    }
    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _ledger.Remove(_window, bytes, _retained);
        Bytes -= bytes;
    }
    internal MapPreparationReservation Transfer(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_retained || bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { Bytes = bytes };
        _ledger.Transfer(bytes); Bytes -= bytes;
        return owner;
    }
    internal MapPreparationReservation SplitRetained(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_retained || bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(_ledger, _window, true) { Bytes = bytes };
        Bytes -= bytes;
        return owner;
    }
    internal void MoveWorkspaceTo(MapPreparationReservation destination, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed || destination._disposed, this);
        if (_retained || destination._retained || _ledger != destination._ledger || _window != destination._window || bytes < 0 || bytes > Bytes)
            throw new InvalidOperationException("Workspace can move only between live owners in the same preparation view.");
        Bytes -= bytes; destination.Bytes = checked(destination.Bytes + bytes);
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
    }
    public void Dispose()
    {
        if (_disposed) return;
        Release(Bytes); _disposed = true;
    }
}
