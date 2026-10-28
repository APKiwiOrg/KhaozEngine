namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// Query ownership, non-flushing timestamp reads and the availability read of the completion wait,
    /// independent of COM types. <see cref="ReadAvailability"/> returns the success HRESULT, S_OK when ready.
    /// </summary>
    internal interface ID3D11TimestampApi
    {
        object CreateTimestamp();
        object CreateDisjoint();
        void Begin(object disjoint);
        void End(object query);
        bool TryReadTimestamp(object query, out ulong value);
        bool TryReadDisjoint(object query, out ulong frequency, out bool disjoint);
        int ReadAvailability(object query, bool flush);
        void Destroy(object query);
    }
}
