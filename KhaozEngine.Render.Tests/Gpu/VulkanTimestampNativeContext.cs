using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    // Only the driver boundary is replaced. The real adapter still marshals and checks every call.
    internal sealed unsafe class VulkanTimestampNativeContext : INativeContext
    {
        readonly Dictionary<string, Delegate> _calls;
        readonly List<string> _trace;
        internal ulong[] Values { get; set; } = [250, 5, 9];
        internal ulong[] Availability { get; set; } = [1, 1, 1];
        internal Result ReadResult { get; set; } = Result.Success;
        internal QueryType QueryType { get; private set; }
        internal uint Capacity { get; private set; }
        internal uint ResetFirst { get; private set; }
        internal uint ResetCount { get; private set; }
        internal PipelineStageFlags2 WriteStage { get; private set; }
        internal QueryResultFlags ReadFlags { get; private set; }
        internal ulong ReadStride { get; private set; }
        internal nuint ReadBytes { get; private set; }
        internal uint ReadFirst { get; private set; }
        internal uint ReadCount { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int DestroyCalls { get; private set; }

        internal VulkanTimestampNativeContext(List<string>? trace = null)
        {
            _trace = trace ?? [];
            _calls = new()
            {
                ["vkCreateQueryPool"] = new Create(CreatePool),
                ["vkDestroyQueryPool"] = new Destroy((_, _, _) => DestroyCalls++),
                ["vkCmdResetQueryPool"] = new Reset(ResetPool),
                ["vkCmdWriteTimestamp2"] = new Write(WriteTimestamp),
                ["vkGetQueryPoolResults"] = new Read(ReadPool),
            };
        }

        Result CreatePool(Device device, QueryPoolCreateInfo* info, AllocationCallbacks* allocator, QueryPool* pool)
        {
            QueryType = info->QueryType;
            Capacity = info->QueryCount;
            *pool = new QueryPool(7);
            return Result.Success;
        }

        void ResetPool(CommandBuffer commandBuffer, QueryPool pool, uint first, uint count)
        {
            ResetFirst = first;
            ResetCount = count;
            _trace.Add($"Reset({count})");
        }

        void WriteTimestamp(CommandBuffer commandBuffer, PipelineStageFlags2 stage, QueryPool pool, uint index)
        {
            WriteStage = stage;
            _trace.Add($"Write({index})");
        }

        Result ReadPool(Device device, QueryPool pool, uint first, uint count, nuint size, void* data,
            ulong stride, QueryResultFlags flags)
        {
            ReadCalls++;
            ReadFlags = flags;
            ReadStride = stride;
            ReadBytes = size;
            ReadFirst = first;
            ReadCount = count;
            // Fill the native result/availability pairs with independent literal counter data.
            for (int i = 0; i < count; i++)
            {
                ulong* pair = (ulong*)((byte*)data + (nuint)i * stride);
                pair[0] = Values[i];
                pair[1] = Availability[i];
            }
            return ReadResult;
        }

        public nint GetProcAddress(string proc, int? slot = null)
            => TryGetProcAddress(proc, out nint address, slot) ? address : throw new EntryPointNotFoundException(proc);

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        {
            addr = _calls.TryGetValue(proc, out Delegate? call) ? Marshal.GetFunctionPointerForDelegate(call) : 0;
            return addr != 0;
        }

        public void Dispose() { }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate Result Create(Device device, QueryPoolCreateInfo* info, AllocationCallbacks* allocator, QueryPool* pool);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void Destroy(Device device, QueryPool pool, AllocationCallbacks* allocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void Reset(CommandBuffer commands, QueryPool pool, uint first, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void Write(CommandBuffer commands, PipelineStageFlags2 stage, QueryPool pool, uint index);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate Result Read(Device device, QueryPool pool, uint first, uint count, nuint size, void* data,
            ulong stride, QueryResultFlags flags);
    }
}
