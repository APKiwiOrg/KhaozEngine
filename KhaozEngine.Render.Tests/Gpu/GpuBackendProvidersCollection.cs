using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Serializes writers to the process-global GpuBackendProviders registry against every other collection.
/// </summary>
[CollectionDefinition("GpuBackendProvidersSerial", DisableParallelization = true)]
public sealed class GpuBackendProvidersCollection { }
