using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-N1, V-N2 and V-N6: the ONE <c>VkInstance</c> this process ever has, refcounted, created on the
    /// first device and destroyed with the last.
    /// <para>
    /// <b>THE VERSION IS ASKED FOR RATHER THAN ASSUMED</b> (V-N2):
    /// <c>apiVersion = min(VK_API_VERSION_1_3, vkEnumerateInstanceVersion())</c>. The incumbent hardcodes
    /// <c>1.0.0</c> at two sites and never calls <c>vkEnumerateInstanceVersion</c> at all, which is why everything
    /// past 1.0 has to arrive there as an extension. Here 1.3 is the floor the whole design is built on, so a
    /// loader below it is turned away by the probe before this type is reached.
    /// </para>
    /// <para>
    /// <b>A MISSING LAYER OR EXTENSION IS DROPPED WITH A WARNING, never a refusal.</b>
    /// <c>vkCreateInstance</c> fails outright on a layer or extension that is not installed, so requesting
    /// <c>VK_LAYER_KHRONOS_validation</c> on a machine without the validation layers would stop the app starting
    /// for somebody who is by definition mid-diagnosis. Both lists are checked against what the loader actually
    /// enumerates and the absent entries are dropped, which is the same shape the Direct3D 11 debug layer's
    /// retry-without takes.
    /// </para>
    /// <para>
    /// <b>THIS IS NOT THE PROBE'S INSTANCE.</b> <see cref="VulkanSupportProbe"/> creates and destroys its own
    /// throwaway one, because it has to answer before any device exists, and it takes no lease: the lifecycle
    /// test asserts that asking the probe leaves the shared refcount at zero.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanInstance : IDisposable
    {
        static readonly ILogger log = Log.For<VulkanInstance>();

        // The process-wide refcount. The bookkeeping lives in VulkanInstanceRefCount, over an injected factory,
        // which is what makes the create-and-destroy-many-devices lifecycle testable without a loader.
        static readonly VulkanInstanceRefCount<VulkanInstance> _shared =
            new(CreateNative, static instance => instance.Dispose());

        bool _disposed;

        VulkanInstance(Vk api, Instance handle, uint apiVersion, VulkanValidationMode validation,
            VulkanDebugMessenger? messenger)
        {
            Api = api;
            Handle = handle;
            ApiVersion = apiVersion;
            Validation = validation;
            Messenger = messenger;
        }

        /// <summary>The loaded Vulkan entry points. One <c>Vk</c> for the instance, shared by every device on
        /// it.</summary>
        internal Vk Api { get; }

        /// <summary>The instance handle itself.</summary>
        internal Instance Handle { get; }

        /// <summary>The <c>VkApplicationInfo.apiVersion</c> this instance was created with, which is
        /// <c>min(1.3, loader version)</c> and is therefore 1.3 on every machine that got this far.</summary>
        internal uint ApiVersion { get; }

        /// <summary>The validation rung this instance was created on. Fixed for its lifetime, because the layer
        /// is an instance-creation argument.</summary>
        internal VulkanValidationMode Validation { get; }

        /// <summary>The debug-utils messenger, or null when validation is off or the loader could not supply it.
        /// Object naming and the strict latch both go through it.</summary>
        internal VulkanDebugMessenger? Messenger { get; }

        /// <summary>How many devices currently hold the shared instance. THE number the lifecycle test asserts
        /// reaches zero, and the one the probe must not move.</summary>
        internal static int LeaseCount => _shared.Count;

        /// <summary>Whether a shared instance currently exists. A count of zero and a live instance is exactly the
        /// failure the lifecycle test is written to catch, so the two are asked separately.</summary>
        internal static bool IsLive => _shared.IsLive;

        /// <summary>Claim the shared instance for <paramref name="key"/>, creating it when this is the first
        /// device. Release the lease to give the claim up.</summary>
        internal static VulkanInstanceLease<VulkanInstance> Acquire(in VulkanInstanceKey key)
            => _shared.Acquire(key);

        /// <summary>Destroy the instance. Called by the refcount when the last lease goes, never directly: a
        /// device that destroyed the instance itself would take every other device's with it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // The messenger is a child of the instance, so it goes FIRST. Destroying the instance with a live
            // messenger on it is undefined behaviour, and it is also how a late callback finds a torn-down sink.
            Messenger?.Dispose();
            Api.DestroyInstance(Handle, null);
            Api.Dispose();
        }

        // The native creation, in the order the loader wants it: version, then what is actually installed, then
        // the request built from the intersection, then the messenger.
        static VulkanInstance CreateNative(VulkanInstanceKey key)
        {
            Vk vk;
            try
            {
                vk = Vk.GetApi();
            }
            catch (Exception ex)
            {
                // Caught and RENAMED rather than left to escape, because what comes out of the loader here is a
                // DllNotFoundException or an EntryPointNotFoundException naming a native library, and a tester
                // who selected this backend reads that as the engine being broken. This is the one failure that
                // is about the MACHINE rather than about anything the caller did, and the D3D11 package answers
                // its equivalent with a named PlatformNotSupportedException off a static guard. Vulkan has no
                // static guard to answer from (V-P1), so the rename happens where the loader actually fails.
                throw new NotSupportedException(
                    "The native Vulkan backend cannot create a device on this machine: no Vulkan loader could be "
                    + $"resolved ({ex.GetType().Name}: {ex.Message}). This is a statement about the MACHINE and "
                    + "not about the package, so read GpuBackendSelector.IsBackendSupported to ask the same "
                    + "question without a device. macOS is the expected case in this fleet: there is no Vulkan "
                    + "loader there, this package loads harmlessly, and phase 4 of the native GPU program brings "
                    + "a real Metal backend.", ex);
            }

            uint loaderVersion = 0;
            VulkanResultCodes.Require(vk.EnumerateInstanceVersion(ref loaderVersion),
                "vkEnumerateInstanceVersion");

            // V-N2's clamp, written as the min it is rather than folded to the constant. The probe has already
            // refused a loader below the floor, so this always answers 1.3 today, and writing the min anyway is
            // what keeps the line readable as the decision it implements when somebody moves the floor.
            uint apiVersion = Math.Min(loaderVersion, VulkanDeviceRequirements.MinimumApiVersion);

            VulkanValidationMode validation = key.Validation;
            IReadOnlyList<string> wantedExtensions = key.Windowed
                ? VulkanInstanceLayout.WindowedInstanceExtensions(key.Window, validation)
                : VulkanInstanceLayout.HeadlessInstanceExtensions(validation);

            string[] extensions = Available(wantedExtensions, EnumerateExtensionNames(vk), "instance extension");
            string[] layers = Available(VulkanInstanceLayout.InstanceLayers(validation),
                EnumerateLayerNames(vk), "instance layer");

            if (VulkanValidation.WantsMessenger(validation) && Array.IndexOf(layers, VulkanValidation.LayerName) < 0)
            {
                log.Warn(VulkanValidation.LayerUnavailableWarning(validation));
            }

            Instance handle = Create(vk, apiVersion, extensions, layers, validation);

            VulkanDebugMessenger? messenger = VulkanValidation.WantsMessenger(validation)
                && Array.IndexOf(extensions, ExtDebugUtils.ExtensionName) >= 0
                    ? VulkanDebugMessenger.TryCreate(vk, handle, validation)
                    : null;

            if (validation != VulkanValidationMode.Off) log.Info(VulkanValidation.ActiveDescription(validation));

            return new VulkanInstance(vk, handle, apiVersion, validation, messenger);
        }

        // vkCreateInstance itself, with everything it needs pinned. The unmanaged string arrays are freed on every
        // path including the throwing one: a failed creation that leaked its own argument list would leak once per
        // failed attempt, and the failed-attempt path is the one a fallback retries.
        static Instance Create(Vk vk, uint apiVersion, string[] extensions, string[] layers,
            VulkanValidationMode validation)
        {
            nint applicationName = SilkMarshal.StringToPtr("KhaozEngine");
            nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
            nint layerNames = SilkMarshal.StringArrayToPtr(layers);

            try
            {
                var applicationInfo = new ApplicationInfo(
                    sType: StructureType.ApplicationInfo,
                    pApplicationName: (byte*)applicationName,
                    applicationVersion: 1,
                    pEngineName: (byte*)applicationName,
                    engineVersion: 1,
                    apiVersion: apiVersion);

                // The sync rung, chained into pNext (V-G3). The default validation layer does NOT run
                // synchronisation validation, so without this the sync rung would be the plain rung with a
                // different name, and a session that reported no race would have proved nothing.
                ValidationFeatureEnableEXT syncValidation =
                    ValidationFeatureEnableEXT.SynchronizationValidationExt;
                var validationFeatures = new ValidationFeaturesEXT(
                    sType: StructureType.ValidationFeaturesExt,
                    enabledValidationFeatureCount: 1,
                    pEnabledValidationFeatures: &syncValidation);

                var createInfo = new InstanceCreateInfo(
                    sType: StructureType.InstanceCreateInfo,
                    pNext: VulkanValidation.WantsSynchronizationValidation(validation) ? &validationFeatures : null,
                    pApplicationInfo: &applicationInfo,
                    enabledExtensionCount: (uint)extensions.Length,
                    ppEnabledExtensionNames: (byte**)extensionNames,
                    enabledLayerCount: (uint)layers.Length,
                    ppEnabledLayerNames: (byte**)layerNames);

                VulkanResultCodes.Require(vk.CreateInstance(in createInfo, null, out Instance instance),
                    "vkCreateInstance");
                return instance;
            }
            finally
            {
                SilkMarshal.Free(layerNames);
                SilkMarshal.Free(extensionNames);
                SilkMarshal.Free(applicationName);
            }
        }

        // The intersection of what was asked for and what the loader has, with every drop named. Naming them is
        // the whole value: an instance created without the debug-utils extension and a session that reports no
        // validation messages look identical from the outside, and one of those is a machine problem the tester
        // can fix in a minute.
        static string[] Available(IReadOnlyList<string> wanted, HashSet<string> present, string what)
        {
            var kept = new List<string>(wanted.Count);
            foreach (string name in wanted)
            {
                if (present.Contains(name)) kept.Add(name);
                else
                {
                    log.Warn($"The native Vulkan backend asked for the {what} '{name}' and this loader does not "
                        + "offer it, so it was dropped and the instance was created without it. Requesting it "
                        + "anyway would fail instance creation outright, which is a worse answer than going "
                        + "without a diagnostic.");
                }
            }
            return kept.ToArray();
        }

        static HashSet<string> EnumerateExtensionNames(Vk vk)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            uint count = 0;
            Result counted = vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null);
            if ((counted != Result.Success && counted != Result.Incomplete) || count == 0) return names;

            var properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* handles = properties)
            {
                Result filled = vk.EnumerateInstanceExtensionProperties((byte*)null, &count, handles);
                if (filled != Result.Success && filled != Result.Incomplete) return names;

                for (uint i = 0; i < count; i++)
                {
                    string? name = SilkMarshal.PtrToString((nint)handles[i].ExtensionName);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names;
        }

        static HashSet<string> EnumerateLayerNames(Vk vk)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            uint count = 0;
            Result counted = vk.EnumerateInstanceLayerProperties(&count, null);
            if ((counted != Result.Success && counted != Result.Incomplete) || count == 0) return names;

            var properties = new LayerProperties[count];
            fixed (LayerProperties* handles = properties)
            {
                Result filled = vk.EnumerateInstanceLayerProperties(&count, handles);
                if (filled != Result.Success && filled != Result.Incomplete) return names;

                for (uint i = 0; i < count; i++)
                {
                    string? name = SilkMarshal.PtrToString((nint)handles[i].LayerName);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names;
        }
    }
}
