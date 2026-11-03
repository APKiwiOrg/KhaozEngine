namespace KhaozEngine.Gpu.Metal;

/// <summary>The rules of the MetalBackendCreation refusal family.</summary>
public enum MetalBackendCreationRefusal
{
    /// <summary>Refuses a Cocoa window that has no content view.</summary>
    ContentViewMissing,
    /// <summary>Refuses settings not owned by the Metal provider.</summary>
    ForeignDiagnosticSettings,
    /// <summary>Refuses Metal creation when the machine probe reports a missing requirement.</summary>
    MachineUnsupported,
    /// <summary>Refuses creation when a CAMetalLayer cannot be materialized.</summary>
    NativeLayerUnavailable,
    /// <summary>Refuses constructing a native Metal resource on another platform.</summary>
    NativeResourceUnavailableOnPlatform,
    /// <summary>Refuses native Metal creation off macOS.</summary>
    OperatingSystemUnsupported,
    /// <summary>Refuses a null Cocoa window handle.</summary>
    WindowHandleMissing,
    /// <summary>Refuses a Metal swapchain window that is not Cocoa.</summary>
    WindowKindUnsupported,
}
