namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanBackendCreation refusal family.</summary>
public enum VulkanBackendCreationRefusal
{
    /// <summary>Refuses Cocoa presentation because the native Vulkan package does not carry the Metal translation layer.</summary>
    CocoaSurfaceUnsupported,
    /// <summary>Refuses settings not owned by the Vulkan provider.</summary>
    ForeignDiagnosticSettings,
    /// <summary>Refuses Vulkan creation when the machine support probe reports a missing requirement.</summary>
    MachineUnsupported,
    /// <summary>Refuses a window kind with no supported Vulkan surface extension.</summary>
    WindowKindUnsupported,
    /// <summary>Refuses native instance creation when the Vulkan loader cannot be resolved.</summary>
    LoaderUnavailable,
}
