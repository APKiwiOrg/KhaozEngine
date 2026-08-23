using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-G4's FOUNDATION: a <c>VkResult</c> check that runs in EVERY configuration, and the words that
    /// go with the results worth explaining.
    /// <para>
    /// THE INCUMBENT'S <c>VulkanUtil.CheckResult</c> IS <c>[Conditional("DEBUG")]</c>, and that single attribute
    /// is why this type exists. A Release build of the incumbent checked nothing: <c>vkQueueSubmit</c> can return
    /// <c>VK_ERROR_DEVICE_LOST</c> and the call site carries on as though it succeeded, so a device-loss latch
    /// built on that shape would never fire in the only configuration anybody ships. Issue #427 asks for exactly
    /// that latch, and it can only be honest if the check underneath it is unconditional. Nothing in this package
    /// may reintroduce a conditional result check, and the reason is not performance: a <c>VkResult</c> compare
    /// is one branch against a native call that just crossed the driver boundary.
    /// </para>
    /// <para>
    /// <see cref="Require"/> is the CREATION-time form, for the calls that happen before a latch exists. Once a
    /// device is up, results go through <see cref="VulkanDeviceLossLatch.Check"/> first, so a loss is latched at
    /// the fault site with the call's name before anything else is asked of the driver.
    /// </para>
    /// </summary>
    internal static class VulkanResultCodes
    {
        /// <summary>Whether <paramref name="result"/> IS a device loss, which is the one failure that latches.
        /// Every other failure is the caller's to report or throw, because only the caller knows whether its own
        /// failed call is recoverable.
        /// <para>
        /// <c>VK_ERROR_DEVICE_LOST</c> and nothing else. Vulkan has no equivalent of the Direct3D
        /// <c>DEVICE_RESET</c> and <c>DEVICE_HUNG</c> split: the reason a device went is reported through
        /// <c>VK_EXT_device_fault</c> when the driver supports it, not through the result code.
        /// </para></summary>
        internal static bool IsDeviceLoss(Result result) => result == Result.ErrorDeviceLost;

        /// <summary>Whether <paramref name="result"/> is a failure at all. Vulkan's success codes are zero and
        /// positive (<c>VK_SUCCESS</c>, <c>VK_NOT_READY</c>, <c>VK_TIMEOUT</c>, <c>VK_INCOMPLETE</c>,
        /// <c>VK_SUBOPTIMAL_KHR</c>) and every error is negative, which is an ABI guarantee rather than an
        /// observation. <c>VK_SUBOPTIMAL_KHR</c> being a SUCCESS is the one that catches people out, and the
        /// swapchain row (https://github.com/APKiwiOrg/KhaozEngine/issues/527) is where it matters.</summary>
        internal static bool IsFailure(Result result) => (int)result < 0;

        /// <summary>
        /// Throw when <paramref name="result"/> is a failure, naming the call. The CREATION-time check, for the
        /// window before a device (and therefore a latch) exists: <c>vkCreateInstance</c>,
        /// <c>vkEnumeratePhysicalDevices</c>, <c>vkCreateDevice</c>. Unconditional in every configuration, which
        /// is the whole point of this type.
        /// </summary>
        /// <param name="result">What the call returned.</param>
        /// <param name="call">The Vulkan call's own name, e.g. <c>vkCreateDevice</c>. It goes in the message
        /// verbatim, because the reader of that message is looking the call up in the spec.</param>
        /// <exception cref="InvalidOperationException"><paramref name="result"/> is a failure.</exception>
        internal static void Require(Result result, string call)
        {
            if (!IsFailure(result)) return;

            throw new InvalidOperationException(
                $"The native Vulkan backend's {call} failed: {Describe(result)}. This is a creation-time failure "
                + "on a machine whose support probe answered yes, so it is either a driver that reports more than "
                + "it can do or a resource the process could not get.");
        }

        /// <summary>The stable token for a result, for a telemetry header field that has to group cleanly across
        /// sessions. The spec's own spelling, so it can be searched for.</summary>
        internal static string Token(Result result) => result switch
        {
            Result.Success => "VK_SUCCESS",
            Result.ErrorDeviceLost => "VK_ERROR_DEVICE_LOST",
            Result.ErrorOutOfHostMemory => "VK_ERROR_OUT_OF_HOST_MEMORY",
            Result.ErrorOutOfDeviceMemory => "VK_ERROR_OUT_OF_DEVICE_MEMORY",
            Result.ErrorInitializationFailed => "VK_ERROR_INITIALIZATION_FAILED",
            Result.ErrorLayerNotPresent => "VK_ERROR_LAYER_NOT_PRESENT",
            Result.ErrorExtensionNotPresent => "VK_ERROR_EXTENSION_NOT_PRESENT",
            Result.ErrorFeatureNotPresent => "VK_ERROR_FEATURE_NOT_PRESENT",
            Result.ErrorIncompatibleDriver => "VK_ERROR_INCOMPATIBLE_DRIVER",
            Result.ErrorTooManyObjects => "VK_ERROR_TOO_MANY_OBJECTS",
            _ => result.ToString(),
        };

        /// <summary>The sentence a human reads, for the session log. The results named here are the ones a reader
        /// needs told apart: everything else is reported by its token, which is enough to look up.</summary>
        internal static string Describe(Result result) => result switch
        {
            Result.Success => "VK_SUCCESS (the call reports no failure)",
            Result.ErrorDeviceLost => "VK_ERROR_DEVICE_LOST, the device went away: a driver reset, a hardware "
                + "removal, or a fault the driver answered by tearing the device down. Vulkan does not say which "
                + "through the result code, and VK_EXT_device_fault is the extension that does when a driver "
                + "supports it",
            Result.ErrorOutOfHostMemory => "VK_ERROR_OUT_OF_HOST_MEMORY, the process could not get CPU memory",
            Result.ErrorOutOfDeviceMemory => "VK_ERROR_OUT_OF_DEVICE_MEMORY, the device is out of memory",
            Result.ErrorInitializationFailed => "VK_ERROR_INITIALIZATION_FAILED, the object could not be "
                + "initialized for a driver-specific reason",
            Result.ErrorLayerNotPresent => "VK_ERROR_LAYER_NOT_PRESENT, a requested layer is not installed",
            Result.ErrorExtensionNotPresent => "VK_ERROR_EXTENSION_NOT_PRESENT, a requested extension is not "
                + "supported by this loader or device",
            Result.ErrorFeatureNotPresent => "VK_ERROR_FEATURE_NOT_PRESENT, a requested feature is not supported "
                + "by this device. On this backend that means a feature was asked for that the chained query said "
                + "was there, which is a driver disagreeing with itself",
            Result.ErrorIncompatibleDriver => "VK_ERROR_INCOMPATIBLE_DRIVER, no installed driver is compatible "
                + "with the requested API version",
            Result.ErrorTooManyObjects => "VK_ERROR_TOO_MANY_OBJECTS, too many objects of this type already exist",
            _ => $"{result} ({(int)result})",
        };
    }
}
