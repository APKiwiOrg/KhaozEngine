# Veldrid Immediate-Context Fork

These packages come from `APKiwiOrg/veldrid` commit `20650ed392bcbf6a6c0ef214b9141bc1f6007950`, branch
`release/4.9.102`, tag `v4.9.102`.

They are based on upstream Veldrid `v4.9.0` and retain Vortice `2.3.0`. The fork adds the opt-in
`D3D11DeviceOptions.UseImmediateContext` mode used by `KhaozEngine.Gpu` for Direct3D11 only. The matching
Windows Veldrid suite covers buffer, render, resource-set, and texture paths through this mode.

`release/4.9.102` sits deliberately BELOW the unreleased immediate-mode guardrail commits on
`fix/d3d11-immediate-4.9.0`, and that is the point of it being its own branch. Those commits make a SECOND
command list reaching `Begin` while another holds the immediate context throw instead of silently running
`ClearState` on the live context. They were held back because the windowed `GameApp3D` path forced exactly that
second list open: `AppWindow.Run` opened the frame's command list before calling back into the app, so a guardrail
would have turned a corrupted frame into a hard throw on Windows. KhaozEngine issue #429 removed that by giving
the frame loop a pre-record phase, which is where the ocean prime runs now, so no windowed path opens a nested
list any more. Vendoring the guardrail commits is KhaozEngine issue #428, and it is no longer blocked.

What `4.9.102` adds over `4.9.101`:

- `SmallFixedOrDynamicArray` copies the dynamic offsets into the rented array once the count passes the
  five-value fixed buffer. It rented from `ArrayPool` and never copied, so `Get(i)` returned whatever the
  previous renter had left there: every backend read garbage dynamic offsets for a resource set with more than
  five dynamic bindings, Direct3D11 turned that into a wild `firstConstant`, and both `BoundResourceSetInfo.Equals`
  overloads compared garbage.
- An `InternalsVisibleTo` grant to `Veldrid.Tests`. The two types above are internal with no device-free public
  surface, so the regression test needs it. That test is pure CPU and runs in every configuration.
- Fork README accuracy corrections. The fork paragraph claimed one deliberate departure from upstream deferred
  mode and then described two, so it now says two and numbers them, and the resource-set slot-order note names
  the wider conflict it actually covers rather than only the SRV-against-UAV case.

What `4.9.101` added over `4.9.100`:

- Immediate-context hazard fixes. A `Reset` issued from a foreign thread, which both `Swapchain.Resize` and
  `CommandList.Dispose` do, is now a silent no-op instead of throwing `SynchronizationLockException` and
  clobbering the render thread's in-flight recording. A concurrent resize is still not safe: `Resize` disposes
  the framebuffer the render thread has bound, exactly as it does in deferred mode, so resize between frames
  and never during one. A double `Begin` on the SAME command list throws instead of corrupting state (a
  different list is what the held-back guardrails above cover), and a lock-order fix makes the immediate-context
  lock outermost, which kills a reachable two-thread deadlock. `Map` and `Present` serialization plus
  same-thread `UpdateBuffer` reentrancy are now documented.
- Direct3D11 bind batching. Dirty tracking with a draw-and-dispatch-time flush, an offsets-only rebind
  fast path, bound-record dedup, and a pipeline-switch drain.

The Windows Veldrid suite gained new tests for these paths. They cannot execute on macOS, so the engine's
Windows WARP CI leg is the executing gate for them.

Package version: `4.9.102`

SHA-256:

```text
b70d1dc321f67499f465f8c01251492cdb8487f40853a994e716bbc19f972a50  Veldrid.4.9.102.nupkg
935bbab3dd9e58e6b90ba34970c09b5bdfd4f70aa3d693b03c233e258cc45d63  Veldrid.MetalBindings.4.9.102.nupkg
963da934cf590426d3bf22a2bd339bf76919bb44ece43847cdbc96f0b4848443  Veldrid.OpenGLBindings.4.9.102.nupkg
```
