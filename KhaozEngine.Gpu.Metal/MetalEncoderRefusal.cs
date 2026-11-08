namespace KhaozEngine.Gpu.Metal;

/// <summary>The rules of the MetalEncoder refusal family.</summary>
public enum MetalEncoderRefusal
{
    /// <summary>Refuses a timestamp boundary while a native encoder remains open.</summary>
    EncoderStillOpen,
    /// <summary>Refuses opening an encoder with no native command-buffer recording in flight.</summary>
    RecordingMissing,
    /// <summary>Refuses opening a render encoder with a nil render-pass descriptor.</summary>
    RenderDescriptorMissing,
}
