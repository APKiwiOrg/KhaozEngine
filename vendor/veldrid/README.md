# Veldrid Immediate-Context Fork

These packages come from `APKiwiOrg/veldrid` commit `74e523607843f49cd8f6969815c94b37cd047bb7`, branch
`fix/d3d11-immediate-4.9.0`, tag `v4.9.103`.

They are based on upstream Veldrid `v4.9.0` and retain Vortice `2.3.0`. The fork adds the opt-in
`D3D11DeviceOptions.UseImmediateContext` mode used by `KhaozEngine.Gpu` for Direct3D11 only. The matching
Windows Veldrid suite covers buffer, render, resource-set, and texture paths through this mode.

The immediate-mode guardrail is IN as of `4.9.103`, which is why the vendored line moves off the separate
`release/*` branch and back onto `fix/d3d11-immediate-4.9.0`. Both throws now exist and they are different
cases. A second `Begin` on the SAME command list has thrown since `4.9.101`. A second `Begin` on a DIFFERENT
one, while the first still holds the immediate context, throws as of `4.9.103`, where before it ran `ClearState`
on the live context and silently wiped the open recording's bindings.

That guardrail was held back for four releases because the windowed `GameApp3D` path forced exactly that second
list open: `AppWindow.Run` opened the frame's command list before calling back into the app, so the throw would
have converted a corrupted frame into a hard crash on Windows. KhaozEngine issue #429 removed the cause by giving
the frame loop a pre-record phase, which is where the ocean prime runs now, so no engine-shipped windowed host
opens a nested list any more. That is why the guardrail and the pre-record phase ship together in KhaozEngine
`17.27.0`, and the ordering is the whole point: the cause went first.

One residual remains by design. A host driving a `Render3DSurface` off a raw `AppWindow.Run(onFrame)` without
passing `onPrepare` still nests, because the surface's safety-net `Scene3D.PrepareFrame` then runs inside the
frame's recording. Under this version that residual is a loud `VeldridException` naming the fix rather than
silent corruption, which is the intended trade.

What `4.9.103` adds over `4.9.102`:

- The second-recorder guardrail. In immediate mode every `D3D11CommandList`'s context IS the device's immediate
  context, so the per-instance `_recordingThreadId` guard cannot see the hazard at all: a fresh instance has
  never recorded, so its field reads as "nobody" no matter what another instance is doing. The owner moves onto
  `D3D11GraphicsDevice`, beside the recording lock it belongs to, and a different command list reaching `Begin`
  gets a `VeldridException` naming the situation and pointing at `UseImmediateContext`. The refusal happens
  before anything is touched, ahead of the deferred list disposal, the `ClearState`, and the lock, so the open
  recorder carries on unharmed and the refused instance is left pristine and begins normally once the context is
  free. Deferred mode never reaches any of this.
- Interrupted-lock-acquisition hardening on that guard. `Monitor.Enter` takes the ref-bool overload, because a
  `ThreadInterruptedException` delivered AFTER acquisition is indistinguishable from a failed acquisition through
  the bare overload. The catch was therefore dropping the claim on a lock the thread still held, and the next
  `Begin` passed the claim and then blocked on that lock forever, which is the silent wedge the guard exists to
  remove. The claim is released only when the lock demonstrably was not taken. The throw message also stops
  saying the context is captured by an open `Begin`, since the common holder has already reached `End` and is
  waiting on `SubmitCommands`, so it names the whole `Begin` to `SubmitCommands` span the way the XML doc does.

What `4.9.102` added over `4.9.101`:

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
  different list is the separate case `4.9.103` above closes), and a lock-order fix makes the immediate-context
  lock outermost, which kills a reachable two-thread deadlock. `Map` and `Present` serialization plus
  same-thread `UpdateBuffer` reentrancy are now documented.
- Direct3D11 bind batching. Dirty tracking with a draw-and-dispatch-time flush, an offsets-only rebind
  fast path, bound-record dedup, and a pipeline-switch drain.

The Windows Veldrid suite gained new tests for these paths. They cannot execute on macOS, so the engine's
Windows WARP CI leg is the executing gate for them.

Package version: `4.9.103`

SHA-256:

```text
8089e48f02d01ba90c25e7a9f18cc64b5b457606b7cf62316d53526a9fac010a  Veldrid.4.9.103.nupkg
8c938d04da9f3a91d90d5f6097a5f956b417da259e38bf705e40da85cdc72ebd  Veldrid.MetalBindings.4.9.103.nupkg
00bb3486e28e1370114f8de3d6c5b8a03e4ddbc137dc51126a4515b4138a260d  Veldrid.OpenGLBindings.4.9.103.nupkg
```
