using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanCommandList
    {
        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            EndRenderingBeforeIllegalCommand();
            native.Begin(CurrentBuffer, count, this, _timestampRecordingId);
        }

        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            RefuseArmedTimestampPool(native);
            EndRenderingBeforeIllegalCommand();
            native.Write(CurrentBuffer, index);
        }

        public void EndTimestamps(IGpuTimestampPool pool)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            RefuseArmedTimestampPool(native);
            native.End(CurrentBuffer);
        }

        VulkanTimestampPool RequireTimestampPool(IGpuTimestampPool pool, [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_recording) throw new GpuRecordingStateException(GpuRecordingStateRefusal.CommandOutsideRecording, "Timestamp commands require an open command list.") { Backend = GpuBackendKind.VulkanNative, Command = member };
            if (_timestampTimeline is null) throw new GpuTimestampUnsupportedException(GpuTimestampRefusal.TimestampUnsupported, "This Vulkan command list has no timestamp support.") { Backend = GpuBackendKind.VulkanNative, Command = member };
            ArgumentNullException.ThrowIfNull(pool);
            if (pool is not VulkanTimestampPool native)
                throw new GpuTimestampArgumentException(GpuTimestampRefusal.ForeignPool, "Use a timestamp pool from this Vulkan device.", nameof(pool)) { Backend = GpuBackendKind.VulkanNative, ActualType = pool.GetType() };
            native.RequireOwner(_timestampTimeline);
            return native;
        }
    }
}
