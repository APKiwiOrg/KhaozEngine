namespace KhaozEngine.Render3D.Internal;

/// <summary>Hold an optional Fine creation refusal until the scene reaches a retry point.</summary>
internal sealed class TemporalDetailAdmission
{
    (int Width, int Height)? _refusedSize;
    bool _loggedRefusal;

    internal (TemporalHistoryDetail Mode, TemporalHistoryDetailFallback Reason) Latch(TemporalHistoryDetail requested,
        bool resolves, int width, int height, uint deviceLimit, long pixelLimit, int colorAttachments)
    {
        var reason = TemporalDetailMath.Fallback(requested, resolves, width, height, deviceLimit, pixelLimit, colorAttachments);
        if (requested != TemporalHistoryDetail.Fine || !resolves)
        {
            _refusedSize = null;
            _loggedRefusal = false;
            return (TemporalHistoryDetail.Display, reason);
        }
        if (_refusedSize is { } refused && refused != (width, height)) _refusedSize = null;
        if (reason != TemporalHistoryDetailFallback.None) return (TemporalHistoryDetail.Display, reason);
        if (_refusedSize.HasValue) return (TemporalHistoryDetail.Display, TemporalHistoryDetailFallback.CreationFailed);
        return (TemporalHistoryDetail.Fine, TemporalHistoryDetailFallback.None);
    }

    // A new size can admit an attempt while the device is still refusing Fine. Only a live Fine store ends the run.
    internal void ConfirmFine() => _loggedRefusal = false;

    /// <summary>Hold the size and return whether this run of refusals should be logged.</summary>
    internal bool RefuseCreation(int width, int height)
    {
        _refusedSize = (width, height);
        if (_loggedRefusal) return false;
        _loggedRefusal = true;
        return true;
    }
}
