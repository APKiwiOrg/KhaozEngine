namespace KhaozEngine.Gpu.Metal;

/// <summary>Rules that refuse MetalTimestampAdapter operations.</summary>
public enum MetalTimestampAdapterRefusal
{
    /// <summary>Refuses a nil timestamp resource or encoder.</summary>
    NativeTimestampResourceUnavailable,

    /// <summary>Refuses a counter sampling boundary the Metal device does not support.</summary>
    SamplingBoundaryUnsupported,
}
