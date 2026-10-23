namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal readonly partial struct D3D11NativeEmitter
    {
        public void BeginTimestamps(IGpuTimestampPool pool, uint count) => ((D3D11TimestampPool)pool).EmitBegin();
        public void WriteTimestamp(IGpuTimestampPool pool, uint index) => ((D3D11TimestampPool)pool).EmitWrite(index);
        public void EndTimestamps(IGpuTimestampPool pool) => ((D3D11TimestampPool)pool).EmitEnd();
    }
}
