using System;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TWO REAL DRIVER CALLS BEHIND <see cref="IVulkanShaderApi"/>, and nothing else. Which bytes get here,
    /// and whether two programs share one module, are decided above this line in
    /// <see cref="VulkanShaderModuleCache"/>, which is what makes the dedup testable with no loader.
    ///
    /// <para><b>THE RESULT GOES THROUGH THE LOSS LATCH AND THEN THROUGH <see cref="VulkanResultCodes.Require"/>,
    /// IN EVERY CONFIGURATION</b>, like every other creation call in this package. <c>vkCreateShaderModule</c> is
    /// named among the calls that can return <c>VK_ERROR_DEVICE_LOST</c>, and the incumbent's own
    /// <c>CheckResult</c> is <c>[Conditional("DEBUG")]</c>, so a Release build of it would carry on with a handle
    /// that is not one.</para>
    ///
    /// <para><b>THE BYTES ARE PASSED VERBATIM AND ARE NOT COPIED.</b> <c>pCode</c> wants a <c>uint*</c>, and the
    /// span is pinned for the duration of the call only, because the driver is required to have consumed the code
    /// by the time <c>vkCreateShaderModule</c> returns. They arrive already checked for being a whole number of
    /// 32-bit words: that guard is <see cref="VulkanShaderModuleCache.GetOrCreate"/>'s, above this line, where it
    /// can be tested with no loader.</para>
    /// </summary>
    internal sealed unsafe class VulkanShaderApi : IVulkanShaderApi
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every module made here and outlives them all.</param>
        /// <param name="loss">The device's loss latch, which the create result is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates the destroy.</param>
        internal VulkanShaderApi(Vk vk, Device device, VulkanDeviceLossLatch loss, IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _loss = loss;
            _liveness = liveness;
        }

        /// <inheritdoc/>
        public ulong CreateShaderModule(ReadOnlySpan<byte> spirv)
        {
            fixed (byte* code = spirv)
            {
                var createInfo = new ShaderModuleCreateInfo(
                    sType: StructureType.ShaderModuleCreateInfo,
                    codeSize: (nuint)spirv.Length,
                    pCode: (uint*)code);

                Result created = _vk.CreateShaderModule(_device, in createInfo, null, out ShaderModule module);
                if (_loss.Check(created, "vkCreateShaderModule"))
                {
                    throw new InvalidOperationException(
                        "The native Vulkan backend could not create a shader module, because the device was LOST. "
                        + "The loss itself is in the session log and in the telemetry session header, with the "
                        + "call that first noticed it.");
                }

                VulkanResultCodes.Require(created, "vkCreateShaderModule");
                return module.Handle;
            }
        }

        /// <inheritdoc/>
        public void DestroyShaderModule(ulong module)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyShaderModule(_device, new ShaderModule(module), null);
        }
    }
}
