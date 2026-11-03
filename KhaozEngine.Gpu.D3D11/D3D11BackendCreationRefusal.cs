namespace KhaozEngine.Gpu.D3D11;

/// <summary>The rules of the D3D11BackendCreation refusal family.</summary>
public enum D3D11BackendCreationRefusal
{
    /// <summary>Refuses settings not owned by the D3D11 provider.</summary>
    ForeignDiagnosticSettings,
    /// <summary>Refuses native D3D11 device creation off Windows.</summary>
    OperatingSystemUnsupported,
    /// <summary>Refuses a D3D11 swapchain window that is not Win32.</summary>
    WindowKindUnsupported,
}
