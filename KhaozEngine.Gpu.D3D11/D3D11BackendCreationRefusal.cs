namespace KhaozEngine.Gpu.D3D11;

/// <summary>The rules of the D3D11BackendCreation refusal family.</summary>
public enum D3D11BackendCreationRefusal
{
    /// <summary>Refuses native D3D11 device creation off Windows.</summary>
    OperatingSystemUnsupported,
    /// <summary>Refuses a D3D11 swapchain window that is not Win32.</summary>
    WindowKindUnsupported,
    /// <summary>Refuses a null HWND for native D3D11 windowed creation.</summary>
    WindowHandleMissing,
    /// <summary>Refuses a runtime without the required versioned D3D11 context.</summary>
    RuntimeContextUnavailable,
    /// <summary>Refuses native creation success without its device and context outputs.</summary>
    NativeObjectsMissing,
}
