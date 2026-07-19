# KhaozEngine.Determinism

Cross-platform deterministic floating-point environment control for fixed-tick / lockstep sims.
Pins the one piece of per-thread CPU state (the FP control register: ARM64 FPCR, x86 MXCSR) that
otherwise makes the same fixed-seed sim drift across threads, machines, and process runs. Canonical
state is the IEEE default: round-to-nearest-even, FTZ/DAZ off, FP exception traps masked.

- `DeterministicFpScope` - RAII scope. `Enter()` saves the current FP environment and applies the
  canonical one, `Dispose()` restores the save. A `readonly struct`, allocation-free. Wrap a sim
  tick or a whole sim run.
- `DeterministicFp` - the explicit form: `SetCanonical()` returns an `FpEnvToken`, `Restore(token)`
  puts things back (e.g. on sim-thread teardown). `IsSupported` tells you whether register access is
  wired for this platform/architecture.
- `FpEnvToken` - opaque saved-environment token, stored inline (no heap allocation).

```csharp
using (DeterministicFpScope.Enter())
{
    sim.Tick(dt);   // runs under the canonical FP environment
}                   // prior environment restored, even on throw
```

Pure-managed P/Invoke over the platform C library's `<fenv.h>` (libSystem / libm / ucrtbase), so
there is no per-RID native build asset and the package packs like any other managed one. On
platforms or architectures where that is not wired up, `IsSupported` is false and
`SetCanonical`/`Enter` are safe no-ops whose restore is also a no-op. They never corrupt FP state.
The package depends on `KhaozEngine.Diagnostics` for one thing only: a one-time `Warn` logged
through the static `Log` facade when the native probe fails or the architecture is unsupported, so a
determinism guarantee silently dropping on an odd platform leaves a breadcrumb instead of surfacing
only later as an unexplained sim/replication drift.

Scope caveat: this controls the FP control register only. It does not remove non-determinism from
JIT codegen variance (FMA contraction, auto-vectorization). See "Deterministic floating point" in
the engine's `docs/USING-KHAOZENGINE.md` for the full guidance.
