namespace KhaozEngine.Render3D;

/// <summary>An optional enclosing ledger. A refusal precedes decoder counter changes and allocation.</summary>
internal interface IGltfDecodeReservationObserver
{
    void Reserve(long bytes);
    void Release(long bytes);
}
