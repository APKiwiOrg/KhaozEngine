using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Value-only post-probe native Vulkan device admission.</summary>
internal static class VulkanBackendCreationChecks
{
    internal static void RequirePinnedAdapterForTests(int chosen, string adapterKey)
    {
        if (chosen >= 0) return;
        throw new VulkanBackendCreationStateException(VulkanBackendCreationRefusal.PinnedAdapterUnavailable,
            "The pinned Vulkan UUID is unavailable or ineligible.")
        { AdapterKey = adapterKey };
    }

    internal static void RequirePhysicalDevicesForTests(uint count)
    {
        if (count != 0) return;
        throw new VulkanBackendCreationStateException(VulkanBackendCreationRefusal.PhysicalDevicesMissing,
            "The native Vulkan backend found no physical device, on a machine whose support probe "
            + "answered yes. That is a loader with no installed ICD behind it, and it means the machine "
            + "changed between the probe and this call.")
        { PhysicalDeviceCount = count };
    }

    internal static void RequireEligibleDeviceForTests(bool eligible, IReadOnlyList<VulkanPhysicalDeviceInfo> candidates)
    {
        if (eligible) return;
        var reasons = new List<string>(candidates.Count);
        var names = new string[candidates.Count];
        var rejections = new string?[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            names[i] = candidates[i].Name;
            rejections[i] = candidates[i].RejectionReason;
            reasons.Add(names[i] + ": " + (rejections[i] ?? "no reason recorded"));
        }
        throw new VulkanBackendCreationStateException(VulkanBackendCreationRefusal.EligibleDeviceMissing,
            "No physical device on this machine can run the native Vulkan backend, on a machine whose support "
            + "probe answered yes. Every device and its own reason: " + string.Join(". ", reasons)
            + ". The probe and this call ask the same requirement method, so a disagreement means the machine "
            + "changed between them.")
        { CandidateCount = candidates.Count, CandidateNames = names, CandidateRejections = rejections };
    }
}
