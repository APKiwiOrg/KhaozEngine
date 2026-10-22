namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Query ownership and non-flushing timestamp reads, independent of COM types.</summary>
    internal interface ID3D11TimestampApi
    {
        object CreateTimestamp();
        object CreateDisjoint();
        void Begin(object disjoint);
        void End(object query);
        bool TryReadTimestamp(object query, out ulong value);
        bool TryReadDisjoint(object query, out ulong frequency, out bool disjoint);
        void Destroy(object query);
    }
}
