using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
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
            VulkanValidationMode validation = ValidationFromEnvironment();

            var key = new VulkanInstanceKey(Windowed: false, Window: default, Validation: validation);
            return Acquire(key, window: null);
        }

        /// <summary>
        /// Create a WINDOWED device: a surface from the window, the presenting-family check V-N5 makes against it,
        /// <c>VK_KHR_swapchain</c> on the device, and the first swapchain plus the first acquire inside the
        /// present boundary's own constructor.
        /// <para>
        /// THE INSTANCE IS A DIFFERENT ONE FROM THE HEADLESS PATH'S AND THE TWO CANNOT COEXIST, which is the one
        /// case decision V-N1's single-instance model cannot serve. A live instance's extension list is fixed at
        /// creation and Vulkan offers no way to add one afterwards, so a process holding a headless device open
        /// and then asking for a windowed one is refused by name. See <c>VulkanInstanceRefCount.Acquire</c>
        /// for why refusing beats the two silent alternatives, and the package README for the ordering rule that
        /// resolves it: create the windowed device first, or run them in separate processes.
        /// </para>
        /// </summary>
        internal static GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            VulkanValidationMode validation = ValidationFromEnvironment();

            VulkanAcquireMode acquire = VulkanAcquire.FromEnvironment(out string? unrecognizedAcquire);
            if (unrecognizedAcquire != null) log.Warn(VulkanAcquire.UnrecognizedWarning(unrecognizedAcquire));
            log.Info(VulkanAcquire.ActiveDescription(acquire));

            // THE ONE COMBINATION THAT CANNOT WORK, warned rather than refused. The stall mode reproduces a
            // configuration a validation layer rejects, so a run with both on reports that on every present and
            // buries whatever else the layer found. Turning a diagnostic session into a startup failure would be
            // the wrong trade, and saying nothing would waste the session.
            if (acquire == VulkanAcquireMode.Stall && validation != VulkanValidationMode.Off)
                log.Warn(VulkanAcquire.ValidationConflictWarning(validation));

            var key = new VulkanInstanceKey(
                Windowed: true, Window: request.Window.Kind, Validation: validation);

            return Acquire(key, new VulkanWindowRequest(request, acquire));
        }

        // The lease's lifetime rule, shared by both entry points: it is this method's until the device takes it
        // over, and a throw anywhere below would otherwise hold the process instance alive for the rest of the
        // run. Ownership transfers on a SUCCESSFUL construction only, which is the same rule
        // GpuDeviceContext.Adopt applies one level up.
        static GpuProviderDevice Acquire(in VulkanInstanceKey key, VulkanWindowRequest? window)
        {
            VulkanInstanceLease<VulkanInstance> lease = VulkanInstance.Acquire(key);

            try
            {
                return Create(lease, window);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        static VulkanValidationMode ValidationFromEnvironment()
        {
            VulkanValidationMode validation = VulkanValidation.FromEnvironment(out string? unrecognized);
            if (unrecognized != null) log.Warn(VulkanValidation.UnrecognizedWarning(unrecognized));
            return validation;
        }

        static GpuProviderDevice Create(VulkanInstanceLease<VulkanInstance> lease, VulkanWindowRequest? window)
        {
            VulkanInstance instance = lease.Value;
            Vk vk = instance.Api;

            // THE SURFACE IS CREATED BEFORE THE PHYSICAL DEVICE IS CHOSEN, which is the one ordering the windowed
            // path forces. Decision V-N5 requires the one graphics queue to be the presenting one, and
            // vkGetPhysicalDeviceSurfaceSupportKHR cannot answer that without a surface, so the surface has to
            // exist while candidates are still being filtered. It is an INSTANCE-level object and outlives every
            // swapchain made against it, so creating it early costs nothing.
            VulkanSurfaceApi? surfaces = window is null ? null : new VulkanSurfaceApi(vk, instance.Handle);
            ulong surface = window is null
                ? 0
                : surfaces!.CreateSurface(
                    window.Request.Window.Kind, window.Request.Window.Handle, window.Request.Window.Display);

            try
            {
                return CreateOn(lease, vk, instance, surfaces, surface, window);
            }
            catch
            {
                // The surface is this method's until the present boundary takes it over, and it is a child of the
                // process instance rather than of the device, so a leak here survives every later device.
                surfaces?.DestroySurface(surface);
                throw;
            }
        }

        static GpuProviderDevice CreateOn(VulkanInstanceLease<VulkanInstance> lease, Vk vk, VulkanInstance instance,
            VulkanSurfaceApi? surfaces, ulong surface, VulkanWindowRequest? window)
        {
            bool windowed = window is not null;

            PhysicalDevice[] handles = EnumeratePhysicalDevices(vk, instance.Handle);
            var reads = new VulkanPhysicalDeviceRead[handles.Length];
            var candidates = new VulkanPhysicalDeviceInfo[handles.Length];
            for (int i = 0; i < handles.Length; i++)
            {
                VulkanPhysicalDeviceRead candidate = VulkanPhysicalDeviceReader.Read(vk, handles[i]);

                // THE PRESENT ANSWER IS FILLED IN HERE AND NOWHERE ELSE, because here is where a surface exists.
                // The reader leaves it false, deliberately: it is asked from the probe too, which receives no
                // window and could not build one without enabling a platform surface extension on the headless
                // path, which V-N6 forbids outright.
                if (windowed)
                {
                    surfaces!.BindPhysicalDevice(handles[i]);
                    candidate = candidate with
                    {
                        Facts = candidate.Facts with
                        {
                            GraphicsFamilyPresents = candidate.Facts.HasGraphicsQueueFamily
                                && surfaces.SupportsPresent(surface, candidate.GraphicsQueueFamily),
                        },
                    };
                }

                reads[i] = candidate;

                // The SAME requirement method the probe answered through, with the flag this path's own answer
                // needs, which is what makes "checked by the probe and again at device creation" (V-N2) one
                // decision asked twice rather than two decisions that can disagree.
                string? rejection = VulkanDeviceRequirements.MissingRequirement(
                    reads[i].Facts, presentationRequired: windowed);

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

            // BOUND TO THE CHOSEN DEVICE FOR THE REST OF ITS LIFE. Every capability query the present boundary
            // makes from here on is about this physical device, and the loop above left it bound to whichever
            // candidate it last asked about.
            surfaces?.BindPhysicalDevice(handles[chosen]);

            Device device = CreateDevice(vk, handles[chosen], read.GraphicsQueueFamily, features, windowed);
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

                // MV3's DEPTH, resolved ONCE per device rather than once per command list or once per ring. Two
                // reads of the environment could disagree if the variable changed mid-run, and a device whose
                // lists were three slots deep and whose uniform rings were four segments deep is exactly the
                // shared-number-two-indexes confusion section 6.1 warns about.
                int framesInFlight = VulkanFramesInFlight.FromEnvironment(out string? unrecognizedDepth);
                if (unrecognizedDepth != null)
                    log.Warn(VulkanFramesInFlight.UnrecognizedWarning(unrecognizedDepth));
                log.Info(VulkanFramesInFlight.ActiveDescription(framesInFlight));

                // THE ONE ALLOCATOR (V-M1) is built by the constructor, from the native seam and the memory facts
                // handed in here. It is the device that owns the retire list its chunk destroys go through, so
                // wiring the hook out here would mean handing that list out before the device exists, or building
                // a second one and silently splitting the deferred destroys across two.
                var created = new VulkanGpuDevice(lease, device, graphicsQueue, read.GraphicsQueueFamily,
                    ReadCapabilities(read, features), candidates[chosen].IsSoftwareRasterizer, liveness, loss,
                    timeline, new VulkanDeviceMemoryApi(vk, device, loss, liveness), read.Memory,
                    new VulkanCommandApi(vk, device, graphicsQueue, read.GraphicsQueueFamily, semaphore.Handle,
                        loss, liveness),
                    // THE RESOURCE SEAMS (row 9). Both are pure driver-call adapters: everything that decides
                    // anything about a resource sits above them in device-free types, which is what lets the
                    // usage derivation, the eager view set, the resting layouts, the staging arithmetic and the
                    // sampler mapping all run under dotnet test with no loader.
                    new VulkanResourceApi(vk, device, loss, liveness),
                    new VulkanSetupSink(vk),
                    // THE DESCRIPTOR SEAM (row 10), a pure driver-call adapter for the same reason the two above
                    // are: the type mapping, the content dedup, the pool sizing, the per-type accounting and the
                    // bind window all sit above it in device-free types.
                    new VulkanDescriptorApi(vk, device, loss, liveness),
                    // THE SHADER SEAM (row 16), two driver calls wide, because Vulkan consumes SPIR-V and the
                    // whole shader path above it is the engine's existing front end plus a hash-keyed dedup.
                    new VulkanShaderApi(vk, device, loss, liveness),
                    // THE PIPELINE SEAM (row 13), a pure driver-call adapter like the four above it: the vertex
                    // input derivation, the blend attachment count, the dynamic state list and the whole disk
                    // cache all sit above it in device-free types.
                    new VulkanPipelineApi(vk, device, loss, liveness),
                    // The device's own pipeline cache identity, off the SAME physical-device read, so the file the
                    // cache is seeded from is keyed on the device that was actually selected (V-S7).
                    read.PipelineCacheIdentity,
                    // The device's own maxDescriptorSetUniformBuffersDynamic, read off the SAME physical-device
                    // read the support probe gated on, so 8.3's third and fourth defences measure against one
                    // number rather than two reads that can disagree.
                    read.Facts.MaxDescriptorSetUniformBuffersDynamic,
                    framesInFlight,
                    // THE WINDOWED HALF, or null for a headless device, which is a real state rather than an
                    // unfinished one. The swapchain seam is built here rather than earlier because it resolves
                    // per-DEVICE entry points and there was no device until three lines ago.
                    window is null
                        ? null
                        : new VulkanWindowedParts(
                            surfaces!,
                            new VulkanSwapchainApi(vk, instance.Handle, device, graphicsQueue, loss, liveness),
                            surface,
                            new VulkanExtent(window.Request.Width, window.Request.Height),
                            window.Request.SyncToVerticalBlank,
                            window.Acquire));

                log.Info(created.Memory.Describe());

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
            in VulkanFeatureSelection features, bool windowed)
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

            // ONE DEVICE EXTENSION ON THE WINDOWED PATH AND NONE AT ALL ON THE HEADLESS ONE (V-N6).
            // VK_KHR_swapchain is windowed-only, and asking for it on a runner with no display server would fail
            // creation outright, which is every runner the golden suite has.
            nint extensionNames = windowed
                ? SilkMarshal.StringArrayToPtr(new[] { VulkanInstanceLayout.SwapchainDeviceExtension })
                : 0;

            try
            {
                var createInfo = new DeviceCreateInfo(
                    sType: StructureType.DeviceCreateInfo,
                    pNext: &features2,
                    queueCreateInfoCount: 1,
                    pQueueCreateInfos: &queueInfo,
                    enabledExtensionCount: windowed ? 1u : 0u,
                    ppEnabledExtensionNames: (byte**)extensionNames,
                    // NO LAYERS. Device layers were deprecated in Vulkan 1.0.13 and modern loaders ignore them
                    // entirely, so the incumbent's device-layer list is dead weight that reads as a working
                    // configuration.
                    enabledLayerCount: 0,
                    ppEnabledLayerNames: null,
                    // Null, deliberately: the features travel through the pNext chain above, and passing both is
                    // a spec violation (VUID-VkDeviceCreateInfo-pNext-00373).
                    pEnabledFeatures: null);

                VulkanResultCodes.Require(vk.CreateDevice(physicalDevice, in createInfo, null, out Device device),
                    "vkCreateDevice");
                return device;
            }
            finally
            {
                // Freed on every path including the throwing one: a failed creation that leaked its own argument
                // list would leak once per failed attempt, and the failed-attempt path is the one a fallback
                // retries.
                if (extensionNames != 0) SilkMarshal.Free(extensionNames);
            }
        }

        // Section 14's table, assembled in VulkanCapabilityRead so every rule that decides what the engine
        // believes about the device is a plain [Fact] on a machine with no loader (row 18,
        // https://github.com/APKiwiOrg/KhaozEngine/issues/528). This method is the three device answers and
        // nothing else: the reported name, the anisotropy bit the feature chain settled, and the R32_SFLOAT
        // format-properties read.
        static GpuCapabilities ReadCapabilities(in VulkanPhysicalDeviceRead read,
            in VulkanFeatureSelection features)
            => VulkanCapabilityRead.Assemble(
                deviceName: read.ReportedDeviceName,
                samplerAnisotropy: features.SamplerAnisotropy,
                supportsShadowMaps: read.SupportsShadowMapFormat,
                // NOT COMPUTED HERE, and not invented anywhere. V-C5 rules that the incumbent's own
                // GetSampleCountLimit is read off and reproduced, which is row 15
                // (https://github.com/APKiwiOrg/KhaozEngine/issues/525), the row that also needs the number for
                // its resolve. Until it lands the capability under-promises rather than guessing: a formula
                // invented here would be a silent lie AntiAliasing.ResolveFor acts on, and it would make row
                // 18's "asserted identical" pass or fail on luck.
                maxMsaaSampleCount: VulkanCapabilityRead.NoMultisampling);

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
