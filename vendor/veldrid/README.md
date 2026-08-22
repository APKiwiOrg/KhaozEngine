# Veldrid Immediate-Context Fork

These packages come from `APKiwiOrg/veldrid` commit `d60cdd2392ba1c5ace12697bc1acf45f1879db14`, branch
`fix/d3d11-immediate-4.9.0`, tag `v4.9.104`. Commit, branch and annotated tag are all on that remote, so the
provenance above is fetchable rather than a claim about one machine.

Pushing them is part of the vendoring ritual, and it happens BEFORE the engine commit that vendors the bytes
lands on `main`. Nothing mechanical enforces it: a restore reads the committed nupkg out of this directory and
never builds the fork, so CI stays green over shipped binaries whose stated source cannot be fetched, with this
paragraph as the only witness. That gap is what
[KhaozEngine issue 672](https://github.com/APKiwiOrg/KhaozEngine/issues/672) recorded for `4.9.104`.

They are based on upstream Veldrid `v4.9.0` and retain Vortice `2.3.0`. The fork adds the opt-in
`D3D11DeviceOptions.UseImmediateContext` mode used by `KhaozEngine.Gpu` for Direct3D11 only, and, as of
`4.9.104`, one Metal repair that is not opt-in and applies to every Metal pipeline. The matching
Windows Veldrid suite covers buffer, render, resource-set, and texture paths through the immediate mode.

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

What `4.9.104` adds over `4.9.103`:

- The Metal backend reads `RasterizerStateDescription.DepthClipEnabled`. `MTLPipeline` derived its
  `MTLDepthClipMode` from `DepthStencilState.DepthTestEnabled` and read the rasterizer flag nowhere at all, so a
  pipeline running the depth test with clipping disabled clamped on Direct3D 11 and Vulkan and clipped on Metal.
  Metal has no rasterizer depth-clip enable of its own, and `MTLDepthClipModeClamp` is its equivalent of
  `DepthClipEnable = FALSE`, so the flag maps onto it directly. Four shipped KhaozEngine pipelines asked for
  clamping with the depth test on (sky, starfield, ground decal, particles) and so rasterised differently on
  macOS than everywhere else. KhaozEngine issue 598.

  The engine's own `KhaozEngine.Gpu.Metal` backend carries the identical change in the same release, which is
  what keeps the two Metal paths agreeing: they share one `metal` golden family, so a repair to only one of them
  would leave the guest leg disagreeing with grids the incumbent baked. Neither moved a committed golden, because
  the three background pipelines among the four emit `z == w` exactly, where clipping and clamping agree.

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

Package version: `4.9.104`

SHA-256:

```text
1105eb60c1e83e3ace9b16f0ac63c1d9c3bd3bca3ac8a21caf774c016ba475c8  Veldrid.4.9.104.nupkg
2c89dd71b96ea539575ba1031d54a9446b4dd8c4c6bf3cf6286bad6e27e6abf1  Veldrid.MetalBindings.4.9.104.nupkg
83b59319f641c8cd604192c72a9652d61bec042ea1298b7afed03f4e762cb11f  Veldrid.OpenGLBindings.4.9.104.nupkg
```
