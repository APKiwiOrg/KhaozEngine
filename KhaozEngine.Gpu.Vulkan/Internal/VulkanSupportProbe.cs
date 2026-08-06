using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The READING half of the support probe behind
    /// <see cref="KhaozEngine.Gpu.IGpuBackendProvider.IsSupported"/>: resolve the loader, create a THROWAWAY
    /// instance at the 1.3 floor, read every physical device, and hand each device's values to
    /// <see cref="VulkanDeviceRequirements"/> to be judged. Section 4.1 and 5.2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>.
    /// <para>
    /// FUNCTIONAL rather than a guess, and that is the whole point of the row. The machine is asked, not the
    /// operating system: a Linux box with no ICD, a driver stuck at 1.2 and a device below the descriptor limit
    /// are three different sentences here, and every one of them routes through the reported fallback rather than
    /// through a crash on frame one. On this repo's own developer machines, which are macOS with no Vulkan loader
    /// at all, the first read answers and the rest is never reached.
    /// </para>
    /// <para>
    /// THREE MACHINE STATES, not two, and the third is the one this file was written blind to. A machine with NO
    /// LOADER answers at the very first read (<see cref="NoLoaderResolved"/>). A machine with a LOADER AND AN ICD
    /// answers null and gets a real device. Between them sits a machine with a LOADER AND NO DRIVER
    /// (<see cref="NoDriverInstalled"/>), which is the ordinary state of a bare CI image and of most servers: the
    /// loader resolves, <c>vkEnumerateInstanceVersion</c> answers with the LOADER's version, and the first call
    /// that knows there is nothing behind it is <c>vkCreateInstance</c>. Both of that state's two spellings, the
    /// <c>VK_ERROR_INCOMPATIBLE_DRIVER</c> refusal and an instance that creates and then enumerates zero devices,
    /// report the SAME sentence, because they are the same machine.
    /// </para>
    /// <para>
    /// THE INSTANCE IS DESTROYED BEFORE THIS RETURNS, on every path, which is why the decision is taken over a
    /// snapshot (<see cref="VulkanDeviceFacts"/>) rather than over live handles. The probe deliberately does NOT
    /// use the refcounted process instance row 4 created
    /// (<see cref="VulkanInstance"/>): it has to answer before any device exists, which is before that instance is
    /// allowed to, and the lifecycle test asserts that asking the probe leaves that refcount at zero. The
    /// physical-device WALK is shared, through <see cref="VulkanPhysicalDeviceReader"/>, which is where row 3's
    /// handoff put it: one walk, one requirement list, so the probe and device creation cannot disagree.
    /// </para>
    /// <para>
    /// NOTHING IS CACHED HERE, mirroring <c>D3D11FeatureProbe</c>, and every caller that wants an answer more
    /// than once memoizes it at its own lifetime. <c>GpuBackendSelector.IsBackendSupported</c> caches the
    /// per-backend boolean for the process and drops it when a registration replaces the answerer, and
    /// <see cref="VulkanBackendProvider"/> memoizes the sentence for the lifetime of ONE provider instance, which
    /// is the same lifetime. A cache in here would be a third one, owned by a static that no re-registration can
    /// reach.
    /// </para>
    /// </summary>
    internal static unsafe class VulkanSupportProbe
    {
        /// <summary>
        /// The sentence for a machine with NO Vulkan loader on it at all. A named constant rather than a literal
        /// because two readers need to recognise the state and not merely read it: the provider's creation
        /// refusal quotes it, and the integration test branches on it to assert the right half of the contract on
        /// whatever machine it is running on.
        /// </summary>
        internal const string NoLoaderResolved =
            "no Vulkan loader could be resolved on this machine, so there is no libvulkan on the search path at "
            + "all, which is the expected state on macOS, where this package loads harmlessly and is never "
            + "selected";

        /// <summary>
        /// The sentence for a machine with a Vulkan LOADER and no DRIVER behind it, which is one machine state
        /// with two spellings: <c>vkCreateInstance</c> answering <c>VK_ERROR_INCOMPATIBLE_DRIVER</c>, and an
        /// instance that creates and then enumerates zero physical devices. Named once so the two paths cannot
        /// drift into describing the same machine two different ways, and so the reader is told the fix rather
        /// than only the fault.
        /// </summary>
        internal const string NoDriverInstalled =
            "a Vulkan loader is installed on this machine but no Vulkan driver (ICD) is, so there is nothing "
            + "behind the loader for an instance to run on. On a bare CI runner or a headless server that is the "
            + "expected state, and installing mesa-vulkan-drivers, which brings the lavapipe software "
            + "rasterizer, is what makes the native Vulkan backend real there";

        /// <summary>
        /// Null when this machine can run the native Vulkan backend, or a sentence saying what is missing, phrased
        /// for a log line a player or a tester will read. Never returns an empty string, so null is the only "yes".
        /// <para>
        /// The caller swallows exceptions: the provider contract says the probe must NEVER throw, because "we
        /// could not even ask" and "no" are the same answer to the settings screen and the fallback that consume
        /// it. The two failures worth a WORDED answer rather than a swallowed exception are caught here, because
        /// a machine with no loader and a machine with a 1.0 loader are the two most likely "no" answers in the
        /// fleet and both deserve to say so.
        /// </para>
        /// </summary>
        internal static string? MissingRequirement()
        {
            Vk vk;
            try
            {
                vk = Vk.GetApi();
            }
            catch (Exception ex)
            {
                // No loader at all, which is machine state one. The common case on macOS, where this package
                // ships, is never selected, and phase 4 brings a real Metal backend.
                return NoLoaderResolved + " (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            uint loaderVersion = 0;
            try
            {
                Result versionResult = vk.EnumerateInstanceVersion(ref loaderVersion);
                if (versionResult != Result.Success)
                {
                    return "the Vulkan loader could not report its instance version (vkEnumerateInstanceVersion "
                        + "returned " + versionResult + ")";
                }
            }
            catch (Exception ex)
            {
                // vkEnumerateInstanceVersion is itself a 1.1 entry point, so a loader that does not export it is
                // a Vulkan 1.0 loader and is three versions below this backend's floor. Caught rather than left
                // to the provider's blanket catch so the message says which fact was missing.
                return "the Vulkan loader exposes no vkEnumerateInstanceVersion, which means a 1.0 loader, and "
                    + "this backend needs 1.3 (" + ex.GetType().Name + ")";
            }

            if (loaderVersion < VulkanDeviceRequirements.MinimumApiVersion)
            {
                return "the Vulkan loader reports instance version "
                    + VulkanDeviceRequirements.FormatApiVersion(loaderVersion)
                    + ", below the 1.3 floor this backend is built on";
            }

            // V-N2's clamp, kept in its stated form rather than simplified to the constant. The comparison above
            // already proves which side wins, and writing the min anyway is what keeps this line readable as the
            // decision it implements when somebody moves the floor.
            uint requestedVersion = Math.Min(loaderVersion, VulkanDeviceRequirements.MinimumApiVersion);
            return MissingRequirementThroughInstance(vk, requestedVersion);
        }

        // The throwaway instance and everything under it. No layers, no extensions: the headless path enables no
        // surface extension (V-N6) and the validation knob is a device-creation concern, so the smallest instance
        // that can enumerate devices is the right one to ask through.
        static string? MissingRequirementThroughInstance(Vk vk, uint apiVersion)
        {
            var applicationInfo = new ApplicationInfo(
                sType: StructureType.ApplicationInfo, apiVersion: apiVersion);

            var createInfo = new InstanceCreateInfo(
                sType: StructureType.InstanceCreateInfo, pApplicationInfo: &applicationInfo);

            Result created = vk.CreateInstance(in createInfo, null, out Instance instance);
            if (created == Result.ErrorIncompatibleDriver)
            {
                // MACHINE STATE TWO, and the state that turned main red on the plain ubuntu-latest runner (CI run
                // 31062315211). Every read above this line answers on a loader with no ICD behind it, because a
                // loader answers them out of its own version rather than a driver's, so this is the first call
                // that can know. Reported as the machine fact it is rather than by its result code, and folded
                // into the same sentence the zero-device path below uses, because the two are one machine.
                return NoDriverInstalled + " (vkCreateInstance returned " + VulkanResultCodes.Token(created) + ")";
            }

            if (created != Result.Success)
            {
                return "a Vulkan instance could not be created at apiVersion "
                    + VulkanDeviceRequirements.FormatApiVersion(apiVersion) + " (vkCreateInstance returned "
                    + VulkanResultCodes.Token(created) + ")";
            }

            try
            {
                return MissingRequirementAcrossDevices(vk, instance);
            }
            finally
            {
                // Every path, including the one that found a device it likes. Nothing above this line may outlive
                // the instance, which is what makes the facts a copied snapshot rather than a handle.
                vk.DestroyInstance(instance, null);
            }
        }

        // Null as soon as ANY physical device meets the requirements, which is the same "first device that
        // qualifies" shape V-N3's default selection takes. When none does, every device's own reason is reported,
        // because on a two-device machine the interesting information is usually why the OTHER one was rejected.
        static string? MissingRequirementAcrossDevices(Vk vk, Instance instance)
        {
            uint deviceCount = 0;
            Result counted = vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
            if (counted != Result.Success && counted != Result.Incomplete)
            {
                return "the physical devices could not be enumerated (vkEnumeratePhysicalDevices returned "
                    + counted + ")";
            }

            if (deviceCount == 0)
            {
                // Machine state two's OTHER spelling. Some loaders refuse at vkCreateInstance and some create an
                // instance with nothing under it, and a reader who has to tell those apart has been given a
                // distinction that costs them time and buys them nothing.
                return NoDriverInstalled + " (the instance created and vkEnumeratePhysicalDevices found none)";
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* handles = devices)
            {
                Result filled = vk.EnumeratePhysicalDevices(instance, &deviceCount, handles);
                if (filled != Result.Success && filled != Result.Incomplete)
                {
                    return "the physical devices could not be read (vkEnumeratePhysicalDevices returned "
                        + filled + ")";
                }
            }

            var rejections = new List<string>();
            for (uint i = 0; i < deviceCount; i++)
            {
                VulkanDeviceFacts facts = VulkanPhysicalDeviceReader.Read(vk, devices[i]).Facts;

                // presentationRequired: false. IsSupported() receives no window, so there is no VkSurfaceKHR to
                // ask about, and building one would need a platform surface extension the headless path is not
                // allowed to enable. The windowed clause is evaluated at swapchain creation against the same
                // method. VulkanDeviceRequirements.MissingRequirement documents the whole split.
                string? missing = VulkanDeviceRequirements.MissingRequirement(facts, presentationRequired: false);
                if (missing is null) return null;

                rejections.Add(facts.DeviceName + ": " + missing);
            }

            return "no physical device on this machine can run the native Vulkan backend. "
                + string.Join(". ", rejections);
        }

        // Every physical-device read goes through VulkanPhysicalDeviceReader, which is where they moved when
        // row 4 needed the same four calls plus the feature bits it enables and the queue family it creates. Row
        // 3's handoff resolved the overlap this way, from the probe side: two copies of a physical-device walk
        // would drift the day a requirement moved, and the failure would be the worst available, a probe that
        // says yes and a creation that then refuses.
    }
}
