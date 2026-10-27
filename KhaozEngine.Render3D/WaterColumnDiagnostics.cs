namespace KhaozEngine.Render3D;

/// <summary>How the latest rendered frame made scene colour readable before water.</summary>
public enum WaterColumnCopyPath
{
    /// <summary>No opted-in plane was queued, so no colour transfer ran.</summary>
    None,
    /// <summary>The multisampled colour attachment resolved into the existing single-sample colour target.</summary>
    MsaaResolve,
    /// <summary>The single-sample colour target's mip zero copied into a separate texture.</summary>
    SingleSampleCopy,
}

/// <summary>Queued effective opt-ins, including culled planes, and the latest rendered frame's colour transfer.</summary>
public readonly record struct WaterColumnDiagnostics(int OptedInPlanes, bool CopyRan, WaterColumnCopyPath CopyPath);
