using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanCommandList
    {
        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            EndRenderingBeforeIllegalCommand();
            native.Begin(CurrentBuffer, count);
        }

        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            EndRenderingBeforeIllegalCommand();
            native.Write(CurrentBuffer, index);
        }

        public void EndTimestamps(IGpuTimestampPool pool)
            => RequireTimestampPool(pool).End(CurrentBuffer);

        VulkanTimestampPool RequireTimestampPool(IGpuTimestampPool pool)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_recording) throw new InvalidOperationException("Timestamp commands require an open command list.");
            if (_timestampTimeline is null) throw new NotSupportedException("This Vulkan command list has no timestamp support.");
            ArgumentNullException.ThrowIfNull(pool);
            if (pool is not VulkanTimestampPool native)
                throw new ArgumentException("Use a timestamp pool from this Vulkan device.", nameof(pool));
            native.RequireOwner(_timestampTimeline);
            return native;
        }
    }
}
