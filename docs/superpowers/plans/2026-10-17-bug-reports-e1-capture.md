# In-game bug reports E1 (capture) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture what the player just saw, without stalling a frame: a rolling 5 second JPEG clip of the final composite and a full-resolution screenshot taken in the trigger frame, exposed through `FrameHistoryRecorder`.

**Architecture:** `AppWindow` gains a frame tap registry that runs after the game's record callback and before `Submit`. A tap asks the window for this frame's copy of the swapchain image, which the native backends produce through a new optional `IGpuSwapchainCapture` device capability. The clip tap downscales that copy on the GPU with `GpuScaledCopy` into an RGBA8 target, copies it into a `GpuReadbackRing` slot fenced on the frame's own submit, and later frames map completed slots and hand them to a background JPEG worker that fills an in-memory ring. The screenshot tap downscales to at most 2560 px and reads back once, blocking, after submit.

**Tech Stack:** C# on .NET 10, the KhaozEngine GPU seam (native Metal, Vulkan and Direct3D 11 backends), GLSL 450 through the engine's SPIR-V toolchain, xUnit, StbImageSharp as the test-side reference decoder.

**Spec:** `docs/superpowers/specs/2026-10-17-bug-reports-design.md` (Capture pipeline, Packages, Testing, Phases E1). **Binding contract:** `docs/superpowers/plans/2026-10-17-bug-reports-contract.md` ("E1 produces"). Executors read both. `RecentLogSink` is listed under E1 in the spec but moved to E2 by contract decision D5, so it is not in this plan.

## Global Constraints

- Public names and signatures in the contract's "E1 produces" block are binding: `JpegWriter.Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality)`, `GpuReadbackRing(IGpuDevice device, int width, int height, int slotCount = 3)`, `IFrameCaptureTap`, `AppWindow.AddCaptureTap`, `AppWindow.RemoveCaptureTap`, `ReportImage`, `ReportClipFrame`, `ReportClip`, `FrameHistoryOptions`, `FrameHistoryRecorder` and every member listed for it.
- `KhaozEngine.BugReports` holds only `ReportImage`, `ReportClipFrame` and `ReportClip` in E1 (D4). It stays headless: no `Gpu`, `Windowing`, `Render2D`, `Gui` or `Game` anywhere in its closure.
- Clip defaults: duration 5 s, 12 fps, long edge 960 px, JPEG quality 70, hard byte cap 8 MB. Frames live in memory only.
- Screenshot: long edge at most 2560 px, PNG when it encodes under 4 MB, JPEG quality 92 otherwise (BR13).
- The tap never stalls a frame: the clip path never calls `WaitForIdle`, and a capture tick with no free slot is dropped. The screenshot accepts one blocking readback after submit.
- A device whose `GpuCapabilities.SupportsCompletionFences` is false gets no rolling clip. The screenshot still works on it.
- Swapchain copy-source usage is requested only while at least one tap is registered.
- With no tap registered, a frame is exactly today's frame: one `Submit(commands)` and one `Present()`, nothing else.
- Encoding never runs on the frame thread. The encode queue holds two mapped frames and drops the oldest.
- New behaviour gets a headless test in the matching area test project: Imaging, Gpu and Windowing tests live in `KhaozEngine.Render.Tests` (`Imaging/`, `Gpu/`, `Windowing/`, where `PngWriterTests` and `FramePhasesTests` already are), client tests in the new `KhaozEngine.BugReports.Client.Tests`. Test namespaces stay under `KhaozEngine.Tests.*` and new test projects set `<IsPackable>false</IsPackable>`.
- KESIZE: new behaviour goes in new types or new partial files. `KhaozEngine.Tests/ArchitectureTests.cs` is at the 800 line cap and must not grow. `AppWindow.cs` (756) and `VulkanGpuDevice.cs` (789) take at most the one-line edits named below.
- Warnings are errors. No suppressions. `AppWindow` stays the only raw input reader (nothing here reads input).
- Every local build or test runs through `build-slot --label <what> -- <command>`. Full-suite and GPU runs add `--heavy`. GPU facts run only with `KE_GPU_TESTS=1`.
- Commit subjects use `area(scope): summary`. Commit explicit paths only. No version bump and no `CHANGELOG.md` entry (E3's last task owns release staging).
- No em dashes, en dashes or prose semicolons in Markdown, comments or shipped text.

## Decisions

1. **Downscale route: copy, then a sampled downscale pass, on all three backends.** The frame tap first copies the swapchain image whole into a window-owned texture of the swapchain's own format with `Sampled` usage, then `GpuScaledCopy` draws a fullscreen triangle that samples it into an `R8G8B8A8UNorm` render target at capture size. Reasons: Direct3D 11 has no scaled blit and Metal's blit encoder does not scale, so a scaled blit would be Vulkan only. Sampling the swapchain image directly would need `SAMPLED` on Vulkan swapchain images, `DXGI_USAGE_SHADER_INPUT` on the DXGI swap chain and layout round trips on the presentable image, all riskier than a copy. The copy-first route needs only copy-source usage: Metal `framebufferOnly = false`, Vulkan `VK_IMAGE_USAGE_TRANSFER_SRC_BIT` added to today's `COLOR_ATTACHMENT | TRANSFER_DST`, Direct3D 11 nothing (a `RenderTargetOutput` back buffer is already a `CopyResource` source). One full-resolution copy also serves the screenshot, so a frame that captures both copies once.
2. **Usage flags only while a tap is registered.** The window sets `IGpuSwapchainCapture.SwapchainCopySourceRequested` true on the first `AddCaptureTap` and false after the last `RemoveCaptureTap`. Metal applies the change at the next present boundary before it takes the next drawable. Vulkan applies it by queueing a swapchain recreation (the existing `QueueRecreate` path), and only when the surface's `supportedUsageFlags` include `TRANSFER_SRC`. Direct3D 11 treats the request as a no-op. `SwapchainCopyable` reports whether the image the current frame renders into can be copied, so the first frame after a request may be uncopyable on Metal and Vulkan.
3. **Fenced submit only when a tap asks for it.** `FramePhases.Run` gains an overload that takes the window's `FrameCaptureTaps` and the frame's unclamped milliseconds. When the registry is null or empty, it runs the existing body unchanged. Otherwise, after `onFrame` and still inside the recording, it runs every tap's `Record`. A tap that recorded a ring copy calls `FrameCaptureContext.SignalOnSubmit(fence)`, at most once per frame. After the recording closes, `FramePhases` calls `Submit(commands, fence)` when a fence was handed in, else `Submit(commands)`, then runs every tap's `AfterSubmit` (nothing recording, so maps and the screenshot's blocking readback are legal), then `Present()`.
4. **Screenshot in the trigger frame, including while the clip is stopped.** The recorder registers its screenshot tap at construction and keeps it until `Dispose`. That tap does nothing per frame unless armed, but its registration keeps copy-source usage on, so the trigger frame's image is copyable on every backend whether or not the clip is recording. `CaptureScreenshotAsync()` arms it, the tap copies and downscales in that frame's `Record`, and `AfterSubmit` reads back once with the existing blocking `GpuReadback.ToRgba`. When the image is not copyable in the arming frame (the first frame after construction, or a minimised window), the arm stays pending and lands on the next copyable frame. The arm is a one-shot. This keeps the contract signatures and refines the contract's "one-shot tap" wording, see the report.
5. **Reference decoder: StbImageSharp, test project only.** `KhaozEngine.Render.Tests/Imaging/PngWriterTests.cs` already decodes with `StbImageSharp.ImageResult`, which reaches the test project through `Render2D`. `ArchitectureTests.ThirdPartyPackages_StayInTheirSeamOrBackendHome` scans packable libraries only, and `docs/DEPENDENCY-SEAMS.md` records test-only edges as outside the shipped graph, so no new edge and no new package pin is introduced. `KhaozEngine.Imaging` stays BCL only.
6. **Channel order: normalized on the GPU.** Every shipped swapchain is `B8G8R8A8UNorm`. Sampling returns logical RGBA regardless of storage order, so the `GpuScaledCopy` output is `R8G8B8A8UNorm` in RGBA order and alpha is forced to 1. Neither the clip nor the screenshot swizzles on the CPU. The `GpuScaledCopyGpuTests` quadrant test pins this on every backend.
7. **Resize: per-frame size.** Capture size is recomputed every captured frame with `CaptureSize.Fit(swapchainWidth, swapchainHeight, longEdge)` (never upscales). When it changes, the clip tap retires its target through the window's `GpuRetireQueue`, keeps the old ring until its in-flight slots are read, then disposes it, and starts a new ring at the new size. Every `ReportClipFrame` carries its own width and height.
8. **Render time per frame: the loop's unclamped delta.** `FrameStats` is a windowed average and `Frame.Dt` is clamped at 100 ms, which would hide exactly the hitches the viewer marks. `FrameCaptureContext.FrameMilliseconds` carries the raw `Render` delta in milliseconds, and the recorder stores it as `ReportClipFrame.RenderMilliseconds`.
9. **Encode cost is measured, not assumed.** Task 1 adds a `--jpeg` mode to `KhaozEngine.Benchmarks` and records the macOS figure for a 960x540 frame in the Imaging README. Windows and Linux figures need a run on those platforms, listed as open in the final task.

## File map

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.Imaging/JpegWriter.cs`, `JpegTables.cs`, `JpegForwardDct.cs`, `JpegBitWriter.cs` | Baseline JPEG encoder, tables, DCT, entropy bit writer |
| `KhaozEngine.Benchmarks/Imaging/JpegEncodeBenchmark.cs` | `--jpeg` encode timing |
| `KhaozEngine.Gpu/GpuScaledCopy.cs` | Fullscreen linear downscale of any sampled texture into an RGBA8 target |
| `KhaozEngine.Gpu/GpuReadbackRing.cs` | Fenced staging slots, polled, never waits |
| `KhaozEngine.Gpu/IGpuSwapchainCapture.cs`, `Internal/SwapchainCopyCheck.cs` | Optional device capability and its shared argument check |
| `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.SwapchainCapture.cs` | D3D11 implementation |
| `KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.SwapchainCapture.cs` plus edits to the present boundary, pending queue and swapchain API | Metal implementation |
| `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.SwapchainCapture.cs` plus edits to surface facts, policy, spec and present boundary | Vulkan implementation |
| `KhaozEngine.Windowing/IFrameCaptureTap.cs`, `FrameCaptureContext.cs`, `FrameCaptureTaps.cs`, `AppWindow.CaptureTaps.cs` | Tap contract, per-frame context, registry, public entry points |
| `KhaozEngine.BugReports/` | `ReportImage`, `ReportClipFrame`, `ReportClip` |
| `KhaozEngine.BugReports.Client/` | `FrameHistoryOptions`, `FrameHistoryRecorder` and its internal pieces |
| `KhaozEngine.BugReports.Client.Tests/` | Headless client tests |
| `KhaozEngine.Tests/ArchitectureTests.BugReports.cs` | Headless and opt-in guards for the two new packages |

## Review Focus

1. **A window resize while recording.** The player resizes or toggles fullscreen mid-clip. Expected: recording continues, frames before and after keep their own sizes, no slot of the old ring leaks or is read at the wrong size. Pinned by `FrameHistoryTests.Frames_of_different_sizes_keep_their_own_dimensions` (Task 8) and `GpuReadbackRingTests.Dispose_with_slots_in_flight_frees_every_slot` (Task 3).
2. **A minimised or render-suppressed window.** The player alt-tabs or minimises, then presses the trigger. Expected: no tap runs on a suppressed frame, the clip shows a gap, and a screenshot armed while minimised lands on the next rendered frame. Pinned by `FrameCaptureTapsTests.A_render_suppressed_frame_runs_no_tap` (Task 7) and `FrameHistoryRecorderTests.A_second_screenshot_request_while_one_is_pending_returns_the_same_task` (Task 10).
3. **Options the bundle cannot carry.** A game sets 30 fps for 10 s, or a 4K long edge. E2's bundle names at most 120 clip frames and screenshots stop at 2560 px. Expected: the recorder refuses such options at construction with `ArgumentOutOfRangeException` naming the option. Pinned by `FrameHistoryRecorderTests.Options_the_bundle_cannot_carry_are_refused` (Task 10).
4. **A slow machine.** The encoder falls behind on a low-end CPU. Expected: the frame thread never blocks, the oldest waiting frame is dropped, and `Freeze()` still includes the frames already read back, waiting at most 250 ms. Pinned by `ClipEncodeQueueTests.A_full_queue_drops_the_oldest_waiting_frame_and_never_blocks` and `ClipEncodeQueueTests.WaitIdle_returns_false_when_the_encoder_outlives_the_timeout` (Task 9), and `FrameHistoryRecorderTests.Freeze_keeps_frames_that_were_still_encoding` (Task 10).
5. **A capture fault, such as a lost device.** A GPU call inside a tap throws. Expected: the game's frame continues, recording turns itself off with one warning, and the screenshot task faults instead of hanging. Pinned by `TapGuardTests.A_fault_is_reported_once_and_the_guard_stays_tripped` (Task 9) and `FrameHistoryRecorderTests.A_tripped_clip_guard_stops_recording` (Task 10).

---

### Task 1: JpegWriter

**Files:**
- Create: `KhaozEngine.Imaging/JpegWriter.cs`, `KhaozEngine.Imaging/JpegTables.cs`, `KhaozEngine.Imaging/JpegForwardDct.cs`, `KhaozEngine.Imaging/JpegBitWriter.cs`
- Create: `KhaozEngine.Benchmarks/Imaging/JpegEncodeBenchmark.cs`
- Modify: `KhaozEngine.Benchmarks/Program.cs` (one `--jpeg` branch beside `--items`), `KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj` (add the Imaging `ProjectReference`), `KhaozEngine.Benchmarks/README.md` (one usage line)
- Modify: `KhaozEngine.Imaging/KhaozEngine.Imaging.csproj` (`<Description>` names the JPEG encoder), `KhaozEngine.Imaging/README.md` (new `## JpegWriter` section after `## PngWriter`), root `README.md` (`**KhaozEngine.Imaging**` row names `JpegWriter`)
- Test: `KhaozEngine.Render.Tests/Imaging/JpegWriterTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class JpegWriter { public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality); }` in `KhaozEngine.Imaging`. Baseline sequential, 4:2:0, alpha ignored, deterministic.

- [ ] **Step 1: Write the failing tests** in `KhaozEngine.Render.Tests/Imaging/JpegWriterTests.cs` (namespace `KhaozEngine.Tests.Imaging`, decode with `ImageResult.FromMemory(jpeg, ColorComponents.RedGreenBlue)`). Helpers in the test: `Gradient(w, h)` (R = x ramp, G = y ramp, B = 128, A = 255), `Textured(w, h)` (gradient plus a deterministic `x * 31 + y * 17` ripple), `Segment(jpeg, marker)` returning the payload after the two length bytes, `Psnr(rgbaSource, rgbDecoded)`.

```csharp
[Fact] public void Encode_brackets_the_stream_with_SOI_and_EOI()
{   byte[] jpg = JpegWriter.Encode(Gradient(16, 16), 16, 16, 90);
    Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpg[..2]);
    Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpg[^2..]); }

[Fact] public void Encode_writes_a_baseline_SOF0_with_4_2_0_sampling()
{   byte[] jpg = JpegWriter.Encode(Gradient(40, 24), 40, 24, 90);
    byte[] sof = Segment(jpg, 0xC0);
    Assert.Equal(8, sof[0]);                         // precision
    Assert.Equal(24, (sof[1] << 8) | sof[2]);        // height
    Assert.Equal(40, (sof[3] << 8) | sof[4]);        // width
    Assert.Equal(3, sof[5]);                         // components
    Assert.Equal(0x22, sof[7]); Assert.Equal(0x11, sof[10]); Assert.Equal(0x11, sof[13]);
    Assert.Null(SegmentOrNull(jpg, 0xC2)); }         // never progressive

[Theory, InlineData(90, 35.0), InlineData(50, 30.0)]
public void A_smooth_gradient_round_trips_through_the_reference_decoder(int quality, double minPsnr)
{   byte[] src = Gradient(97, 61);
    ImageResult d = ImageResult.FromMemory(JpegWriter.Encode(src, 97, 61, quality), ColorComponents.RedGreenBlue);
    Assert.Equal((97, 61), (d.Width, d.Height));
    Assert.True(Psnr(src, d.Data) >= minPsnr); }

[Theory, InlineData(1, 1), InlineData(7, 9), InlineData(17, 15), InlineData(33, 1)]
public void Sizes_that_are_not_multiples_of_the_MCU_decode_at_their_own_size(int w, int h)
    // solid (40, 120, 200) at quality 90: decoded size equal, every channel of every pixel within 4

[Theory, InlineData(255, 0, 0), InlineData(0, 255, 0), InlineData(0, 0, 255)]
public void Primary_colours_keep_their_channel_order(byte r, byte g, byte b)
    // 32x32 solid at quality 90: the centre pixel is within 8 of (r, g, b) on each channel

[Fact] public void Alpha_is_ignored()
    // the same Gradient(24, 24) with alpha 255 and with alpha 0 encodes to identical bytes

[Fact] public void The_output_is_deterministic()
    // two encodes of Textured(64, 48) at 70 are byte-equal

[Fact] public void Higher_quality_costs_bytes_and_buys_fidelity()
    // Textured(128, 96): length(q30) < length(q90) and Psnr(q30) < Psnr(q90)

[Theory, InlineData(0), InlineData(101)]
public void A_quality_outside_1_to_100_is_refused(int q)
    => Assert.Throws<ArgumentOutOfRangeException>(() => JpegWriter.Encode(Gradient(8, 8), 8, 8, q));

[Theory, InlineData(0, 8), InlineData(8, -1), InlineData(65536, 1)]
public void A_size_outside_1_to_65535_is_refused(int w, int h)
    => Assert.Throws<ArgumentOutOfRangeException>(() => JpegWriter.Encode(new byte[4], w, h, 70));

[Fact] public void A_buffer_that_is_not_width_times_height_times_four_is_refused()
    => Assert.Throws<ArgumentException>(() => JpegWriter.Encode(new byte[8 * 8 * 4 - 1], 8, 8, 70));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-jpeg-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~JpegWriterTests"`
Expected: build fails with CS0103 naming `JpegWriter`.

- [ ] **Step 3: Implement `JpegWriter.Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality) -> byte[]`**

Validation mirrors `PngWriter`: sizes `1..65535` and quality `1..100` throw `ArgumentOutOfRangeException`, a length other than `width * height * 4` throws `ArgumentException`. Stream layout: SOI, APP0 `JFIF` 1.01 (density 1:1, no thumbnail), DQT table 0 (luma) and table 1 (chroma), SOF0 (8 bit, three components, Y `0x22` table 0, Cb and Cr `0x11` table 1), DHT for DC0, AC0, DC1, AC1, SOS, entropy data, EOI. Pinned choices the tests do not determine:

- Quantization: ITU T.81 Annex K.1 luminance and chrominance tables, scaled by the IJG rule `scale = q < 50 ? 5000 / q : 200 - 2 * q`, entry `clamp((base * scale + 50) / 100, 1, 255)`, written in zigzag order.
- Huffman: the Annex K.3 standard tables (`JpegTables`).
- Colour: JFIF full-range BT.601, `Y = 0.299R + 0.587G + 0.114B`, `Cb = 128 - 0.168736R - 0.331264G + 0.5B`, `Cr = 128 + 0.5R - 0.418688G - 0.081312B`, level shift by 128 before the DCT.
- Subsampling: chroma is the mean of each 2x2 luma-aligned block. Edges replicate the last row and column into the 16x16 MCU.
- DCT: separable float DCT over a precomputed 8x8 cosine table (`JpegForwardDct`), quantize with round to nearest.
- Entropy: DC prediction per component, ZRL for 16 zero runs, EOB, `0xFF` stuffed as `0xFF 0x00`, final byte padded with 1 bits (`JpegBitWriter`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e1-jpeg-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~JpegWriterTests"`
Expected: all `JpegWriterTests` pass.

- [ ] **Step 5: Add the `--jpeg` benchmark mode and measure**

`internal static class JpegEncodeBenchmark { internal static int Run(string[] args); }` encodes a deterministic 960x540 `Textured`-style frame at quality 70, 10 warm-up then 200 timed iterations, and prints `jpeg 960x540 q70: mean <ms> ms, p95 <ms> ms, bytes <n>`. `Program.cs` dispatches `--jpeg` the way it dispatches `--items`.

Run: `build-slot --heavy --label e1-jpeg-bench -- dotnet run --project KhaozEngine.Benchmarks -c Release -- --jpeg`
Expected: one `jpeg 960x540 q70:` line. Record the macOS mean and p95 with the machine model in the Imaging README `## JpegWriter` section. If the mean exceeds 15 ms, replace the float DCT with the AAN factorization and measure again before committing.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Imaging/JpegWriter.cs KhaozEngine.Imaging/JpegTables.cs KhaozEngine.Imaging/JpegForwardDct.cs KhaozEngine.Imaging/JpegBitWriter.cs KhaozEngine.Imaging/KhaozEngine.Imaging.csproj KhaozEngine.Imaging/README.md README.md KhaozEngine.Render.Tests/Imaging/JpegWriterTests.cs KhaozEngine.Benchmarks/Imaging/JpegEncodeBenchmark.cs KhaozEngine.Benchmarks/Program.cs KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj KhaozEngine.Benchmarks/README.md
git commit -m "feat(imaging): add a baseline JPEG writer"
```

### Task 2: GpuScaledCopy

**Files:**
- Create: `KhaozEngine.Gpu/GpuScaledCopy.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs` (one `yield return new("GpuScaledCopy", GpuScaledCopy.VertexSource, GpuScaledCopy.FragmentSource);` row, and every "85" in its comments becomes "86"), `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs` (`Assert.Equal(85, graphics.Length)` becomes 86)
- Modify (regenerated): `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`
- Modify: `KhaozEngine.Gpu/README.md` (new `## GpuScaledCopy` section)
- Test: `KhaozEngine.Render.Tests/Gpu/GpuScaledCopyGpuTests.cs`, `KhaozEngine.Render.Tests/Gpu/GpuScaledCopyTests.cs`

**Interfaces:**
- Consumes: the GPU seam only.
- Produces:

```csharp
namespace KhaozEngine.Gpu;
public sealed class GpuScaledCopy : IDisposable
{
    public GpuScaledCopy(IGpuDevice device);
    // Records, into an OPEN recording, a linear-filtered draw of source (any size, Sampled usage, any
    // colour format) over the whole of destination, whose single colour attachment is R8G8B8A8UNorm.
    // Output is in RGBA order whatever the source's storage order, alpha forced to 1.
    public void Record(IGpuCommandList commands, IGpuTexture source, IGpuFramebuffer destination);
    internal const string VertexSource;
    internal const string FragmentSource;
}
```

- [ ] **Step 1: Write the failing tests**

`GpuScaledCopyTests` (headless):

```csharp
[Fact] public void The_sources_validate_through_the_shipped_toolchain()
    => ShaderValidation.ValidatePair(GpuScaledCopy.VertexSource, GpuScaledCopy.FragmentSource, "GpuScaledCopy");

[Fact] public void A_destination_that_is_not_one_RGBA8_attachment_is_refused()
    // FakeGpuDevice: a framebuffer over one B8G8R8A8UNorm texture -> ArgumentException naming "destination"
```

`GpuScaledCopyGpuTests` (`[GpuFact]`, `GpuDeviceContext.CreateHeadless()`), source is 64x32 split into four solid quadrants, top-left (200, 30, 30), top-right (30, 200, 30), bottom-left (30, 30, 200), bottom-right (220, 220, 40), read back with `GpuReadback.ToRgba` after submit:

```csharp
[GpuFact] public void A_BGRA_source_downscales_four_to_one_into_RGBA_with_every_quadrant_in_place()
    // source B8G8R8A8UNorm (bytes uploaded in BGRA order), destination 16x8 RGBA8.
    // Pixels (3,1), (12,1), (3,6), (12,6) equal their quadrant colour within 2 per channel, alpha 255.

[GpuFact] public void An_RGBA_source_at_the_same_size_keeps_every_quadrant_interior()
    // source R8G8B8A8UNorm 64x32, destination 64x32: pixels at least 2 texels from a quadrant edge within 2

[GpuFact] public void Two_records_into_the_same_destination_in_one_recording_leave_the_second_source()
    // record source A then source B into one destination in one list: the result is B's quadrants
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-scaled-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuScaledCopy"`
Expected: build fails with CS0246 naming `GpuScaledCopy`.

- [ ] **Step 3: Implement `GpuScaledCopy`**

`VertexSource` is Render3D's `ShaderSources.FullscreenVert` copied verbatim (the post chain proves it upright on every backend, and the quadrant test pins it here). Resource layout: binding 0 `texture2D Src`, binding 1 `sampler Samp`, fragment stage. The sampler is the instance's own `GpuSamplerDescription.Linear` (clamp-addressed), never the device's wrap-addressed `LinearSampler`, so edge taps do not bleed the opposite edge. The pipeline is built once for `new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm)`. One resource set is cached per source texture and rebuilt when the source instance changes. `Record` binds the destination, sets the full scissor and draws 3 vertices. Fragment body:

```glsl
vec2 o = 0.25 * vec2(dFdx(vUv.x), dFdy(vUv.y));   // a quarter of one destination texel in source UV
vec3 c = texture(sampler2D(Src, Samp), vUv + vec2(-o.x, -o.y)).rgb
       + texture(sampler2D(Src, Samp), vUv + vec2( o.x, -o.y)).rgb
       + texture(sampler2D(Src, Samp), vUv + vec2(-o.x,  o.y)).rgb
       + texture(sampler2D(Src, Samp), vUv + vec2( o.x,  o.y)).rgb;
oColor = vec4(c * 0.25, 1.0);
```

Four bilinear taps cover a 4x4 source footprint, which is a box filter up to a 4:1 reduction (2560 to 960 is 2.67:1).

- [ ] **Step 4: Register the program and regenerate the emission tables, once**

Add the `ShippedShaderPrograms` row and the 86 count, then run:

`build-slot --heavy --label e1-shader-tables -- env KE_UPDATE_HLSL_HASHES=1 KE_UPDATE_MSL_HASHES=1 KE_UPDATE_SPIRV_HASHES=1 KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~D3D11HlslByteEquality|FullyQualifiedName~MetalMslByteEquality|FullyQualifiedName~VulkanSpirvByteEquality|FullyQualifiedName~ShaderCorpusTests"`

Expected: `git diff --stat` on the four tables shows only added `GpuScaledCopy.*` lines. Any other moved line is a toolchain change and stops the task.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `build-slot --label e1-scaled-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuScaledCopy|FullyQualifiedName~ShaderCorpus|FullyQualifiedName~ByteEquality|FullyQualifiedName~D3D11FxcValidation"`
Expected: pass.

Run: `build-slot --heavy --label e1-scaled-gpu -- env KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuScaledCopyGpuTests"`
Expected: 3 passed, 0 skipped on Metal.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Gpu/GpuScaledCopy.cs KhaozEngine.Gpu/README.md KhaozEngine.Render.Tests/Gpu/GpuScaledCopyTests.cs KhaozEngine.Render.Tests/Gpu/GpuScaledCopyGpuTests.cs KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt
git commit -m "feat(gpu): add a scaled RGBA copy pass"
```

### Task 3: GpuReadbackRing

**Files:**
- Create: `KhaozEngine.Gpu/GpuReadbackRing.cs`
- Modify: `KhaozEngine.Gpu/README.md` (new `## GpuReadbackRing` section)
- Test: `KhaozEngine.Render.Tests/Gpu/GpuReadbackRingTests.cs`, `KhaozEngine.Render.Tests/Gpu/ReadbackRingFakeDevice.cs`, `KhaozEngine.Render.Tests/Gpu/GpuReadbackRingGpuTests.cs`

**Interfaces:**
- Consumes: the GPU seam only.
- Produces:

```csharp
namespace KhaozEngine.Gpu;
public sealed class GpuReadbackRing : IDisposable
{
    public GpuReadbackRing(IGpuDevice device, int width, int height, int slotCount = 3);
    public int Width { get; }
    public int Height { get; }
    public int SlotCount { get; }
    public int PendingCount { get; }          // slots recorded and not yet read
    public bool HasFreeSlot { get; }
    // Records, into an OPEN recording, a whole copy of source (R8G8B8A8UNorm, 1 mip, 1 sample, exactly
    // Width x Height) into a free slot. The caller hands fence to the submit that carries this recording.
    // False and nothing recorded when every slot is in flight.
    public bool TryRecordCopy(IGpuCommandList commands, IGpuTexture source, long tag, out IGpuFence fence);
    // Oldest completed slot first: maps, de-strides into destination (at least Width * Height * 4 bytes,
    // packed RGBA rows, top row first), unmaps, resets the fence, frees the slot. Never waits. Call only
    // while nothing is recording.
    public bool TryReadCompleted(Span<byte> destination, out long tag);
    public void Dispose();
}
```

- [ ] **Step 1: Write the failing tests**

`ReadbackRingFakeDevice` (test-only `IGpuDevice` over `FakeGpuDevice`'s factory): reports `SupportsCompletionFences`, `CreateFence` returns a `ManualFence` with `Signal()`, `Submit(cl, fence)` records the fence, `Map(texture)` returns pinned memory filled with a per-texture byte pattern at `RowPitch = Width * 4 + 16`, counts `WaitForIdleCalls`, `Maps`, and `CopyTexture` calls through a counting command list.

```csharp
[Fact] public void A_device_without_completion_fences_is_refused()
    => Assert.Throws<NotSupportedException>(() => new GpuReadbackRing(new FakeGpuDevice(), 8, 4));

[Fact] public void TryRecordCopy_records_one_copy_and_returns_an_unsignaled_fence()
    // true, CopyTextureCalls == 1, fence.Signaled false, PendingCount 1

[Fact] public void A_tick_with_every_slot_in_flight_is_refused_and_records_nothing()
    // three records succeed, the fourth returns false, CopyTextureCalls stays 3, HasFreeSlot false

[Fact] public void Nothing_is_read_until_a_fence_signals_and_nothing_ever_waits()
    // record, TryReadCompleted false, Maps 0, then Signal(), TryReadCompleted true. WaitForIdleCalls == 0 throughout

[Fact] public void Completed_slots_are_read_oldest_first_with_their_tags()
    // record tags 7, 8, 9, signal all three: reads return 7, 8, 9 in order

[Fact] public void A_read_slot_is_reset_and_reused()
    // fill three, read one, a fourth record succeeds and its fence was Reset (ManualFence.Resets == 1)

[Fact] public void Rows_are_destrided_from_the_mapped_pitch()
    // destination equals the fake's pattern packed at Width * 4 per row, the 16 pad bytes absent

[Fact] public void A_source_of_the_wrong_shape_is_refused()
    // 8x4 ring, 8x5 source -> ArgumentException, B8G8R8A8UNorm source -> ArgumentException, nothing recorded

[Fact] public void A_destination_shorter_than_one_frame_is_refused()
    // Span of Width * Height * 4 - 1 -> ArgumentException before any Map

[Fact] public void Dispose_with_slots_in_flight_frees_every_slot()
    // two in flight, Dispose: every staging texture and fence disposed, WaitForIdleCalls == 0
```

`GpuReadbackRingGpuTests`:

```csharp
[GpuFact(RequiresCompletionFences = true)]
public void A_copied_render_target_reads_back_exactly_through_a_polled_fence()
    // 13x7 RGBA8 render target uploaded with a known pattern, record copy, Submit(cl, fence),
    // poll fence.Signaled with Thread.Sleep(1) for at most 5 s, TryReadCompleted equals the pattern byte for byte

[GpuFact(RequiresCompletionFences = true)]
public void Three_slots_cycle_through_six_frames_without_a_device_wait()
    // six frames, each records then reads whatever completed: six reads total, each matching its frame's pattern
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-ring-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuReadbackRing"`
Expected: build fails with CS0246 naming `GpuReadbackRing`.

- [ ] **Step 3: Implement `GpuReadbackRing`**

Each slot owns one `Staging` texture of `Width x Height` `R8G8B8A8UNorm` and one fence. Slots are claimed in FIFO order and read in FIFO order, so a later slot is never read before an earlier one even if its fence reads signaled first. The shape check reuses the same rule `GpuReadback.RequireCopyableSource` enforces, written as a private check with the same message style. `Dispose` disposes slots without draining: a staging texture freed while in flight is the backends' existing deferred-destruction case, and the caller retires the ring only after `PendingCount` reaches zero (Task 10) or at window teardown after the device drains.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e1-ring-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuReadbackRingTests"`
Expected: 10 passed.

Run: `build-slot --heavy --label e1-ring-gpu -- env KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GpuReadbackRingGpuTests"`
Expected: 2 passed, 0 skipped on Metal.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gpu/GpuReadbackRing.cs KhaozEngine.Gpu/README.md KhaozEngine.Render.Tests/Gpu/GpuReadbackRingTests.cs KhaozEngine.Render.Tests/Gpu/ReadbackRingFakeDevice.cs KhaozEngine.Render.Tests/Gpu/GpuReadbackRingGpuTests.cs
git commit -m "feat(gpu): add a fenced non-blocking readback ring"
```

### Task 4: Swapchain capture seam and the Direct3D 11 implementation

**Files:**
- Create: `KhaozEngine.Gpu/IGpuSwapchainCapture.cs`, `KhaozEngine.Gpu/Internal/SwapchainCopyCheck.cs`
- Create: `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.SwapchainCapture.cs` (declares `partial class D3D11GpuDevice : IGpuSwapchainCapture`, so `D3D11GpuDevice.cs` does not grow)
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11Swapchain.cs` (expose the current colour attachment's texture as an internal read-only member)
- Modify: `KhaozEngine.Gpu/README.md` (new `## IGpuSwapchainCapture` section), `KhaozEngine.Gpu.D3D11/README.md` (one paragraph under its swapchain section)
- Test: `KhaozEngine.Render.Tests/Gpu/SwapchainCopyCheckTests.cs`, `KhaozEngine.Render.Tests/Gpu/D3D11SwapchainCaptureTests.cs`

**Interfaces:**
- Consumes: `D3D11Swapchain`, `D3D11SwapchainAttachments`, `FakeD3D11SwapchainSurface`.
- Produces:

```csharp
namespace KhaozEngine.Gpu;
// Optional device capability. The three native devices implement it. Fakes and headless devices need not.
public interface IGpuSwapchainCapture
{
    // Ask for copy-source usage on the swapchain colour images. Applied at the next present boundary.
    bool SwapchainCopySourceRequested { get; set; }
    // True when the image the current frame renders into can be copied out.
    bool SwapchainCopyable { get; }
    // Records, into an OPEN recording of commands, a whole copy of the current swapchain colour image into
    // destination: same width and height as SwapchainFramebuffer, the swapchain's colour format, 1 mip,
    // 1 sample. InvalidOperationException when SwapchainCopyable is false.
    void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination);
}

namespace KhaozEngine.Gpu.Internal;
internal static class SwapchainCopyCheck
{
    internal static void RequireDestination(IGpuFramebuffer swapchain, IGpuTexture destination);   // ArgumentException naming both shapes
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// SwapchainCopyCheckTests
[Fact] public void A_destination_matching_the_swapchain_is_accepted()
[Fact] public void A_destination_of_another_size_is_refused_naming_both_sizes()
[Fact] public void A_destination_of_another_format_is_refused_naming_both_formats()
[Fact] public void A_mipped_or_multisampled_destination_is_refused()

// D3D11SwapchainCaptureTests (device free, FakeD3D11SwapchainSurface)
[Fact] public void The_back_buffer_is_copyable_without_any_request()
    // a swapchain with live attachments reports copyable, and setting the request true then false changes nothing
[Fact] public void A_swapchain_with_its_attachments_released_is_not_copyable()
    // between ReleaseAttachments and CreateAttachments the capture reports false
[Fact] public void The_copy_source_is_the_current_colour_attachment_after_a_resize()
    // after a resize the exposed texture is the new attachment's, not the released one
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-d3d11-capture-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SwapchainCopyCheckTests|FullyQualifiedName~D3D11SwapchainCaptureTests"`
Expected: build fails with CS0246 naming `IGpuSwapchainCapture`.

- [ ] **Step 3: Implement the seam and the D3D11 device**

`SwapchainCopyable` is true while the device owns a swapchain whose attachments are live. The request is a stored value with no effect. `CopySwapchainColor` calls `SwapchainCopyCheck.RequireDestination`, then records the same copy command the D3D11 command list records for `CopyTexture`, with the back buffer as source, so replay issues `CopyResource` on the immediate context in recording order.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the command from Step 2.
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gpu/IGpuSwapchainCapture.cs KhaozEngine.Gpu/Internal/SwapchainCopyCheck.cs KhaozEngine.Gpu/README.md KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.SwapchainCapture.cs KhaozEngine.Gpu.D3D11/Internal/D3D11Swapchain.cs KhaozEngine.Gpu.D3D11/README.md KhaozEngine.Render.Tests/Gpu/SwapchainCopyCheckTests.cs KhaozEngine.Render.Tests/Gpu/D3D11SwapchainCaptureTests.cs
git commit -m "feat(gpu): add the swapchain capture capability for Direct3D 11"
```

### Task 5: Metal swapchain capture

**Files:**
- Create: `KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.SwapchainCapture.cs` (declares `partial class MetalGpuDevice : IGpuSwapchainCapture`)
- Modify: `KhaozEngine.Gpu.Metal/Internal/IMetalSwapchainApi.cs` (add `void SetFramebufferOnly(bool value);`), `KhaozEngine.Gpu.Metal/Internal/MetalSwapchainApi.cs` (implement it. `Configure` keeps writing `true` at creation, and the boundary re-applies a standing request after any later `Configure`), `KhaozEngine.Gpu.Metal/Internal/MetalPresentPending.cs` (queue a framebuffer-only change beside resize and vsync), `KhaozEngine.Gpu.Metal/Internal/MetalPresentBoundary.cs` (apply it in `ApplyPending` before `AcquireAndPublish`, and expose `internal bool CurrentDrawableCopyable`)
- Modify: `KhaozEngine.Render.Tests/Gpu/MetalSwapchainFakes.cs` (`FakeMetalSwapchainApi` logs `SetFramebufferOnly` and exposes `FramebufferOnly`)
- Modify: `KhaozEngine.Gpu.Metal/README.md` (the swapchain section says `framebufferOnly = true` unless a capture is requested)
- Test: `KhaozEngine.Render.Tests/Gpu/MetalSwapchainCaptureTests.cs`

**Interfaces:**
- Consumes: `IGpuSwapchainCapture`, `SwapchainCopyCheck` (Task 4).
- Produces: `MetalGpuDevice` implements `IGpuSwapchainCapture`. `MetalPresentPending.QueueFramebufferOnly(bool value)`, `MetalPresentBoundary.CurrentDrawableCopyable`.

- [ ] **Step 1: Write the failing tests** (device free, `FakeMetalSwapchainApi` and `FakeMetalOrphanTarget`)

```csharp
[Fact] public void The_layer_is_configured_framebuffer_only_by_default()
    // FramebufferOnly true after construction, CurrentDrawableCopyable false

[Fact] public void A_copy_source_request_clears_framebuffer_only_before_the_next_drawable_is_taken()
    // QueueFramebufferOnly(false), Present(): Log shows "SetFramebufferOnly(False)" before the next "NextDrawable"

[Fact] public void The_drawable_taken_before_the_request_is_not_copyable_and_the_next_one_is()
    // request mid-frame: CurrentDrawableCopyable false until the following Present, then true

[Fact] public void Releasing_the_request_restores_framebuffer_only()
    // request, Present, release, Present: FramebufferOnly true, CurrentDrawableCopyable false

[Fact] public void An_orphan_bound_boundary_is_never_copyable()
    // a minimised layer that hands back no drawable: CurrentDrawableCopyable false with the request on
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-metal-capture-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~MetalSwapchainCaptureTests"`
Expected: build fails naming `QueueFramebufferOnly`.

- [ ] **Step 3: Implement**

The boundary records, for the drawable it holds, the framebuffer-only value in force when that drawable was taken. `CurrentDrawableCopyable` is true only when a real drawable is held and that value was false. The device's `SwapchainCopySourceRequested` setter queues `!value` through `MetalPresentPending`. `CopySwapchainColor` checks `SwapchainCopyable`, runs `SwapchainCopyCheck.RequireDestination`, then records a blit copy from the held drawable's texture through the same path the Metal command list uses for `CopyTexture`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e1-metal-capture-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~MetalSwapchainCaptureTests|FullyQualifiedName~MetalPresentBoundaryTests|FullyQualifiedName~MetalSwapchainPolicyTests"`
Expected: pass, and the existing boundary tests still pass.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.SwapchainCapture.cs KhaozEngine.Gpu.Metal/Internal/IMetalSwapchainApi.cs KhaozEngine.Gpu.Metal/Internal/MetalSwapchainApi.cs KhaozEngine.Gpu.Metal/Internal/MetalPresentPending.cs KhaozEngine.Gpu.Metal/Internal/MetalPresentBoundary.cs KhaozEngine.Gpu.Metal/README.md KhaozEngine.Render.Tests/Gpu/MetalSwapchainFakes.cs KhaozEngine.Render.Tests/Gpu/MetalSwapchainCaptureTests.cs
git commit -m "feat(metal): copy the swapchain image on request"
```

### Task 6: Vulkan swapchain capture

**Files:**
- Create: `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.SwapchainCapture.cs` (declares `partial class VulkanGpuDevice : IGpuSwapchainCapture`, so `VulkanGpuDevice.cs` does not grow)
- Modify: `KhaozEngine.Gpu.Vulkan/Internal/VulkanSurfaceFacts.cs` (`VulkanSurfaceReport` gains `ImageUsageFlags SupportedUsage` as its last member), `KhaozEngine.Gpu.Vulkan/Internal/VulkanSurfaceApi.cs` (fill it from `supportedUsageFlags`), `KhaozEngine.Gpu.Vulkan/Internal/VulkanSwapchainSpec.cs` (add `ColourTransferDstAndSrc` and correct the `Usage` doc, which today says the transfer bit lets a capture copy one out although that needs `TRANSFER_SRC`), `KhaozEngine.Gpu.Vulkan/Internal/VulkanSwapchainPolicy.cs` (`Decide` gains `bool copySource`), `KhaozEngine.Gpu.Vulkan/Internal/VulkanPresentBoundary.cs` and `VulkanPresentBoundary.Recreate.cs` (hold the request, queue a recreate when it changes, record the generation's usage)
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanPresentHarness.cs` (`FakeVulkanSurfaceApi` reports `SupportedUsage`, default includes `TransferSrcBit`), `KhaozEngine.Render.Tests/Gpu/VulkanSwapchainPolicyTests.cs` (existing calls pass `copySource: false`)
- Modify: `KhaozEngine.Gpu.Vulkan/README.md` (usage flags paragraph)
- Test: `KhaozEngine.Render.Tests/Gpu/VulkanSwapchainCaptureTests.cs`

**Interfaces:**
- Consumes: `IGpuSwapchainCapture`, `SwapchainCopyCheck` (Task 4).
- Produces: `VulkanSwapchainPolicy.Decide(in VulkanSurfaceReport surface, VulkanExtent requested, bool syncToVerticalBlank, bool srgb, bool copySource, out string? warning)`, `VulkanSwapchainSpec.ColourTransferDstAndSrc`, `VulkanPresentBoundary.CopySourceRequested { get; set; }`, `VulkanPresentBoundary.CurrentGenerationCopyable`. `VulkanGpuDevice` implements `IGpuSwapchainCapture`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Without_a_request_the_usage_is_unchanged()
    // Decide(..., copySource: false): Usage == ColourAndTransferDst, warning null
[Fact] public void A_request_adds_TRANSFER_SRC_when_the_surface_supports_it()
    // Usage == ColourAndTransferDst | TransferSrcBit
[Fact] public void A_request_the_surface_cannot_honour_is_dropped_with_a_warning()
    // SupportedUsage without TransferSrcBit: Usage == ColourAndTransferDst, warning names TRANSFER_SRC
[Fact] public void Changing_the_request_queues_one_recreate()
    // boundary rig: CopySourceRequested = true, Present(): Swapchains.Created has 2 entries, the second with TransferSrcBit
[Fact] public void Setting_the_same_request_twice_queues_nothing()
[Fact] public void The_current_generation_is_copyable_only_when_its_usage_has_TRANSFER_SRC()
    // before the recreate false, after it true, after releasing and recreating false
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-vulkan-capture-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSwapchainCaptureTests"`
Expected: build fails naming `copySource`.

- [ ] **Step 3: Implement**

`Decide` adds `TransferSrcBit` only when `copySource` is true and `surface.SupportedUsage` has it, otherwise returns the warning `"The surface does not support TRANSFER_SRC on swapchain images, so frame capture is unavailable."`. The boundary passes its request into `Decide` on every recreation and keeps `CurrentGenerationCopyable` from the created spec. `CopySwapchainColor` checks `SwapchainCopyable`, runs `SwapchainCopyCheck.RequireDestination`, then records the copy through the command list's existing tracked-image copy path with the bound swapchain attachment as source, so the layout tracker transitions it to `TRANSFER_SRC_OPTIMAL` and back to its `PresentSrcKhr` resting layout before present.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e1-vulkan-capture-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSwapchainCaptureTests|FullyQualifiedName~VulkanSwapchainPolicyTests|FullyQualifiedName~VulkanPresentBoundaryTests"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.SwapchainCapture.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanSurfaceFacts.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanSurfaceApi.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanSwapchainSpec.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanSwapchainPolicy.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanPresentBoundary.cs KhaozEngine.Gpu.Vulkan/Internal/VulkanPresentBoundary.Recreate.cs KhaozEngine.Gpu.Vulkan/README.md KhaozEngine.Render.Tests/Gpu/VulkanPresentHarness.cs KhaozEngine.Render.Tests/Gpu/VulkanSwapchainPolicyTests.cs KhaozEngine.Render.Tests/Gpu/VulkanSwapchainCaptureTests.cs
git commit -m "feat(vulkan): request TRANSFER_SRC swapchain images for capture"
```

### Task 7: Frame tap in Windowing

**Files:**
- Create: `KhaozEngine.Windowing/IFrameCaptureTap.cs`, `KhaozEngine.Windowing/FrameCaptureContext.cs`, `KhaozEngine.Windowing/FrameCaptureTaps.cs`, `KhaozEngine.Windowing/AppWindow.CaptureTaps.cs`
- Modify: `KhaozEngine.Windowing/FramePhases.cs` (new overload, the old one forwards with `null, 0`), `KhaozEngine.Windowing/AppWindow.Frames.cs` (pass `_captureTaps` and `dt * 1000.0`), `KhaozEngine.Windowing/AppWindow.cs` (one line in `Dispose`: `_captureTaps?.Dispose();` before the device is disposed)
- Modify: `KhaozEngine.Render.Tests/Gpu/OpenListTrackingGpuDevice.cs` (add `FencedSubmits` and `Presents` counters, an `Order` log of `"submit"`, `"submit+fence"`, `"present"`, and an optional constructor parameter that makes `SwapchainFramebuffer` a fake 320x200 `B8G8R8A8UNorm` framebuffer instead of null)
- Modify: `KhaozEngine.Windowing/README.md` (new `## Frame capture taps` section)
- Test: `KhaozEngine.Render.Tests/Windowing/FrameCaptureTapsTests.cs`

**Interfaces:**
- Consumes: `IGpuSwapchainCapture` (Task 4), `GpuRetireQueue`.
- Produces:

```csharp
namespace KhaozEngine.Windowing;
public interface IFrameCaptureTap
{
    void Record(FrameCaptureContext context);       // inside the frame's recording, after the game's draws
    void AfterSubmit(FrameCaptureContext context);  // after Submit, before Present, nothing recording
    void Detach(Action<IDisposable> retire);        // once, on removal or window disposal: hand over GPU resources
}
public sealed class FrameCaptureContext   // internal constructor, built by FrameCaptureTaps once per frame
{
    public Frame Frame { get; }
    public IGpuDevice Device { get; }
    public IGpuCommandList Commands { get; }        // open during Record only
    public double FrameMilliseconds { get; }        // unclamped loop delta
    public IGpuTexture? AcquireSwapchainImage();    // Record only. One copy per frame shared by every tap. Null when not copyable
    public void SignalOnSubmit(IGpuFence fence);    // Record only. At most once per frame, a second call throws InvalidOperationException
    public void Retire(IDisposable resource);       // destroyed once the GPU has finished with it
}
public sealed partial class AppWindow
{
    public void AddCaptureTap(IFrameCaptureTap tap);     // idempotent per instance, frame thread
    public void RemoveCaptureTap(IFrameCaptureTap tap);  // calls tap.Detach, safe from inside a tap callback
}
internal sealed class FrameCaptureTaps : IDisposable
{
    internal FrameCaptureTaps(IGpuDevice device, IGpuSwapchainCapture? capture);
    internal int Count { get; }
    internal bool HasWork { get; }                  // taps registered or retirements pending
    internal void Add(IFrameCaptureTap tap);
    internal void Remove(IFrameCaptureTap tap);
    internal IGpuFence? Record(Frame frame, IGpuCommandList commands, double frameMilliseconds);
    internal void AfterSubmit();
    public void Dispose();                          // detaches every tap, then GpuRetireQueue.FlushAll
}
static class FramePhases
{
    internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands, Color clearColor,
        Action<Frame>? onPrepare, Action<Frame> onFrame, FrameCaptureTaps? taps, double frameMilliseconds);
}
```

- [ ] **Step 1: Write the failing tests** (`OpenListTrackingGpuDevice(completionFences: true)`, a `RecordingTap` that logs callbacks with `device.OpenLists`, and a `FakeSwapchainCapture : IGpuSwapchainCapture` with settable `SwapchainCopyable`, a `Requested` history and a `Copies` count)

```csharp
[Fact] public void With_no_tap_registered_a_frame_submits_once_without_a_fence_then_presents()
    // taps: null and taps: empty registry both give Order == { "submit", "present" }, FencedSubmits 0
[Fact] public void A_tap_records_inside_the_frame_list_after_the_game_draws()
    // log: "frame", then "record" with OpenLists == 1
[Fact] public void AfterSubmit_runs_with_nothing_recording_between_submit_and_present()
    // Order: "submit", "after", "present", and OpenLists == 0 during "after"
[Fact] public void A_fence_handed_in_by_a_tap_rides_the_frame_submit()
    // FencedSubmits == 1, Submits == 1
[Fact] public void A_second_fence_in_one_frame_is_refused()
    // two taps each call SignalOnSubmit: InvalidOperationException
[Fact] public void A_render_suppressed_frame_runs_no_tap()
    // render: false: no "record", no "after", no submit
[Fact] public void The_swapchain_is_copied_once_per_frame_however_many_taps_ask()
    // two taps call AcquireSwapchainImage: same texture instance, FakeSwapchainCapture.Copies == 1
[Fact] public void An_uncopyable_swapchain_gives_every_tap_null()
[Fact] public void Copy_source_usage_is_requested_with_the_first_tap_and_released_after_the_last()
    // Requested == { true, false } across add A, add B, remove A, remove B
[Fact] public void A_tap_may_remove_itself_from_inside_Record()
    // it gets Detach once, is not called next frame, and the other tap still runs this frame
[Fact] public void Disposing_the_registry_detaches_every_tap()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-taps-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FrameCaptureTapsTests|FullyQualifiedName~FramePhasesTests"`
Expected: build fails with CS0246 naming `IFrameCaptureTap`.

- [ ] **Step 3: Implement**

`FrameCaptureTaps` keeps a copy-on-write `IFrameCaptureTap[]`, so removal during a callback never disturbs the iteration in progress. It owns the swapchain image copy: a texture of the swapchain's size and `Outputs.Colour[0]` format with `Sampled` usage, created on first use, retired and recreated when the swapchain size changes, retired when the last tap leaves. Its `GpuRetireQueue` is created with the first tap through `GpuRetireQueue.Create(device)` and advanced with `BeginFrame()` at the end of `AfterSubmit`, where nothing records. `FramePhases.Run` with a null or empty registry executes the existing body without change. Otherwise it follows decision 3. `AppWindow.CaptureTaps.cs` holds `FrameCaptureTaps? _captureTaps`, creates it on the first `AddCaptureTap` with `_device as IGpuSwapchainCapture`, and forwards both methods.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the command from Step 2.
Expected: the 11 new tests and every existing `FramePhasesTests` pass.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Windowing/IFrameCaptureTap.cs KhaozEngine.Windowing/FrameCaptureContext.cs KhaozEngine.Windowing/FrameCaptureTaps.cs KhaozEngine.Windowing/AppWindow.CaptureTaps.cs KhaozEngine.Windowing/FramePhases.cs KhaozEngine.Windowing/AppWindow.Frames.cs KhaozEngine.Windowing/AppWindow.cs KhaozEngine.Windowing/README.md KhaozEngine.Render.Tests/Gpu/OpenListTrackingGpuDevice.cs KhaozEngine.Render.Tests/Windowing/FrameCaptureTapsTests.cs
git commit -m "feat(windowing): add frame capture taps"
```

### Task 8: The two packages, media records and the clip ring

**Files:**
- Create: `KhaozEngine.BugReports/KhaozEngine.BugReports.csproj`, `KhaozEngine.BugReports/README.md`, `KhaozEngine.BugReports/ReportImage.cs`, `KhaozEngine.BugReports/ReportClipFrame.cs`, `KhaozEngine.BugReports/ReportClip.cs`
- Create: `KhaozEngine.BugReports.Client/KhaozEngine.BugReports.Client.csproj` (references `BugReports`, `Gpu`, `Windowing`, `Imaging`, `InternalsVisibleTo KhaozEngine.BugReports.Client.Tests`), `KhaozEngine.BugReports.Client/README.md`, `KhaozEngine.BugReports.Client/FrameHistoryOptions.cs`, `KhaozEngine.BugReports.Client/Internal/CaptureSize.cs`, `KhaozEngine.BugReports.Client/Internal/ClipFrameRecord.cs`, `KhaozEngine.BugReports.Client/Internal/FrameHistory.cs`
- Create: `KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj` (pattern of `KhaozEngine.Windowing.Tests.csproj`, `RootNamespace` `KhaozEngine.Tests.BugReports.Client`, one `ProjectReference` to the Client)
- Create: `KhaozEngine.Tests/ArchitectureTests.BugReports.cs`
- Modify: `KhaozEngine.slnx` (three `<Project>` lines), root `README.md` (catalog rows `**KhaozEngine.BugReports**` and `**KhaozEngine.BugReports.Client**` with their `Depends on`)
- Test: `KhaozEngine.BugReports.Client.Tests/CaptureSizeTests.cs`, `KhaozEngine.BugReports.Client.Tests/FrameHistoryTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks at compile time beyond the project references.
- Produces: the contract records verbatim, `FrameHistoryOptions` verbatim, and

```csharp
namespace KhaozEngine.BugReports.Client.Internal;
internal static class CaptureSize { internal static (int Width, int Height) Fit(int width, int height, int longEdge); }
internal readonly record struct ClipFrameRecord(TimeSpan CapturedAt, double RenderMilliseconds, int Width, int Height,
    byte[] Jpeg, int Generation);
internal sealed class FrameHistory
{
    internal FrameHistory(FrameHistoryOptions options);
    internal TimeSpan Interval { get; }             // 1 s / FramesPerSecond
    internal int Generation { get; }
    internal int Count { get; }
    internal long TotalBytes { get; }
    internal bool IsSuspended { get; }
    internal bool IsFrozen { get; }
    internal bool TryClaimTick(TimeSpan now);
    internal void Append(ClipFrameRecord frame);    // thread-safe, ignored while frozen or from an older generation
    internal void Suspend();
    internal void Resume();
    internal ReportClip Freeze();
    internal void Thaw();
    internal void Clear();                          // drops every frame, Generation + 1, resets the tick schedule
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// CaptureSizeTests
[Theory, InlineData(2560, 1440, 960, 960, 540), InlineData(1440, 2560, 960, 540, 960),
         InlineData(800, 600, 960, 800, 600), InlineData(3001, 7, 960, 960, 2), InlineData(1, 5000, 960, 1, 960)]
public void Fit_scales_the_long_edge_down_and_never_up(int w, int h, int edge, int ew, int eh)
    => Assert.Equal((ew, eh), CaptureSize.Fit(w, h, edge));

// FrameHistoryTests (times are passed in, no clock)
[Theory, InlineData(60.0), InlineData(144.0), InlineData(30.0)]
public void Ticks_hold_twelve_per_second_at_any_display_rate(double hz)
    // step now by 1/hz for 5 s calling TryClaimTick: claimed count is 60 plus or minus 1
[Fact] public void A_display_slower_than_the_rate_claims_every_frame()
    // 10 Hz for 5 s: 50 claimed
[Fact] public void The_ring_never_holds_more_than_duration_times_rate_frames()
    // append 200 frames 1/12 s apart: Count == 60, oldest CapturedAt > newest - 5 s
[Fact] public void The_byte_cap_drops_the_oldest_frames_first()
    // MaxBytes 1000, frames of 300 bytes: Count == 3, TotalBytes <= 1000, the newest three remain
[Fact] public void Frames_of_different_sizes_keep_their_own_dimensions()
    // 960x540 then 960x720 then 540x960: Freeze returns each frame's own Width and Height
[Fact] public void Freeze_returns_offsets_from_the_first_frame_and_render_times()
    // CapturedAt 10.0 s, 10.1 s, 10.2 s: Offsets 0, 100, 200 ms, RenderMilliseconds carried through
[Fact] public void Freeze_with_no_frames_returns_an_empty_clip()
    // Frames is empty, never null
[Fact] public void While_frozen_no_tick_is_claimed_and_appends_are_ignored()
[Fact] public void Thaw_resumes_ticks_and_keeps_the_frames_from_before_the_freeze()
[Fact] public void While_suspended_no_tick_is_claimed_but_frames_are_kept()
[Fact] public void Clear_drops_every_frame_and_rejects_frames_from_the_old_generation()
```

`ArchitectureTests.BugReports.cs`:

```csharp
[Fact] public void BugReports_StaysHeadless()
    // TransitiveClosure("KhaozEngine.BugReports", graph) contains none of GpuRuntimeStack plus "Game"
[Fact] public void BugReports_HasNoThirdPartyPackage()
[Fact] public void BugReportsPackages_AreExplicitReferencesOutsideEveryUmbrella()
    // neither package in any Umbrellas closure
[Fact] public void BugReportsClientTests_ReferenceOnlyTheClient()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-history-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release`
Expected: build fails with CS0246 naming `FrameHistory`.

- [ ] **Step 3: Implement**

`FrameHistory` holds frames in a `Queue<ClipFrameRecord>` behind one lock. After each append it removes frames whose `CapturedAt <= newest.CapturedAt - Duration`, then removes the oldest while `TotalBytes > MaxBytes`. The tick schedule keeps the average rate at any display rate rather than drifting to the next whole frame:

```csharp
internal bool TryClaimTick(TimeSpan now)
{
    if (IsSuspended || IsFrozen || now < _nextDue) return false;
    _nextDue = now - _nextDue >= Interval ? now + Interval : _nextDue + Interval;
    return true;
}
```

`_nextDue` starts at `TimeSpan.Zero` and `Clear` resets it there. Package READMEs: `KhaozEngine.BugReports` says it is the headless report model, that E1 ships the media records and that the rest of the core follows. `KhaozEngine.BugReports.Client` gives the `FrameHistoryRecorder` quick start (completed in Task 10). Root catalog rows: `BugReports` depends on nothing, `BugReports.Client` on `BugReports, Gpu, Windowing, Imaging`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e1-history-green -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release`
Expected: pass.

Run: `build-slot --label e1-arch -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: pass, including `UmbrellaMembership_MatchesTheLockedList`.

Run: `bash scripts/check-doc-versions.sh`
Expected: no `FAIL` line.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports KhaozEngine.BugReports.Client/KhaozEngine.BugReports.Client.csproj KhaozEngine.BugReports.Client/README.md KhaozEngine.BugReports.Client/FrameHistoryOptions.cs KhaozEngine.BugReports.Client/Internal/CaptureSize.cs KhaozEngine.BugReports.Client/Internal/ClipFrameRecord.cs KhaozEngine.BugReports.Client/Internal/FrameHistory.cs KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj KhaozEngine.BugReports.Client.Tests/CaptureSizeTests.cs KhaozEngine.BugReports.Client.Tests/FrameHistoryTests.cs KhaozEngine.Tests/ArchitectureTests.BugReports.cs KhaozEngine.slnx README.md
git commit -m "feat(bugreports): add the report media records and the clip ring"
```

### Task 9: Encode queue, screenshot encoder and tap guard

**Files:**
- Create: `KhaozEngine.BugReports.Client/Internal/RawClipFrame.cs`, `KhaozEngine.BugReports.Client/Internal/ClipEncodeQueue.cs`, `KhaozEngine.BugReports.Client/Internal/ScreenshotEncoder.cs`, `KhaozEngine.BugReports.Client/Internal/TapGuard.cs`
- Test: `KhaozEngine.BugReports.Client.Tests/ClipEncodeQueueTests.cs`, `KhaozEngine.BugReports.Client.Tests/ScreenshotEncoderTests.cs`, `KhaozEngine.BugReports.Client.Tests/TapGuardTests.cs`

**Interfaces:**
- Consumes: `ClipFrameRecord` (Task 8), `JpegWriter` (Task 1), `PngWriter`, `ReportImage`.
- Produces:

```csharp
namespace KhaozEngine.BugReports.Client.Internal;
// Rgba is rented from the queue's pool (ArrayPool<byte>.Shared unless a test injects one) and may be longer than Width * Height * 4.
internal sealed record RawClipFrame(byte[] Rgba, int Width, int Height, TimeSpan CapturedAt, double RenderMilliseconds, int Generation);
internal sealed class ClipEncodeQueue : IDisposable
{
    internal const int Capacity = 2;
    internal ClipEncodeQueue(Func<RawClipFrame, byte[]> encode, Action<ClipFrameRecord> deliver, ArrayPool<byte>? pool = null);
    internal int Dropped { get; }
    internal void Enqueue(RawClipFrame frame);      // never blocks, drops the oldest waiting frame when full
    internal bool WaitIdle(TimeSpan timeout);       // true once nothing waits or encodes
    public void Dispose();                          // stops the worker, returns every buffer
}
internal static class ScreenshotEncoder
{
    internal const int MaxLongEdge = 2560;
    internal const int PngLimitBytes = 4 * 1024 * 1024;
    internal const int JpegQuality = 92;
    internal static ReportImage Encode(byte[] rgba, int width, int height);
}
internal sealed class TapGuard
{
    internal TapGuard(string owner, Action? onTrip = null, Action<string, Exception>? warn = null);   // warn defaults to Log.Warn
    internal bool Tripped { get; }
    internal bool Run(Action body);                 // on a throw: warn once, onTrip once, false. Once tripped: false without running
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ClipEncodeQueueTests (a gated encoder: blocks on a ManualResetEventSlim until released)
[Fact] public void Frames_are_encoded_off_the_calling_thread_and_delivered_in_order()
    // three frames: deliver sees CapturedAt ascending, encoder thread id != test thread id
[Fact] public void A_full_queue_drops_the_oldest_waiting_frame_and_never_blocks()
    // encoder gated on frame 1, enqueue 2, 3, 4 each returning within 50 ms: Dropped == 1, delivered 1, 3, 4
[Fact] public void Every_buffer_goes_back_to_the_pool_after_encode_or_drop()
    // an injected counting ArrayPool<byte>: every buffer the test rented from it is returned exactly once after WaitIdle
[Fact] public void WaitIdle_returns_true_once_the_queue_drains()
[Fact] public void WaitIdle_returns_false_when_the_encoder_outlives_the_timeout()
[Fact] public void Dispose_stops_the_worker_with_frames_still_waiting()

// ScreenshotEncoderTests
[Fact] public void A_small_image_is_a_PNG()
    // 64x32 gradient: MediaType "image/png", bytes start with the PNG signature, Width 64, Height 32
[Fact] public void An_image_whose_PNG_is_4_MB_or_more_falls_back_to_JPEG_quality_92()
    // 2560x1440 deterministic noise: MediaType "image/jpeg", bytes start FF D8, DQT luma entry 0 equals the q92 scaled value 3
[Fact] public void A_buffer_longer_than_one_frame_is_encoded_from_its_prefix()
    // rented-size buffer gives the same bytes as the exact-size one

// TapGuardTests
[Fact] public void A_body_that_returns_runs_and_reports_true()
[Fact] public void A_fault_is_reported_once_and_the_guard_stays_tripped()
    // first Run throws inside: false, Tripped, the injected warn saw one call naming the owner, onTrip ran once,
    // a second Run does not invoke the body
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-encode-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~ClipEncodeQueueTests|FullyQualifiedName~ScreenshotEncoderTests|FullyQualifiedName~TapGuardTests"`
Expected: build fails with CS0246 naming `ClipEncodeQueue`.

- [ ] **Step 3: Implement**

`ClipEncodeQueue` runs one background thread (`IsBackground = true`) waiting on a semaphore over a two-entry deque under one lock. The encoder receives the frame and encodes `Rgba.AsSpan(0, Width * Height * 4)`. A dropped or encoded frame's buffer goes back to `ArrayPool<byte>.Shared` exactly once. `ScreenshotEncoder.Encode` tries `PngWriter.Encode` and returns it when shorter than `PngLimitBytes`, otherwise `JpegWriter.Encode(..., JpegQuality)`. It does not resize: the GPU path in Task 10 already delivers at most `MaxLongEdge`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the command from Step 2.
Expected: 11 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Client/Internal/RawClipFrame.cs KhaozEngine.BugReports.Client/Internal/ClipEncodeQueue.cs KhaozEngine.BugReports.Client/Internal/ScreenshotEncoder.cs KhaozEngine.BugReports.Client/Internal/TapGuard.cs KhaozEngine.BugReports.Client.Tests/ClipEncodeQueueTests.cs KhaozEngine.BugReports.Client.Tests/ScreenshotEncoderTests.cs KhaozEngine.BugReports.Client.Tests/TapGuardTests.cs
git commit -m "feat(bugreports): encode clip frames and screenshots off the frame thread"
```

### Task 10: FrameHistoryRecorder and its taps

**Files:**
- Create: `KhaozEngine.BugReports.Client/FrameHistoryRecorder.cs`, `KhaozEngine.BugReports.Client/Internal/IFrameCaptureHost.cs`, `KhaozEngine.BugReports.Client/Internal/AppWindowCaptureHost.cs`, `KhaozEngine.BugReports.Client/Internal/ClipCaptureTap.cs`, `KhaozEngine.BugReports.Client/Internal/ScreenshotTap.cs`
- Modify: `KhaozEngine.BugReports.Client/README.md` (quick start, lifecycle table, availability rules, the standby screenshot tap)
- Test: `KhaozEngine.BugReports.Client.Tests/FrameHistoryRecorderTests.cs`, `KhaozEngine.BugReports.Client.Tests/FakeCaptureHost.cs`, `KhaozEngine.BugReports.Client.Tests/ManualTimeProvider.cs` (a `TimeProvider` whose `GetTimestamp` the test advances)

**Interfaces:**
- Consumes: `IFrameCaptureTap`, `FrameCaptureContext`, `AppWindow.AddCaptureTap/RemoveCaptureTap` (Task 7), `GpuScaledCopy` (Task 2), `GpuReadbackRing` (Task 3), `IGpuSwapchainCapture` (Task 4), `FrameHistory`, `CaptureSize` (Task 8), `ClipEncodeQueue`, `ScreenshotEncoder`, `TapGuard` (Task 9), `GpuReadback.ToRgba`, `JpegWriter`.
- Produces: the contract's `FrameHistoryRecorder` verbatim, plus

```csharp
internal interface IFrameCaptureHost
{
    bool SupportsClipCapture { get; }       // completion fences and a device implementing IGpuSwapchainCapture
    bool SupportsSwapchainCopy { get; }     // a device implementing IGpuSwapchainCapture
    void AddCaptureTap(IFrameCaptureTap tap);
    void RemoveCaptureTap(IFrameCaptureTap tap);
}
// in FrameHistoryRecorder
internal FrameHistoryRecorder(IFrameCaptureHost host, FrameHistoryOptions options, TimeProvider time,
    Func<RawClipFrame, byte[]>? encode = null);
internal FrameHistory History { get; }
internal TapGuard ClipGuard { get; }                  // created in the constructor, onTrip stops recording
internal void OnClipEncoded(ClipFrameRecord frame);   // the queue's deliver target, exposed for tests
```

- [ ] **Step 1: Write the failing tests** (`FakeCaptureHost` records added and removed taps and calls `Detach` on removal)

```csharp
[Fact] public void Construction_registers_only_the_standby_screenshot_tap()
    // host.Taps.Count == 1, IsRecording false
[Fact] public void Start_registers_the_clip_tap_and_Stop_removes_it_and_clears_the_ring()
    // after Start 2 taps and IsRecording, after Stop 1 tap, History.Count 0, the clip tap was detached once
[Fact] public void An_unavailable_device_never_records()
    // SupportsClipCapture false: IsAvailable false, Start leaves IsRecording false and 1 tap
[Fact] public void Options_the_bundle_cannot_carry_are_refused()
    // FramesPerSecond 30 with Duration 10 s, LongEdge 4096, LongEdge 0, JpegQuality 0, JpegQuality 101,
    // MaxBytes 0, Duration zero: each ArgumentOutOfRangeException whose ParamName names the option
[Fact] public void Freeze_returns_the_clip_and_stops_ticks_until_Thaw()
[Fact] public void Freeze_keeps_frames_that_were_still_encoding()
    // gated encoder holding one frame: Freeze waits up to 250 ms and the clip includes it once released within that time
[Fact] public void Suspend_and_Resume_pause_ticks_without_dropping_frames()
[Fact] public void CaptureScreenshotAsync_faults_with_NotSupportedException_without_swapchain_copy()
[Fact] public void A_second_screenshot_request_while_one_is_pending_returns_the_same_task()
[Fact] public void A_tripped_clip_guard_stops_recording()
    // Start, then recorder.ClipGuard.Run(() => throw new InvalidOperationException()): IsRecording false,
    // the clip tap removed and detached once
[Fact] public void Dispose_removes_every_tap_and_faults_a_pending_screenshot()
    // ObjectDisposedException on the pending task, host.Taps empty
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e1-recorder-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~FrameHistoryRecorderTests"`
Expected: build fails with CS0246 naming `FrameHistoryRecorder`.

- [ ] **Step 3: Implement the recorder**

Option bounds: `Duration > 0`, `FramesPerSecond` in `1..60`, `ceil(Duration.TotalSeconds * FramesPerSecond) <= 120` (E2's `clip/000.jpg` to `clip/119.jpg`), `LongEdge` in `16..2560`, `JpegQuality` in `1..100`, `MaxBytes > 0`. `IsAvailable` is `host.SupportsClipCapture`. `Start` on an available recorder adds the clip tap. `Stop` removes it, clears the history and the queue. `Freeze` calls `ClipEncodeQueue.WaitIdle(TimeSpan.FromMilliseconds(250))`, then `History.Freeze()`. The default encoder is `f => JpegWriter.Encode(f.Rgba.AsSpan(0, f.Width * f.Height * 4), f.Width, f.Height, options.JpegQuality)`. The public constructor is `this(new AppWindowCaptureHost(window), options, TimeProvider.System)`. Every method is frame-thread only, except that the encode worker delivers through `History.Append`, which locks.

- [ ] **Step 4: Implement `ClipCaptureTap`**

Every callback body runs inside its `TapGuard`, and a trip calls back into the recorder to stop recording.

- `Record`: return unless `History.TryClaimTick(now)`. Take `image = context.AcquireSwapchainImage()`, return if null. Compute `CaptureSize.Fit(image.Width, image.Height, LongEdge)`. On a size change, `context.Retire` the old target and framebuffer, move the old ring to a retiring list, create a `Sampled | RenderTarget` `R8G8B8A8UNorm` target, its framebuffer and a new `GpuReadbackRing`. Return when `!ring.HasFreeSlot` (the dropped tick). Otherwise `GpuScaledCopy.Record(commands, image, framebuffer)`, `ring.TryRecordCopy(commands, target, tag, out fence)`, `context.SignalOnSubmit(fence)`, and remember `tag -> (now, context.FrameMilliseconds, width, height, History.Generation)`.
- `AfterSubmit`: for the current ring and every retiring ring, while `TryReadCompleted` succeeds into a buffer rented at `width * height * 4` from the same pool the encode queue returns buffers to, enqueue a `RawClipFrame`. Dispose each retiring ring whose `PendingCount` is 0.
- `Detach(retire)`: retire the target, framebuffer, rings and the `GpuScaledCopy`.

- [ ] **Step 5: Implement `ScreenshotTap`**

- `Arm()` returns the pending `TaskCompletionSource<ReportImage>.Task`, or a new one.
- `Record`: when armed and `AcquireSwapchainImage()` is not null, fit the size to `ScreenshotEncoder.MaxLongEdge`, create a one-shot `R8G8B8A8UNorm` render target and framebuffer at that size, and `GpuScaledCopy.Record` into it.
- `AfterSubmit`: `GpuReadback.ToRgba(context.Device, target, w, h)` (one blocking readback, legal because nothing records), `context.Retire` the target and framebuffer, disarm, then `Task.Run(() => ScreenshotEncoder.Encode(...))` completes the source. A guard trip faults the source.
- `Detach`: fault a pending source with `ObjectDisposedException` and retire resources.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `build-slot --label e1-recorder-green -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release`
Expected: every client test passes.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.BugReports.Client/FrameHistoryRecorder.cs KhaozEngine.BugReports.Client/Internal/IFrameCaptureHost.cs KhaozEngine.BugReports.Client/Internal/AppWindowCaptureHost.cs KhaozEngine.BugReports.Client/Internal/ClipCaptureTap.cs KhaozEngine.BugReports.Client/Internal/ScreenshotTap.cs KhaozEngine.BugReports.Client/README.md KhaozEngine.BugReports.Client.Tests/FrameHistoryRecorderTests.cs KhaozEngine.BugReports.Client.Tests/FakeCaptureHost.cs KhaozEngine.BugReports.Client.Tests/ManualTimeProvider.cs
git commit -m "feat(bugreports): record the rolling clip and the trigger screenshot"
```

### Task 11: Whole-branch verification and documentation sweep

**Files:**
- Modify: any Markdown file the sweep finds stale. Expected hits: `KhaozEngine.Gpu.Metal/README.md` (`framebufferOnly = true`, already corrected in Task 5), `KhaozEngine.Gpu.Vulkan/README.md` swapchain usage, `docs/DEPENDENCY-SEAMS.md` only if a sentence lists GPU seam members exhaustively.

**Interfaces:**
- Consumes: everything above.
- Produces: a verified branch head. No new API.

- [ ] **Step 1: Sweep the names and the old behaviour**

Run: `git grep -n -w -e JpegWriter -e GpuScaledCopy -e GpuReadbackRing -e IGpuSwapchainCapture -e IFrameCaptureTap -e FrameCaptureContext -e FrameHistoryRecorder -e framebufferOnly -e TransferDst -- '*.md'`
Expected: every hit describes the new behaviour. Fix any stale sentence. `docs/USING-KHAOZENGINE.md` and `docs/INDEX.md` stay for E3's documentation task, as the spec's documentation section assigns.

- [ ] **Step 2: Full Release build**

Run: `mkdir -p local-feed && build-slot --heavy --label e1-build -- dotnet build KhaozEngine.slnx -c Release`
Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Full Release test**

Run: `build-slot --heavy --label e1-test -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`
Expected: exit code 0, no failed tests.

- [ ] **Step 4: Local GPU slice**

Run: `build-slot --heavy --label e1-gpu -- env KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~GpuScaledCopyGpuTests|FullyQualifiedName~GpuReadbackRingGpuTests"`
Expected: 5 passed, 0 skipped on Metal. The D3D11 and Vulkan legs run in the CI GPU matrix after push.

- [ ] **Step 5: Repository guards**

Run each: `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `bash scripts/check-doc-versions.sh`
Expected: each exits 0.

- [ ] **Step 6: Commit the sweep, if it changed anything**

```bash
git add <each corrected Markdown path>
git commit -m "docs(bugreports): sweep capture names across the docs"
```

- [ ] **Step 7: Record what remains open for the orchestrator**

Report, without filing: the Windows and Linux `--jpeg` figures, the live windowed check of the frame tap on each backend (the spec assigns it to the first consumer's live check), and the E2 note that `BugReportScreen` may open on the frame after the trigger because the screenshot pixels are taken in the trigger frame.
