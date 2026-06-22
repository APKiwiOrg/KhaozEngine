# SlathRepro - skinned-mesh windowed-Metal regression repro

Manual regression repro for the skinned-mesh "screen-spanning garbage triangle" bug (SpaceGame's
tentacles). It reproduces SpaceGame's exact render pattern with engine code only: a 3D world rendered to an
offscreen `Render3DPreview` (its own submit), then composited to the swapchain as a 2D quad (a second
submit), then present - rigid cubes plus many `RestPose` skinned tubes.

**Not a CI/unit test** (it opens a GLFW window and the bug only manifests in the live windowed
swapchain-present path; headless `GpuReadback` fences every frame and is structurally always clean). It is
deliberately kept out of `KhaozEngine.slnx` so the solution build/pack ignore it.

## The bug it guards (fixed)

In the windowed Veldrid/Metal swapchain-present context the skinned vertex shader's bone-buffer **array
read corrupts past element 0** - only `bones[0]` survives; a constant `bones[1]` or any data-dependent
index reads garbage - independent of buffer type (uniform/SSBO), binding (range/whole/dynamic), dynamic
offset, and submit structure. A texture read (texelFetch) dodges the corruption but vertex-stage texture
data did not deliver in this stack. Fix: `Scene3D` skins skinned meshes on the **CPU**
(`SkinningMath.SkinVertex`) and draws them through the proven-clean no-bone `ModelRenderer` pipeline. The
GPU path (`SkinnedModelRenderer`) is correct headless and retained dormant.

## Run

```sh
# windowed, look at it live:
dotnet run --project SlathRepro/SlathRepro.csproj -c Debug

# self-capture the OFFSCREEN preview texture to a PNG on a frame, then inspect it:
dotnet run --project SlathRepro/SlathRepro.csproj -c Debug -- --shot      # frame 30 (default)
SLATH_SHOT_FRAME=1 dotnet run --project SlathRepro/SlathRepro.csproj -c Debug -- --shot   # any frame
sips -s format png /tmp/slath_offscreen.ppm --out /tmp/slath_offscreen.png
```

Fixed = green tubes render as clean cylinders. Regressed = green tubes flung into screen-spanning garbage
triangles (rigid blue cubes stay fine either way).
