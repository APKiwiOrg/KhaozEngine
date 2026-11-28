using System;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Core;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreStartupTests
{
    private enum StartupMode : uint
    {
        SupportedWithUnloadableLayer,
        MissingDriverAndLayer,
        ZeroDevices,
        InstanceInitializationFailed,
        InstanceHostMemoryFailed,
        DeviceEnumerationFailed,
    }

    [NativeFact]
    public void Advertised_but_unloadable_validation_layer_is_a_creation_failure()
        => WithStartupFixture(StartupMode.SupportedWithUnloadableLayer, "1", provider =>
        {
            Assert.True(provider.IsSupported());
            Assert.Throws<InvalidOperationException>(() => provider.CreateHeadless());
        });

    [NativeFact]
    public void No_compatible_driver_refusal_carries_the_driver_package()
        => WithStartupFixture(StartupMode.MissingDriverAndLayer, null, AssertMissingDriver);

    [NativeFact]
    public void Missing_driver_takes_precedence_over_a_missing_requested_layer()
        => WithStartupFixture(StartupMode.MissingDriverAndLayer, "1", AssertMissingDriver);

    [NativeFact]
    public void An_unlayered_instance_with_zero_devices_is_a_missing_driver()
        => WithStartupFixture(StartupMode.ZeroDevices, "1", AssertMissingDriver);

    [NativeFact]
    public void Unlayered_instance_initialization_failure_is_a_machine_refusal()
        => WithStartupFixture(StartupMode.InstanceInitializationFailed, "1", AssertOtherMachineRefusal);

    [NativeFact]
    public void Unlayered_instance_host_memory_failure_is_a_machine_refusal()
        => WithStartupFixture(StartupMode.InstanceHostMemoryFailed, "1", AssertOtherMachineRefusal);

    [NativeFact]
    public void Unlayered_device_enumeration_failure_is_a_machine_refusal()
        => WithStartupFixture(StartupMode.DeviceEnumerationFailed, null, AssertOtherMachineRefusal);

    private static void AssertOtherMachineRefusal(CoreVulkanBackendProvider provider)
        => AssertMachineRefusal(provider, driverPackage: null);

    private static void AssertMissingDriver(CoreVulkanBackendProvider provider)
        => AssertMachineRefusal(provider, driverPackage: "mesa-vulkan-drivers");

    private static void AssertMachineRefusal(CoreVulkanBackendProvider provider, string? driverPackage)
    {
        Assert.False(provider.IsSupported());
        var refusal = Assert.Throws<VulkanBackendCreationUnsupportedException>(() => provider.CreateHeadless());
        Assert.Equal(VulkanBackendCreationRefusal.MachineUnsupported, refusal.Reason);
        Assert.Equal(driverPackage, refusal.DriverPackage);
        Assert.Equal("GpuBackendSelector.IsBackendSupported", refusal.Probe);
        Assert.False(string.IsNullOrEmpty(refusal.MissingRequirement));
    }

    private static unsafe void WithStartupFixture(StartupMode mode, string? validation, Action<CoreVulkanBackendProvider> check)
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

        // Keep the configured fixture loaded while the production loader takes its own reference.
        nint library = NativeLibrary.Load(fixture);
        try
        {
            var setMode = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(library, "khaoz_fixture_set_startup_mode");
            setMode((uint)mode);
            Environment.SetEnvironmentVariable(loaderVar, fixture);
            Environment.SetEnvironmentVariable(validationVar, validation);
            check(new CoreVulkanBackendProvider());
        }
        finally
        {
            Environment.SetEnvironmentVariable(loaderVar, oldLoader);
            Environment.SetEnvironmentVariable(validationVar, oldValidation);
            NativeLibrary.Free(library);
        }
    }
}
