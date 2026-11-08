namespace KhaozEngine.Gpu.Metal;

/// <summary>The rules of the MetalBindRecords refusal family.</summary>
public enum MetalBindRecordsRefusal
{
    /// <summary>Refuses a bind flush with a nil native encoder.</summary>
    EncoderMissing,
    /// <summary>Refuses binding a resource in an argument table different from shader reflection.</summary>
    IndexSpaceMismatch,
    /// <summary>Refuses flushing resource sets without a bound pipeline index table.</summary>
    PipelineTableMissing,
}
