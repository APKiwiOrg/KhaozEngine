using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Compute in the name includes this control in the existing sync subset and ordinary Vulkan shards.
[Collection("NativeDeviceLifecycle")]
public sealed unsafe class VulkanValidationComputeGpuTests(ITestOutputHelper output)
{
    const string ExpectedVuid = "VUID-VkDebugUtilsMessengerCreateInfoEXT-flags-zerobitmask";

    // #1539: a quiet pump is ambiguous until a native validation error has actually reached it.
    [GpuFact]
    public void TheNativeMessenger_ReceivesAControlledValidationError()
    {
        VulkanValidationMode mode = VulkanValidation.FromEnvironment(out _);
        if (!VulkanValidation.WantsMessenger(mode)) return;
        if (!VulkanDormancy.NativeDeviceAvailable()) return;

        using Vk vk = Vk.GetApi();
        Instance instance = CreateInstance(vk, mode);
        try
        {
            // An isolated messenger uses the established recording logger. Only the deliberate error stays
            // out of the suite log. Other tests keep their ordinary pump and the CI scan has no exemptions.
            var log = new RecordingLogger();
            using VulkanDebugMessenger messenger = Assert.IsType<VulkanDebugMessenger>(
                VulkanDebugMessenger.TryCreate(vk, instance, mode, log));
            VulkanValidationPump pump = messenger.Pump;
            Assert.Equal(0, pump.ErrorCount);
            Assert.True(vk.TryGetInstanceExtension(instance, out ExtDebugUtils debug));
            using (debug)
            {
                var invalid = new DebugUtilsMessengerCreateInfoEXT(
                    sType: StructureType.DebugUtilsMessengerCreateInfoExt,
                    flags: 1,
                    messageSeverity: DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                    messageType: DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
                    pfnUserCallback: new PfnDebugUtilsMessengerCallbackEXT(&IgnoreMessage));

                // Only flags is invalid. It is reserved and must be zero. No invalid handles or GPU work.
                Result result = debug.CreateDebugUtilsMessenger(instance, in invalid, null,
                    out DebugUtilsMessengerEXT extra);
                if (result == Result.Success) debug.DestroyDebugUtilsMessenger(instance, extra, null);
            }

            Assert.Equal(1, pump.ErrorCount);
            Assert.Contains(ExpectedVuid, Assert.Single(log.Errors), System.StringComparison.Ordinal);
            Assert.Equal(VulkanValidation.ThrowsOnError(mode), pump.IsLatched);
            if (VulkanValidation.ThrowsOnError(mode))
            {
                var error = Assert.Throws<VulkanValidationException>(() => pump.ThrowIfLatched("positive control"));
                Assert.Equal(VulkanValidationRefusal.StrictErrorLatched, error.Reason);
                Assert.Equal(ExpectedVuid, error.MessageIdName);
            }
            else
            {
                pump.ThrowIfLatched("positive control");
            }
            output.WriteLine($"Vulkan messenger positive control caught {ExpectedVuid}, rung={mode}, errors={pump.ErrorCount}");
        }
        finally
        {
            vk.DestroyInstance(instance, null);
        }
    }

    static Instance CreateInstance(Vk vk, VulkanValidationMode mode)
    {
        string[] layers = [VulkanValidation.LayerName];
        nint nativeLayers = SilkMarshal.StringArrayToPtr(layers);
        nint nativeExtensions = SilkMarshal.StringArrayToPtr(VulkanInstanceLayout.HeadlessInstanceExtensions(mode).ToArray());
        VulkanInstanceValidation validation = VulkanInstanceValidation.Select(mode, layers);
        try
        {
            fixed (ValidationFeatureEnableEXT* enabled = validation.EnabledFeatures.AsSpan())
            {
                var features = new ValidationFeaturesEXT(
                    sType: StructureType.ValidationFeaturesExt,
                    enabledValidationFeatureCount: (uint)validation.EnabledFeatures.Length,
                    pEnabledValidationFeatures: enabled);
                var app = new ApplicationInfo(sType: StructureType.ApplicationInfo, apiVersion: (uint)Vk.Version13);
                var info = new InstanceCreateInfo(
                    sType: StructureType.InstanceCreateInfo,
                    pNext: validation.EnabledFeatures.IsEmpty ? null : &features,
                    pApplicationInfo: &app,
                    enabledLayerCount: 1,
                    ppEnabledLayerNames: (byte**)nativeLayers,
                    enabledExtensionCount: 1,
                    ppEnabledExtensionNames: (byte**)nativeExtensions);
                VulkanResultCodes.Require(vk.CreateInstance(in info, null, out Instance instance), "vkCreateInstance");
                return instance;
            }
        }
        finally
        {
            SilkMarshal.Free(nativeExtensions);
            SilkMarshal.Free(nativeLayers);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static Bool32 IgnoreMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data, void* userData) => false;
}
