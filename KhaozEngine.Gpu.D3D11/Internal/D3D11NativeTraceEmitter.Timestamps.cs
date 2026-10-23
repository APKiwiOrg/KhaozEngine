namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal readonly partial struct D3D11NativeTraceEmitter
    {
        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
            => _log.Record(D3D11NativeCall.Begin, _log.Id(((D3D11TimestampPool)pool).DisjointQuery));
        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
            => _log.Record(D3D11NativeCall.End, _log.Id(((D3D11TimestampPool)pool).TimestampQuery(index)));
        public void EndTimestamps(IGpuTimestampPool pool)
            => _log.Record(D3D11NativeCall.End, _log.Id(((D3D11TimestampPool)pool).DisjointQuery));
    }
}
