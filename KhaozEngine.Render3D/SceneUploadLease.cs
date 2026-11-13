namespace KhaozEngine.Render3D;

/// <summary>When an admitted upload's reservation is released. The upload's own retirement owes one settlement and
/// every resident material texture the upload created owes another, settled when that texture is destroyed, so a
/// texture the upload's claim paid for keeps the claim charged while another owner still uses it (issue #1776).
/// Released then confirms destruction of everything the claim covered, as <see cref="ISceneUploadReservation"/>
/// requires.</summary>
internal sealed class SceneUploadLease(ISceneUploadReservation reservation)
{
    int _owed = 1;

    /// <summary>A resident texture the upload created now owes a settlement of its own.</summary>
    internal void Owe() => _owed++;

    /// <summary>One owed settlement is done. The last releases and disposes the reservation.</summary>
    internal void Settle()
    {
        if (--_owed > 0) return;
        try { reservation.Released(); }
        finally { reservation.Dispose(); }
    }
}
