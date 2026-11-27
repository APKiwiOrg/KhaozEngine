using System;
using System.IO;
using KhaozEngine.Gpu.Core;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreStartupTests
{
    [NativeFact]
    public void Advertised_but_unloadable_validation_layer_is_a_creation_failure()
        => WithStartupFixture("1", provider => Assert.Throws<InvalidOperationException>(() => provider.CreateHeadless()));

    [NativeFact]
    public void No_compatible_driver_refusal_carries_the_driver_package()
        => WithStartupFixture(null, provider =>
        {
            var refusal = Assert.Throws<VulkanBackendCreationUnsupportedException>(() => provider.CreateHeadless());
            Assert.Equal(VulkanBackendCreationRefusal.MachineUnsupported, refusal.Reason);
            Assert.Equal("mesa-vulkan-drivers", refusal.DriverPackage);
            Assert.Equal("GpuBackendSelector.IsBackendSupported", refusal.Probe);
            Assert.False(string.IsNullOrEmpty(refusal.MissingRequirement));
        });

    private static void WithStartupFixture(string? validation, Action<CoreVulkanBackendProvider> check)
    {
        const string loaderVar = "KHAOZ_NATIVE_VULKAN_LOADER";
        const string validationVar = "KHAOZ_NATIVE_VALIDATION";
        const string fixtureName = "khaoz_native_vulkan_startup_fixture";
        string file = OperatingSystem.IsWindows() ? fixtureName + ".dll"
            : OperatingSystem.IsMacOS() ? "lib" + fixtureName + ".dylib" : "lib" + fixtureName + ".so";
        string fixture = Path.Combine(AppContext.BaseDirectory, file);
        Assert.True(File.Exists(fixture));
        string? oldLoader = Environment.GetEnvironmentVariable(loaderVar);
        string? oldValidation = Environment.GetEnvironmentVariable(validationVar);
        try
        {
            Environment.SetEnvironmentVariable(loaderVar, fixture);
            Environment.SetEnvironmentVariable(validationVar, validation);
            check(new CoreVulkanBackendProvider());
        }
        finally
        {
            Environment.SetEnvironmentVariable(loaderVar, oldLoader);
            Environment.SetEnvironmentVariable(validationVar, oldValidation);
        }
    }
}
