using System;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>CI must not accept a requested validation rung without its native message producer.</summary>
internal static class VulkanValidationRequirements
{
    internal static void RequireMessenger(VulkanValidationMode mode, bool hasMessenger, string? ci)
    {
        bool isCi = ci == "1" || string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase);
        if (!isCi || !VulkanValidation.WantsMessenger(mode) || hasMessenger) return;

        throw new VulkanValidationException(VulkanValidationRefusal.MessengerUnavailable,
            $"CI requested {VulkanValidation.EnvVarName}={mode}, but no debug-utils messenger was created. "
            + "Validation cannot report errors without it, so this run refuses to continue. Check the "
            + "VK_EXT_debug_utils extension and the Vulkan loader.")
        {
            Site = "vkCreateDebugUtilsMessengerEXT",
        };
    }
}
