using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanValidationRequirementsTests
{
    // #1539: validation in CI must fail closed, while local diagnosis stays warn-only.
    [Theory]
    [InlineData((int)VulkanValidationMode.On, false, "true", true)]
    [InlineData((int)VulkanValidationMode.Strict, false, "TRUE", true)]
    [InlineData((int)VulkanValidationMode.Sync, false, "1", true)]
    [InlineData((int)VulkanValidationMode.On, true, "true", false)]
    [InlineData((int)VulkanValidationMode.Off, false, "true", false)]
    [InlineData((int)VulkanValidationMode.On, false, null, false)]
    [InlineData((int)VulkanValidationMode.Strict, false, "false", false)]
    [InlineData((int)VulkanValidationMode.Sync, false, "0", false)]
    public void MissingMessenger_IsFatalOnlyWhenCiRequestedValidation(
        int rung, bool hasMessenger, string? ci, bool refused)
    {
        var mode = (VulkanValidationMode)rung;
        if (refused)
        {
            var error = Assert.Throws<VulkanValidationException>(
                () => VulkanValidationRequirements.RequireMessenger(mode, hasMessenger, ci));
            Assert.Equal(VulkanValidationRefusal.MessengerUnavailable, error.Reason);
            Assert.Equal("vkCreateDebugUtilsMessengerEXT", error.Site);
        }
        else
        {
            VulkanValidationRequirements.RequireMessenger(mode, hasMessenger, ci);
        }
    }
}
