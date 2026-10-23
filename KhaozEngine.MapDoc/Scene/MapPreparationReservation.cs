using System;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One operation or product owns its capacity until explicit release.</summary>
internal sealed class MapPreparationReservation(MapPreparationLedger ledger, MapPreparationLedger.Window? window, bool retained) : IDisposable
{
    bool _disposed;
    internal long Bytes { get; private set; }
    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ledger.Add(window, bytes, retained);
        Bytes += bytes;
    }
    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        ledger.Remove(window, bytes, retained);
        Bytes -= bytes;
    }
    internal MapPreparationReservation Transfer(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (retained || bytes < 0 || bytes > Bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var owner = new MapPreparationReservation(ledger, window, true) { Bytes = bytes };
        ledger.Transfer(bytes); Bytes -= bytes;
        return owner;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Release(Bytes); _disposed = true;
    }
}
