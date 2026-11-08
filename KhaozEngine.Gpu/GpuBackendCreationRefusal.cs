namespace KhaozEngine.Gpu;

/// <summary>Shared provider rules that refuse GPU backend creation.</summary>
public enum GpuBackendCreationRefusal
{
    /// <summary>Refuses headless creation with diagnostic settings not owned by the requested backend.</summary>
    ForeignDiagnosticSettings,
}
