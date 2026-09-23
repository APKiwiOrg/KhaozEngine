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
guessing from the operating system, so an unpinned fallback is named correctly. This property describes that
cached probe only.

## Golden-image check

`GoldenImage.Check(goldenDirectory, scene, rgba, width, height, tolerance, captureBackend)` downsamples an RGBA8
capture with `KhaozEngine.Imaging.GoldenGrid` and compares it with
`<goldenDirectory>/<scene>.<capture-backend>.txt`. Pass `ctx.GpuDevice.Backend` from the exact device that produced
the RGBA bytes. The helper maps that value through the audited golden-family table, including canonical hyphens
such as `metal-native`. It never substitutes the cached default probe's backend, and an unmapped enum value fails
instead of falling back.

`tolerance` is an integer in 8-bit per-channel units from 0 through 255. The helper divides it by `255f` for
`GoldenGrid`'s normalized comparison. The fresh downsample stays unrounded while the committed grid has four
decimal places. The comparison therefore adds `0.00005f`, half of one stored decimal step, plus a `0.0000001f`
floating-point epsilon to the normalized tolerance. This keeps the integer boundary inclusive and lets a golden
written from a capture pass a later check of the same bytes even at tolerance zero.

```csharp
GoldenResult result = GoldenImage.Check(
    goldenDirectory, "menu", rgba, width, height, tolerance: 15,
    captureBackend: ctx.GpuDevice.Backend);

// Translate a non-null SkipReason through the test framework's skip mechanism.
// Fail the test with Detail when Pass is false and SkipReason is null.
```

`Render3DSnapshot` owns its device internally. Use its metadata capture path so the backend comes from the same
render as the bytes:

```csharp
Render3DCapture capture = Render3DSnapshot.CaptureWithBackend(width, height, setup, drawFrame);
GoldenResult result = GoldenImage.Check(
    goldenDirectory, "world", capture.Rgba, capture.Width, capture.Height, tolerance: 15,
    captureBackend: capture.Backend);
```

`GoldenResult` reports `Pass`, `Rebaked`, an optional `SkipReason`, and `Detail`. A missing backend-specific golden
returns a skip reason that names the expected file. A mismatch returns `Pass = false` and names the worst grid
cell, colour channel, compared values and difference. Scene names must be portable single-file names. A malformed
grid length or non-finite reference cell returns an actionable failure instead of passing or escaping its folder.
Windows device stems such as `CON`, `PRN`, `AUX`, `NUL`, `COM1` and `LPT1` are rejected case-insensitively, even
when followed by an extension. The documented superscript-digit forms `COM¹` through `COM³` and `LPT¹` through
`LPT³` are reserved too.

Set `KE_UPDATE_GOLDENS=1` to create the directory and write the canonical golden instead of comparing. Only the
exact value `1` enables writes. Unset, empty and every other value keep the normal comparison behavior. A read,
parse, validation or write problem returns `Pass = false` with the path and concrete failure in `Detail`.
