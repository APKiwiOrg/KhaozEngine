using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal sealed partial class D3D11CommandRecorder<TEmitter>
    {
        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        {
            D3D11TimestampPool native = RequireTimestampPool(pool);
            native.BeginRecording(this, count);
            _emitter.BeginTimestamps(pool, count);
        }

        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        {
            D3D11TimestampPool native = RequireTimestampPool(pool);
            native.WriteRecording(this, index);
            _emitter.WriteTimestamp(pool, index);
        }

        public void EndTimestamps(IGpuTimestampPool pool)
        {
            D3D11TimestampPool native = RequireTimestampPool(pool);
            native.EndRecording(this);
            _emitter.EndTimestamps(pool);
        }

        D3D11TimestampPool RequireTimestampPool(IGpuTimestampPool pool, [CallerMemberName] string member = "")
        {
            RequireRecording(member);
            ArgumentNullException.ThrowIfNull(pool);
            if (_timestampFences is null) throw new NotSupportedException("This Direct3D 11 command list has no timestamp device.");
            if (pool is not D3D11TimestampPool native) throw new ArgumentException("Use this Direct3D 11 device's timestamp pool.", nameof(pool));
            native.RequireOwner(_timestampFences);
            return native;
        }
    }
}
