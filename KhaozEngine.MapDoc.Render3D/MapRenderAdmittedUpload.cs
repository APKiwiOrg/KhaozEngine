namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Observed device readiness ends in-flight admission without assuming CPU inputs were released.</summary>
internal readonly struct MapRenderAdmittedUpload(IMapRenderUpload upload, MapUploadReservation reservation)
{
    internal bool Ready
    {
        get
        {
            if (!upload.Ready) return false;
            reservation.ConfirmReady();
            return true;
        }
    }
    internal long Advance(long transferBytes) => upload.Advance(transferBytes);
    internal MapRenderHandle Publish() => upload.Publish();
    internal void Dispose() => upload.Dispose();
}
