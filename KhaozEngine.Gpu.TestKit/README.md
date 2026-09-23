# KhaozEngine.Gpu.TestKit

Framework-agnostic helpers for GPU tests. Reference this package explicitly from a test project. It belongs to no
umbrella metapackage and carries no xUnit, NUnit or MSTest dependency.

## GPU run gate

`GpuTestGate.SkipReason()` reads `KE_GPU_TESTS` and returns null to run or a reason to skip.

- `1` is strict mode. It returns null without probing, so a missing device remains a test failure.
- `probe` attempts one headless device creation for the process. Success returns null. Failure returns the
  exception type and message as a concrete skip reason.
- Unset, empty and other values return the setup reason without probing.

The gate registers the engine's Direct3D 11, Vulkan and Metal native backends before a probe. Consumers can call
it directly without first touching `GpuFactAttribute` or another xUnit type.

```csharp
using KhaozEngine.Gpu.TestKit;
using Xunit;

public sealed class GameGpuFactAttribute : FactAttribute
{
    public GameGpuFactAttribute() => Skip = GpuTestGate.SkipReason();
}
```

`GpuTestGate.BackendName` returns the established golden-file family of the backend the cached headless probe
actually created: `direct3d11-native`, `vulkan-native` or `metal-native`. It reads the created device rather than
guessing from the operating system, so an unpinned fallback is named correctly.
