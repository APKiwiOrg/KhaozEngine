# Veldrid Immediate-Context Fork

These packages come from `APKiwiOrg/veldrid` commit `2dce75411fc869c4bdc36c4c313ca03fbda7a1cd`, branch
`fix/d3d11-immediate-4.9.0`, tag `v4.9.101`.

They are based on upstream Veldrid `v4.9.0` and retain Vortice `2.3.0`. The fork adds the opt-in
`D3D11DeviceOptions.UseImmediateContext` mode used by `KhaozEngine.Gpu` for Direct3D11 only. The matching
Windows Veldrid suite covers buffer, render, resource-set, and texture paths through this mode.

What `4.9.101` adds over `4.9.100`:

- Immediate-context hazard fixes. Cross-thread `Resize` and `Dispose` are now safe against a concurrent
  `Reset`, a double `Begin` throws instead of corrupting state, and a lock-order fix makes the
  immediate-context lock outermost, which kills a reachable two-thread deadlock. `Map` and `Present`
  serialization plus same-thread `UpdateBuffer` reentrancy are now documented.
- Direct3D11 bind batching. Dirty tracking with a draw-and-dispatch-time flush, an offsets-only rebind
  fast path, bound-record dedup, and a pipeline-switch drain.

The Windows Veldrid suite gained new tests for these paths. They cannot execute on macOS, so the engine's
Windows WARP CI leg is the executing gate for them.

Package version: `4.9.101`

SHA-256:

```text
8d579aa09561e8e7aeb315fcb8a920cd4d7b48b259cc822d5f0e07bd0227582e  Veldrid.4.9.101.nupkg
c1dbd50a50097925cd023a740142ce84cf4c3d78647968349523b7a152980ea4  Veldrid.MetalBindings.4.9.101.nupkg
83664960e06d39134244dbdf2f86e5d466ebd9a74eefafc106328d4b16ca5c37  Veldrid.OpenGLBindings.4.9.101.nupkg
```
