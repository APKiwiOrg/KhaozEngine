using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The outcome of asking this machine for the device <c>KE_METAL_DEVICE</c> names: the device itself, what
    /// was read off it, and the two lines a session log gets.
    /// </summary>
    /// <param name="Device">The chosen device at +1, which the CALLER releases. Null when nothing on this machine
    /// could be used, which is an answer rather than a failure: it is what the probe turns into a sentence and
    /// what creation turns into a refusal.</param>
    /// <param name="Facts">What was read off the device that WOULD have been used. On a machine with no eligible
    /// device that is the first enumerated one, so <see cref="MetalDeviceRequirements.MissingRequirement"/> still
    /// produces a specific sentence rather than a shrug.</param>
    /// <param name="LogLine">The INFO naming which device this session runs on and whether that was a selection,
    /// a substitution or the system default (M-N1's "any substitution is LOGGED").</param>
    /// <param name="Warning">The WARN for a request that could not be honoured, or null. Separate from
    /// <paramref name="LogLine"/> because they go to different levels and a soak reads them differently.</param>
    /// <param name="NoDeviceDetail">Every enumerated device and its own rejection reason, set only when
    /// <paramref name="Device"/> is null. On a two-device machine the interesting information is usually why the
    /// OTHER one was turned away.</param>
    internal readonly record struct MetalSelectedDevice(
        MTLDevice Device,
        MetalDeviceFacts Facts,
        string LogLine,
        string? Warning,
        string? NoDeviceDetail);

    /// <summary>
    /// THE OBJECTIVE-C HALF OF M-N1: acquire the device <c>KE_METAL_DEVICE</c> names, or the system default when
    /// it names nothing.
    /// <para>
    /// ONE ACQUISITION FOR TWO CALLERS, and that is the point. <see cref="MetalSupportProbe"/> asks so it can say
    /// whether this machine can run the backend, and <c>MetalGpuDevice</c> asks so it can create one. Row 2
    /// shipped the probe reading <c>MTLCreateSystemDefaultDevice()</c> unconditionally and recorded that as
    /// incomplete on the row-4 handoff: on a dual-GPU Mac with the variable set it answered about a device the
    /// backend would not use. Both now come through here, so the probe's yes and the creation path's yes are one
    /// answer asked twice rather than two answers that can disagree.
    /// </para>
    /// <para>
    /// THE DEFAULT PATH DOES NOT ENUMERATE AT ALL. <c>MTLCreateSystemDefaultDevice()</c> is what M-N1 pins as the
    /// default and what the incumbent calls, so an unset variable takes that function's answer rather than
    /// element zero of <c>MTLCopyAllDevices()</c>, which is a different choice on any machine where the two
    /// differ.
    /// </para>
    /// <para>
    /// OWNERSHIP IS EXPLICIT ON EVERY PATH, because Objective-C's rule is a naming convention rather than a type.
    /// Both <c>MTLCreateSystemDefaultDevice()</c> and <c>MTLCopyAllDevices()</c> follow the create/copy rule and
    /// hand back +1. Devices read out of the array are BORROWED, so the chosen one is retained before the array
    /// is released and the rest are simply dropped. Getting that backwards is a use-after-free that presents as a
    /// crash somewhere else entirely.
    /// </para>
    /// </summary>
    internal static class MetalDeviceEnumeration
    {
        /// <summary>
        /// Acquire the device this machine should use, at +1. Never throws for a bad variable value: an
        /// unhonourable request warns and falls back, which is the shape every lever in this fleet has.
        /// <para>
        /// The caller must already be inside an <see cref="ObjCAutoreleasePool"/> scope, because reading a device
        /// name goes through an autoreleased <c>NSString</c>.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalSelectedDevice AcquireSelected()
        {
            MetalDeviceRequest request = MetalDeviceSelection.FromEnvironment();
            return request.Kind == MetalDeviceRequestKind.Default
                ? AcquireSystemDefault()
                : AcquireEnumerated(request);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalSelectedDevice AcquireSystemDefault()
        {
            var device = new MTLDevice(MTLDevice.CreateSystemDefault());
            MetalDeviceFacts facts = MetalDeviceFactsReader.Read(device);

            if (device.IsNull)
            {
                return new MetalSelectedDevice(device, facts,
                    "Metal device selection: MTLCreateSystemDefaultDevice() returned nil, so this machine has no "
                    + "usable Metal device.", null, null);
            }

            // The requirement is checked HERE as well as by the caller, so the acquisition never hands back a
            // device the backend cannot use. On the default path that means releasing it again, which is cheap
            // and is what keeps "an ineligible device is never chosen" true on both paths rather than only on the
            // enumerated one.
            string? rejection = MetalDeviceRequirements.MissingRequirement(facts);
            if (rejection is not null)
            {
                device.Release();
                return new MetalSelectedDevice(default, facts,
                    "Metal device selection: the system default device ('" + facts.DeviceName
                    + "') cannot run this backend.", null, facts.DeviceName + ": " + rejection);
            }

            return new MetalSelectedDevice(device, facts,
                MetalDeviceSelection.DescribeSystemDefault(facts.DeviceName), null, null);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalSelectedDevice AcquireEnumerated(in MetalDeviceRequest request)
        {
            var all = new NSArray(MTLDevice.CopyAllDevices());
            try
            {
                nuint count = all.Count();
                var handles = new MTLDevice[count];
                var facts = new MetalDeviceFacts[count];
                var candidates = new MetalDeviceCandidate[count];

                for (nuint i = 0; i < count; i++)
                {
                    var device = new MTLDevice(all.ObjectAt(i));
                    handles[i] = device;
                    facts[i] = MetalDeviceFactsReader.Read(device);

                    // The SAME requirement method the probe answers through, over the same snapshot, which is
                    // what makes this one decision asked twice rather than two decisions that can disagree.
                    string? rejection = MetalDeviceRequirements.MissingRequirement(facts[i]);
                    candidates[i] = new MetalDeviceCandidate(facts[i].DeviceName, device.IsLowPower(),
                        device.IsRemovable(), device.IsHeadless(), device.RegistryId(),
                        MeetsRequirements: rejection is null, RejectionReason: rejection);
                }

                int chosen = MetalDeviceSelection.Choose(request, candidates, out string? warning);
                if (chosen == MetalDeviceSelection.NoDevice)
                {
                    return new MetalSelectedDevice(default,
                        count == 0 ? MetalDeviceFactsReader.Read(default) : facts[0],
                        "Metal device selection: no device on this machine meets the requirements.",
                        warning, DescribeRejections(candidates));
                }

                // RETAINED BEFORE THE ARRAY GOES, because -objectAtIndex: hands back the array's own reference
                // without retaining it. Releasing the array first would leave this handle pointing at a freed
                // object on any machine where the array held the last reference.
                handles[chosen].Retain();

                return new MetalSelectedDevice(handles[chosen], facts[chosen],
                    MetalDeviceSelection.Describe(chosen, request, candidates, requestHonoured: warning is null),
                    warning, null);
            }
            finally
            {
                // MTLCopyAllDevices follows the copy rule, so the array is +1 and ours to release. Its elements
                // are not.
                if (!all.IsNull) ObjCRuntime.ObjcRelease(all.Handle);
            }
        }

        static string DescribeRejections(IReadOnlyList<MetalDeviceCandidate> candidates)
        {
            if (candidates.Count == 0) return "this machine enumerates no Metal devices at all";

            var reasons = new List<string>(candidates.Count);
            foreach (MetalDeviceCandidate candidate in candidates)
                reasons.Add(candidate.Name + ": " + (candidate.RejectionReason ?? "no reason recorded"));
            return string.Join(". ", reasons);
        }
    }
}
