using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Groups the GPU tests that time frames, so a timed frame never shares the device with another test class.
/// <c>DisableParallelization</c> keeps the whole group off the parallel pool, so it runs after the parallel collections
/// with the GPU to itself. Reference it by name with <c>[Collection("GpuTimingSensitive")]</c>.
/// </summary>
[CollectionDefinition("GpuTimingSensitive", DisableParallelization = true)]
public sealed class GpuTimingSensitiveCollection { }
