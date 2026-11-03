namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanInstance refusal family.</summary>
public enum VulkanInstanceRefusal
{
    /// <summary>Refuses acquiring an instance configuration not satisfied by the live process-wide instance.</summary>
    ConfigurationIncompatible,
    /// <summary>Refuses an instance factory that returns no instance.</summary>
    FactoryReturnedNull,
}
