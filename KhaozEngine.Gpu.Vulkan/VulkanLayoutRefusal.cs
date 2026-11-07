namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanLayout refused an operation.</summary>
public enum VulkanLayoutRefusal
{
    /// <summary>Refuses a layout transition for a staging or null image handle.</summary>
    ImageHandleMissing,
    /// <summary>Refuses querying one layout for a range spanning narrower tracked pieces.</summary>
    NoSingleLayout,
    /// <summary>Refuses partially overlapping tracked subresource rectangles that neither contain one another.</summary>
    PartialOverlap,
    /// <summary>Refuses a wider transition whose uncovered remainder would need an unrepresentable barrier.</summary>
    UntrackedRemainder,
}
