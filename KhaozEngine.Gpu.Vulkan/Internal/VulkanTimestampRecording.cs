using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// Shared recording state for one timestamp pool. An armed command-list tail holds this state without
    /// reaching the pool's resource owner, which remains responsible for native lifetime and retirement.
    /// </summary>
    internal sealed class VulkanTimestampRecording(
        IVulkanTimestampApi api, VulkanTimeline timeline, GpuTimestampPoolState state)
    {
        ulong _recordingBuffer;
        VulkanCommandList? _recordingList;
        ulong _recordingId;

        internal ulong Handle { get; init; }
        internal bool Ended { get; private set; }
        internal bool IsDisposed { get; set; }

        internal void Begin(ulong commandBuffer, uint count, VulkanCommandList list, ulong recordingId)
        {
            RequireLive();
            state.Begin(count);
            Ended = false;
            _recordingBuffer = commandBuffer;
            _recordingList = list;
            _recordingId = recordingId;
        }

        internal void ValidateTimestampEnd(VulkanCommandList list, ulong recordingId, ulong commandBuffer, uint finalIndex)
        {
            RequireLive();
            if (!ReferenceEquals(_recordingList, list) || _recordingId != recordingId)
                throw new GpuTimestampStateException(GpuTimestampRefusal.WrongRecording, "The timestamp pool must be begun on this list in its current recording.") { Backend = GpuBackendKind.VulkanNative, Command = nameof(ValidateTimestampEnd), RecordingId = recordingId };
            RequireRecording(commandBuffer);
            state.ValidateTimestampEnd(finalIndex);
        }

        internal void Write(ulong commandBuffer, uint index)
        {
            RequireRecording(commandBuffer);
            state.Write(index);
            api.Write(commandBuffer, Handle, index);
        }

        internal void End(ulong commandBuffer)
        {
            RequireRecording(commandBuffer);
            state.End();
            Ended = true;
        }

        internal void RequireLive()
        {
            if (IsDisposed) throw new ObjectDisposedException(typeof(VulkanTimestampPool).FullName);
            RequireDeviceLive(timeline);
        }

        internal static void RequireDeviceLive(VulkanTimeline timeline)
        {
            if (timeline.IsDeviceDead)
                throw new GpuTimestampStateException(GpuTimestampRefusal.TimestampDeviceUnavailable, "The native Vulkan timestamp device is disposed or lost.") { Backend = GpuBackendKind.VulkanNative };
        }

        void RequireRecording(ulong commandBuffer, [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        {
            RequireLive();
            if (Ended || commandBuffer != _recordingBuffer)
                throw new GpuTimestampStateException(GpuTimestampRefusal.WrongRecording, "Write and end a timestamp batch in the command buffer that began it.") { Backend = GpuBackendKind.VulkanNative, Command = member };
        }
    }
}
