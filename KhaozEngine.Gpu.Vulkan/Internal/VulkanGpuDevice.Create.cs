using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE CREATION HALF OF THE NATIVE DEVICE: the shared instance lease, the physical-device selection of
    /// decision V-N3, the selective feature enable of V-N4, the one graphics queue of V-N5, and the first
    /// controlled point the <c>strict</c> validation rung can throw at. Split from the seam surface because the
    /// two are different concerns and because a device that must stay under the file-size cap has no room for
    /// both, which is the same split <c>D3D11GpuDevice</c> takes.
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <summary>
        /// Create an OFFSCREEN device with no swapchain, for the headless snapshot and golden paths. The whole of
        /// this row's creation path: everything a windowed device would additionally need (a surface, a presenting
        /// family, a swapchain) belongs to row 17.
        /// </summary>
        internal static GpuProviderDevice CreateHeadless()
        {
            VulkanValidationMode validation = VulkanValidation.FromEnvironment(out string? unrecognized);
            if (unrecognized != null) log.Warn(VulkanValidation.UnrecognizedWarning(unrecognized));

            var key = new VulkanInstanceKey(Windowed: false, Window: default, Validation: validation);
            VulkanInstanceLease<VulkanInstance> lease = VulkanInstance.Acquire(key);

            try
            {
                return Create(lease);
            }
            catch
            {
                // The lease is this method's until the device takes it over, and a throw anywhere below would
                // otherwise hold the process instance alive for the rest of the run. Ownership transfers on a
                // SUCCESSFUL construction only, which is the same rule GpuDeviceContext.Adopt applies one level
                // up.
                lease.Dispose();
                throw;
            }
        }

        static GpuProviderDevice Create(VulkanInstanceLease<VulkanInstance> lease)
        {
            VulkanInstance instance = lease.Value;
            Vk vk = instance.Api;

            PhysicalDevice[] handles = EnumeratePhysicalDevices(vk, instance.Handle);
            var reads = new VulkanPhysicalDeviceRead[handles.Length];
            var candidates = new VulkanPhysicalDeviceInfo[handles.Length];
            for (int i = 0; i < handles.Length; i++)
            {
                reads[i] = VulkanPhysicalDeviceReader.Read(vk, handles[i]);

                // The SAME requirement method the probe answered through, with the same flag, which is what makes
                // "checked by the probe and again at device creation" (V-N2) one decision asked twice rather than
                // two decisions that can disagree.
                string? rejection = VulkanDeviceRequirements.MissingRequirement(
                    reads[i].Facts, presentationRequired: false);

                candidates[i] = new VulkanPhysicalDeviceInfo(
                    reads[i].Facts.DeviceName, reads[i].Class, reads[i].IsLlvmpipe,
                    MeetsRequirements: rejection is null, RejectionReason: rejection);
            }

            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.FromEnvironment();
            int chosen = VulkanPhysicalDeviceSelection.Choose(request, candidates, out string? warning);
            if (warning != null) log.Warn(warning);
            if (chosen == VulkanPhysicalDeviceSelection.NoDevice) throw NoEligibleDevice(candidates);

            log.Info(VulkanPhysicalDeviceSelection.Describe(chosen, request, candidates));

            VulkanPhysicalDeviceRead read = reads[chosen];
            VulkanFeatureSelection features = VulkanFeatureChain.Select(read.Features, read.Facts.DeviceName);
            log.Info(VulkanFeatureChain.Describe(features));

            Device device = CreateDevice(vk, handles[chosen], read.GraphicsQueueFamily, features);
            var liveness = new VulkanDeviceLiveness();
            var loss = new VulkanDeviceLossLatch(liveness);

            try
            {
                vk.GetDeviceQueue(device, read.GraphicsQueueFamily, 0, out Queue graphicsQueue);
                Name(instance, device, graphicsQueue);

                // THE ONE TIMELINE (V-F1), created with the device because everything else is created against it:
                // a submission takes its next value, a fence holds one, the retire list gates on one, and the ring
                // recycles a segment against one. Creating it here rather than lazily is what lets every later row
                // assume it exists, and a failure here is caught below and destroys the half-built device.
                var semaphore = new VulkanTimelineSemaphore(vk, device, loss);
                NameTimeline(instance, device, semaphore.Handle);
                var timeline = new VulkanTimeline(semaphore, liveness);

                var created = new VulkanGpuDevice(lease, device, graphicsQueue, read.GraphicsQueueFamily,
                    ReadCapabilities(read, features), candidates[chosen].IsSoftwareRasterizer, liveness, loss,
                    timeline);

                // The strict rung's FIRST controlled point. Device creation is the noisiest moment a validation
                // layer sees, so an error raised by the create-info above is caught here rather than surviving
                // until whatever the next call happens to be.
                instance.Messenger?.Pump.ThrowIfLatched("vkCreateDevice");

                // No threading probe and no threading failure. That pair exists because a natively created
                // Direct3D 11 device has no Veldrid GraphicsDevice for D3D11ThreadingProbe to read a raw pointer
                // off. Vulkan has no equivalent query at all: its threading rules are spec guarantees rather than
                // a driver capability, so there is nothing to ask and nulls are the honest answer rather than a
                // gap.
                return new GpuProviderDevice(created, ThreadingCaps: null, ThreadingProbeFailure: null);
            }
            catch
            {
                // Between vkCreateDevice and the constructor taking over, this method holds a VkDevice nothing
                // else knows about, so a throw in the middle would leak it and every driver allocation behind it
                // until the process exits. The liveness flip is what stops a later disposal double-destroying it.
                liveness.MarkDead();
                vk.DestroyDevice(device, null);
                throw;
            }
        }

        // vkCreateDevice, with V-N4's pNext chain built from the SELECTION rather than from what the device
        // supports. One queue, one family, priority 1.0 (V-N5): no transfer queue and no async compute, and the
        // incumbent's cross-family path is not reproduced because its queue-create loop writes the graphics family
        // index for every entry instead of the loop variable, which is a spec violation validation flags.
        static Device CreateDevice(Vk vk, PhysicalDevice physicalDevice, uint queueFamily,
            in VulkanFeatureSelection features)
        {
            float priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo(
                sType: StructureType.DeviceQueueCreateInfo,
                queueFamilyIndex: queueFamily,
                queueCount: 1,
                pQueuePriorities: &priority);

            var features13 = new PhysicalDeviceVulkan13Features(
                sType: StructureType.PhysicalDeviceVulkan13Features,
                dynamicRendering: true,
                synchronization2: true);

            var features12 = new PhysicalDeviceVulkan12Features(
                sType: StructureType.PhysicalDeviceVulkan12Features,
                pNext: &features13,
                timelineSemaphore: true);

            // Named one by one, which is the whole of V-N4. The incumbent hands vkCreateDevice the entire
            // supported feature struct, so its real dependencies are unknowable from the code and a device missing
            // one fails at an unrelated call site instead of here with the feature's name in the message.
            var core = new PhysicalDeviceFeatures(
                samplerAnisotropy: features.SamplerAnisotropy,
                fillModeNonSolid: features.FillModeNonSolid,
                depthClamp: features.DepthClamp,
                independentBlend: features.IndependentBlend);

            var features2 = new PhysicalDeviceFeatures2(
                sType: StructureType.PhysicalDeviceFeatures2,
                pNext: &features12,
                features: core);

            // No device extensions at all on the headless path (V-N6). VK_KHR_swapchain is windowed-only and is
            // row 17's, and asking for it here would fail creation on a runner with no display server, which is
            // every runner the golden suite has.
            var createInfo = new DeviceCreateInfo(
                sType: StructureType.DeviceCreateInfo,
                pNext: &features2,
                queueCreateInfoCount: 1,
                pQueueCreateInfos: &queueInfo,
                enabledExtensionCount: 0,
                ppEnabledExtensionNames: null,
                // NO LAYERS. Device layers were deprecated in Vulkan 1.0.13 and modern loaders ignore them
                // entirely, so the incumbent's device-layer list is dead weight that reads as a working
                // configuration.
                enabledLayerCount: 0,
                ppEnabledLayerNames: null,
                // Null, deliberately: the features travel through the pNext chain above, and passing both is a
                // spec violation (VUID-VkDeviceCreateInfo-pNext-00373).
                pEnabledFeatures: null);

            VulkanResultCodes.Require(vk.CreateDevice(physicalDevice, in createInfo, null, out Device device),
                "vkCreateDevice");
            return device;
        }

        // Section 14's table, filled to the extent a device with no renderer on it can answer honestly. Row 18
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/528) owns the rest and the zero-difference parity test
        // that pins all of it.
        static GpuCapabilities ReadCapabilities(in VulkanPhysicalDeviceRead read,
            in VulkanFeatureSelection features)
            => new(
                // FALSE, and it is the one capability that flips every image. It comes from the negative-height
                // viewport path (7.2), which row 12 emits, so recording it as false here is a promise this row
                // makes to that row rather than a reading of anything.
                clipSpaceYInverted: false,
                depthRangeZeroToOne: true,
                deviceName: read.Facts.DeviceName,
                samplerAnisotropy: features.SamplerAnisotropy,
                samplerLodBias: true,
                // PINNED TO 1 rather than computed. V-C5 says the incumbent's own computation is reproduced, and
                // row 18 is where that happens: a formula invented here would be a silent lie that
                // AntiAliasing.ResolveFor acts on, and 1 is the direction that under-promises.
                maxMsaaSampleCount: 1,
                supportsShadowMaps: read.SupportsShadowMapFormat,
                supportsCompute: true,
                // TRUE, unlike Direct3D 11's, and identical to the incumbent's: VeldridMap already answers true
                // for Vulkan, so section 14's zero-permitted-difference bar is met here by construction.
                supportsCompletionFences: true);

        static PhysicalDevice[] EnumeratePhysicalDevices(Vk vk, Instance instance)
        {
            uint count = 0;
            Result counted = vk.EnumeratePhysicalDevices(instance, &count, null);
            if (counted != Result.Success && counted != Result.Incomplete)
                VulkanResultCodes.Require(counted, "vkEnumeratePhysicalDevices");

            if (count == 0)
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend found no physical device, on a machine whose support probe "
                    + "answered yes. That is a loader with no installed ICD behind it, and it means the machine "
                    + "changed between the probe and this call.");
            }

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* handles = devices)
            {
                VulkanResultCodes.Require(vk.EnumeratePhysicalDevices(instance, &count, handles),
                    "vkEnumeratePhysicalDevices");
            }
            return devices;
        }

        // Object naming (V-G5), best effort and only when the messenger is on. It is what makes a validation
        // message name the device and the queue instead of two bare handles, and it is the reason the naming call
        // is wired from the first row that creates an object rather than from the row that first finds it useful.
        static void Name(VulkanInstance instance, Device device, Queue queue)
        {
            VulkanDebugMessenger? messenger = instance.Messenger;
            if (messenger is null) return;

            messenger.NameObject(device, ObjectType.Device, (ulong)device.Handle, "KhaozEngine.Device");
            messenger.NameObject(device, ObjectType.Queue, (ulong)queue.Handle, "KhaozEngine.GraphicsQueue");
        }

        // The timeline semaphore's own name, separate because it is created after the queue and because it is the
        // object a synchronisation validation message is most likely to name. A bare handle there is exactly the
        // message nobody can act on.
        static void NameTimeline(VulkanInstance instance, Device device, Silk.NET.Vulkan.Semaphore timeline)
        {
            VulkanDebugMessenger? messenger = instance.Messenger;
            if (messenger is null) return;

            messenger.NameObject(device, ObjectType.Semaphore, timeline.Handle, "KhaozEngine.DeviceTimeline");
        }

        static InvalidOperationException NoEligibleDevice(IReadOnlyList<VulkanPhysicalDeviceInfo> candidates)
        {
            var reasons = new List<string>(candidates.Count);
            foreach (VulkanPhysicalDeviceInfo candidate in candidates)
                reasons.Add(candidate.Name + ": " + (candidate.RejectionReason ?? "no reason recorded"));

            return new InvalidOperationException(
                "No physical device on this machine can run the native Vulkan backend, on a machine whose support "
                + "probe answered yes. Every device and its own reason: " + string.Join(". ", reasons)
                + ". The probe and this call ask the same requirement method, so a disagreement means the machine "
                + "changed between them.");
        }
    }
}
