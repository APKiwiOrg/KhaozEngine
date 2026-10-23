using System;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// Native calls for one fixed timestamp pool. Its interleaved native read scratch is allocated once at
    /// creation, so a resolve needs neither a temporary allocation nor a shared scratch lock.
    /// </summary>
    internal sealed unsafe class VulkanTimestampApi : IVulkanTimestampApi
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;
        ulong[] _results = [];

        internal VulkanTimestampApi(Vk vk, Device device, VulkanDeviceLossLatch loss, IDeviceLiveness liveness)
        {
            _vk = vk ?? throw new ArgumentNullException(nameof(vk));
            _device = device;
            _loss = loss ?? throw new ArgumentNullException(nameof(loss));
            _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        }

        public ulong CreatePool(uint capacity)
        {
            RequireLive();
            if (capacity == 0 || capacity > int.MaxValue / 2)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (_results.Length != 0)
                throw new InvalidOperationException("A native timestamp adapter owns exactly one fixed pool.");
            _results = new ulong[checked((int)capacity * 2)];
            var info = new QueryPoolCreateInfo(StructureType.QueryPoolCreateInfo,
                queryType: QueryType.Timestamp, queryCount: capacity);
            Result result = _vk.CreateQueryPool(_device, in info, null, out QueryPool pool);
            Check(result, "vkCreateQueryPool (timestamps)");
            return pool.Handle;
        }

        public void DestroyPool(ulong pool)
        {
            if (_liveness.IsDead) return;
            _vk.DestroyQueryPool(_device, new QueryPool(pool), null);
        }

        public void Reset(ulong commandBuffer, ulong pool, uint count)
        {
            RequireLive();
            // This command returns void. Validation uses the backend's existing strict/sync pump.
            _vk.CmdResetQueryPool(new CommandBuffer((nint)commandBuffer), new QueryPool(pool), 0, count);
        }

        public void Write(ulong commandBuffer, ulong pool, uint index)
        {
            RequireLive();
            _vk.CmdWriteTimestamp2(new CommandBuffer((nint)commandBuffer), PipelineStageFlags2.AllCommandsBit,
                new QueryPool(pool), index);
        }

        public bool TryRead(ulong pool, uint count, Span<ulong> values, Span<ulong> availability)
        {
            RequireLive();
            if (count == 0 || count > _results.Length / 2 || count > values.Length || count > availability.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            Result result;
            fixed (ulong* data = _results)
                result = _vk.GetQueryPoolResults(_device, new QueryPool(pool), 0, count,
                    (nuint)count * 16, data, 16, QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
            Check(result, "vkGetQueryPoolResults (timestamps)");
            if (result != Result.Success) return false;
            for (int i = 0; i < count; i++)
            {
                values[i] = _results[i * 2];
                availability[i] = _results[i * 2 + 1];
            }
            return true;
        }

        void RequireLive()
        {
            if (_liveness.IsDead)
                throw new InvalidOperationException("The native Vulkan timestamp device is disposed or lost.");
        }

        void Check(Result result, string site)
        {
            if (_loss.Check(result, site))
                throw new InvalidOperationException($"The native Vulkan device was lost at {site}.");
            VulkanResultCodes.Require(result, site);
        }
    }
}
