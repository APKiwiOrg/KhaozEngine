using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE NATIVE HALF OF DECISION V-G3: the <c>VK_EXT_debug_utils</c> messenger, its callback, and the object
    /// naming that makes a validation message name a buffer instead of a handle (V-G5). Created with the shared
    /// instance when <c>KE_VULKAN_VALIDATION</c> asks for it, and destroyed with it.
    /// <para>
    /// <b>THE CALLBACK'S SHAPE IS FORCED BY THE BINDING, and row 1's spike found it.</b> Silk.NET types
    /// <c>PfnDebugUtilsMessengerCallbackEXT</c> as a CDECL function pointer, so the callback MUST be
    /// <c>[UnmanagedCallersOnly(CallConvs = [CallConvCdecl])]</c>: a plain static method is CS8786 at compile time
    /// rather than a silently wrong ABI, which is the good failure direction. That attribute also means the
    /// callback CANNOT CAPTURE, so whatever it logs through has to be reachable statically, which is what
    /// <see cref="_active"/> is. Passing the pump through <c>pUserData</c> was the alternative and is worse: it
    /// would need a pinned <c>GCHandle</c> whose lifetime is the messenger's, for a process that has exactly one
    /// instance and therefore exactly one messenger.
    /// </para>
    /// <para>
    /// <b>THE CALLBACK NEVER THROWS AND NEVER BREAKS.</b> The incumbent's throws a managed exception and calls
    /// <c>Debugger.Break()</c> from inside a native driver callback. This one copies the message out into managed
    /// strings, hands it to <see cref="VulkanValidationPump"/>, which is itself catch-all, and returns
    /// <c>VK_FALSE</c>, which is what the spec requires of an application callback: returning true aborts the call
    /// that raised the message and is reserved for the layers' own testing.
    /// </para>
    /// <para>
    /// <b>SEVERITY IS SUBSCRIBED AT WARNING AND ERROR</b> and never at info or verbose. The verbose rung on a
    /// modern validation layer includes per-object creation chatter, which on this engine's load path is tens of
    /// thousands of callbacks that carry no defect report at all, and a rate limiter that has to throw those away
    /// is paying for them twice.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanDebugMessenger : IDisposable
    {
        static readonly ILogger log = Log.For<VulkanDebugMessenger>();

        // The statically reachable sink the CDECL callback logs through, which is the constraint row 1's spike
        // recorded. One process instance means one messenger, so one field is enough and a registry would be a
        // second lifetime to get wrong. Written before the messenger exists and cleared before it is destroyed
        // (see Dispose), on purpose: a callback that lands in the teardown window finds this null and drops the
        // message, which is the safe direction, rather than logging through a pump whose native handles are
        // already being torn down.
        static VulkanValidationPump? _active;

        readonly Vk _vk;
        readonly ExtDebugUtils _debugUtils;
        readonly Instance _instance;
        readonly DebugUtilsMessengerEXT _messenger;

        bool _disposed;

        VulkanDebugMessenger(Vk vk, ExtDebugUtils debugUtils, Instance instance, DebugUtilsMessengerEXT messenger,
            VulkanValidationPump pump)
        {
            _vk = vk;
            _debugUtils = debugUtils;
            _instance = instance;
            _messenger = messenger;
            Pump = pump;
        }

        /// <summary>The sink every message lands in, and the thing <c>strict</c>'s controlled throw is asked
        /// through. Held here so the device can reach it without reaching the messenger's native handles.</summary>
        internal VulkanValidationPump Pump { get; }

        /// <summary>
        /// Create the messenger on <paramref name="instance"/>, or return null when the loader has no
        /// <c>VK_EXT_debug_utils</c> entry points. Null is not a failure: it means the extension was requested and
        /// the loader could not supply it, which the caller reports and carries on from, because a session that
        /// cannot start because its diagnostics could not start is worse than a session with no diagnostics.
        /// </summary>
        internal static VulkanDebugMessenger? TryCreate(Vk vk, Instance instance, VulkanValidationMode mode,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ILogger sink = logger ?? log;

            if (!vk.TryGetInstanceExtension(instance, out ExtDebugUtils debugUtils, ExtDebugUtils.ExtensionName))
            {
                sink.Warn($"{VulkanValidation.EnvVarName} asked for Vulkan validation and this loader offers no "
                    + "VK_EXT_debug_utils entry points, so no messenger was created and this run reports no "
                    + "validation messages. The layer may still be installed: this is the loader half.");
                return null;
            }

            var pump = new VulkanValidationPump(mode, logger: logger);
            // Published BEFORE the messenger exists, because the driver may call back during
            // vkCreateDebugUtilsMessengerEXT itself on some layers, and a callback that found this null would
            // throw the message away at exactly the moment the session is proving the lever works.
            Volatile.Write(ref _active, pump);

            var info = new DebugUtilsMessengerCreateInfoEXT(
                sType: StructureType.DebugUtilsMessengerCreateInfoExt,
                messageSeverity: DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                                 | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                messageType: DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                             | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                             | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                pfnUserCallback: new PfnDebugUtilsMessengerCallbackEXT(&OnMessage));

            Result created = debugUtils.CreateDebugUtilsMessenger(instance, in info, null,
                out DebugUtilsMessengerEXT messenger);
            if (created != Result.Success)
            {
                Volatile.Write(ref _active, null);
                debugUtils.Dispose();
                sink.Warn($"{VulkanValidation.EnvVarName} asked for Vulkan validation and "
                    + $"vkCreateDebugUtilsMessengerEXT returned {VulkanResultCodes.Token(created)}, so this run "
                    + "reports no validation messages. Rendering is unaffected.");
                return null;
            }

            return new VulkanDebugMessenger(vk, debugUtils, instance, messenger, pump);
        }

        /// <summary>
        /// Give a Vulkan object a name the layer and a capture will use (V-G5). Best effort by design: a failed
        /// naming call is a lost label, never a reason to fail creation of the thing being named.
        /// </summary>
        /// <param name="device">The device the object belongs to.</param>
        /// <param name="type">The object's <c>VkObjectType</c>. Nothing type-checks this against
        /// <paramref name="handle"/>, because the naming call takes a raw <c>ulong</c>, so a mismatch shows up as
        /// the name being on the wrong object rather than as a compile error.</param>
        /// <param name="handle">The object's raw handle value.</param>
        /// <param name="name">The name, which reaches the driver as UTF-8.</param>
        internal void NameObject(Device device, ObjectType type, ulong handle, string name)
        {
            if (_disposed || string.IsNullOrEmpty(name)) return;

            byte* utf8 = (byte*)SilkMarshal.StringToPtr(name, NativeStringEncoding.UTF8);
            try
            {
                var info = new DebugUtilsObjectNameInfoEXT(
                    sType: StructureType.DebugUtilsObjectNameInfoExt,
                    objectType: type,
                    objectHandle: handle,
                    pObjectName: utf8);

                Result named = _debugUtils.SetDebugUtilsObjectName(device, in info);
                if (named != Result.Success)
                {
                    log.Info($"Naming the Vulkan {type} '{name}' returned {VulkanResultCodes.Token(named)}, so "
                        + "validation messages about it will name its handle instead. Nothing else is affected.");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)utf8);
            }
        }

        /// <summary>Destroy the messenger. Called while the instance is still alive, because the messenger is a
        /// child of it, and it clears the static sink so a late callback out of the loader's own teardown finds
        /// nothing rather than a pump whose logger may already be gone.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Volatile.Write(ref _active, null);
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _messenger, null);
            _debugUtils.Dispose();
            GC.KeepAlive(_vk);
        }

        // THE CALLBACK. Static, non-capturing, CDECL, never throwing, and logging through statically reachable
        // state, which is every constraint row 1's spike recorded, in one signature.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static Bool32 OnMessage(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT types,
            DebugUtilsMessengerCallbackDataEXT* data,
            void* userData)
        {
            try
            {
                VulkanValidationPump? pump = Volatile.Read(ref _active);
                if (pump is null || data is null) return false;

                // Copied out into managed strings HERE. Nothing the driver handed in outlives this callback, and
                // a pump that held the pointers would be reading freed memory the moment it logged.
                string idName = SilkMarshal.PtrToString((nint)data->PMessageIdName) ?? string.Empty;
                string text = SilkMarshal.PtrToString((nint)data->PMessage) ?? string.Empty;

                pump.Report(new VulkanValidationMessage(
                    Severity(severity), data->MessageIdNumber, idName, Describe(types) + text));
            }
            catch
            {
                // The last line of defence, and it is deliberately empty. An exception unwinding from here goes
                // through native driver frames, which is undefined behaviour and destroys the stack the message
                // was about. Losing one message is the cheaper failure by a wide margin.
            }

            // VK_FALSE, always. Returning true aborts the Vulkan call that raised the message and is reserved for
            // the validation layers' own testing.
            return false;
        }

        static VulkanValidationSeverity Severity(DebugUtilsMessageSeverityFlagsEXT severity)
        {
            if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
                return VulkanValidationSeverity.Error;
            if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
                return VulkanValidationSeverity.Warning;
            if ((severity & DebugUtilsMessageSeverityFlagsEXT.InfoBitExt) != 0)
                return VulkanValidationSeverity.Info;
            return VulkanValidationSeverity.Verbose;
        }

        // The type flags are a prefix rather than a field, because they are only ever read alongside the text and
        // a PERFORMANCE message and a VALIDATION message with the same body are genuinely different reports.
        static string Describe(DebugUtilsMessageTypeFlagsEXT types)
        {
            if ((types & DebugUtilsMessageTypeFlagsEXT.ValidationBitExt) != 0) return string.Empty;
            if ((types & DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt) != 0) return "[performance] ";
            return "[general] ";
        }
    }
}
