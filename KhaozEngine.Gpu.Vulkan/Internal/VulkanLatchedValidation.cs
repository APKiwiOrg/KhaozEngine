namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>The first strict validation error, published atomically with its diagnostic identifiers.</summary>
internal sealed record VulkanLatchedValidation(string Diagnostic, int MessageId, string MessageIdName);
