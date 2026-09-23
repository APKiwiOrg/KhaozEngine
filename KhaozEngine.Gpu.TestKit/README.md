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

## Golden-image check

`GoldenImage.Check(goldenDirectory, scene, rgba, width, height, tolerance)` downsamples an RGBA8 capture with
`KhaozEngine.Imaging.GoldenGrid` and compares it with
`<goldenDirectory>/<scene>.<actual-backend>.txt`. The actual backend is the cached device backend reported by
`GpuTestGate.BackendName`, including canonical hyphens such as `metal-native`.

`tolerance` is an integer in 8-bit per-channel units from 0 through 255. The helper divides it by `255f` for
`GoldenGrid`'s normalized comparison. It compares in the canonical serialized grid precision, so a golden written
from a capture passes a later check of the same bytes even at tolerance zero.

```csharp
GoldenResult result = GoldenImage.Check(goldenDirectory, "menu", rgba, width, height, tolerance: 15);

// Translate a non-null SkipReason through the test framework's skip mechanism.
// Fail the test with Detail when Pass is false and SkipReason is null.
```

`GoldenResult` reports `Pass`, `Rebaked`, an optional `SkipReason`, and `Detail`. A missing backend-specific golden
returns a skip reason that names the expected file. A mismatch returns `Pass = false` and names the worst grid
cell, colour channel, compared values and difference. Scene names must be portable single-file names. A malformed
grid length or non-finite reference cell returns an actionable failure instead of passing or escaping its folder.

Set `KE_UPDATE_GOLDENS=1` to create the directory and write the canonical golden instead of comparing. Only the
exact value `1` enables writes. Unset, empty and every other value keep the normal comparison behavior. A read,
parse, validation or write problem returns `Pass = false` with the path and concrete failure in `Detail`.
