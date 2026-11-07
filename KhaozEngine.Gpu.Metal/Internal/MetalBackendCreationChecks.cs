using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Value-only post-probe admission of a native Metal device and queue.</summary>
internal static class MetalBackendCreationChecks
{
    internal static void RequireEligibleDeviceForTests(bool available, string? detail)
    {
        if (available) return;
        throw new MetalBackendCreationStateException(MetalBackendCreationRefusal.EligibleDeviceMissing,
            "No Metal device on this machine can run the native Metal backend, on a machine whose support "
            + "probe answered yes. " + (detail ?? "No reason was recorded.")
            + " The probe and this call go through the same acquisition and ask the same requirement method, "
            + "so a disagreement means the machine changed between them.")
        { Detail = detail };
    }

    internal static void RequireCommandQueueForTests(bool available, string deviceName)
    {
        if (available) return;
        throw new MetalBackendCreationStateException(MetalBackendCreationRefusal.CommandQueueUnavailable,
            "The Metal device '" + deviceName + "' would not create a command queue. That is a device "
            + "under resource pressure rather than a device below the floor, since -newCommandQueue is the "
            + "first allocation this backend makes and it takes no arguments to get wrong.")
        { DeviceName = deviceName };
    }
}
