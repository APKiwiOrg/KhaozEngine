namespace KhaozEngine.Gpu.Metal;

/// <summary>Why MetalArgumentBatch refused an operation.</summary>
public enum MetalArgumentBatchRefusal
{
    /// <summary>Refuses two entries targeting the same argument index in one space and shader stage.</summary>
    DuplicateArgumentIndex,
}
