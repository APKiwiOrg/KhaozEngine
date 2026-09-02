# Playtest Automation Design (2026-09-02)

Status: proposed, nothing built. Program issue: filed with this doc.

## Problem

Content review during development is a loop with a human in the middle of every turn. The owner boots
the client, plays, notices something, and reports it by screenshot. The agent doing the work cannot see
the client, cannot drive it, and cannot read its state, so every verdict costs a round trip through a
person who has to reproduce the situation by hand each time.

The ask, in the owner's words: "Can we have a MCP (just for me) or something so YOU can playtest and
review content for me during development (only, I dont want players automating gameplay)".

Two halves, and the second is a hard constraint rather than a preference:

1. During development, on the owner's machine, an agent can boot the client, look at it, click in it,
   read its state, and report.
2. A player must never be able to automate gameplay with any of it. A shipped client must not contain
   the mechanism at all, not merely refuse to enable it.

Goals: the agent sees real pixels from the real client, acts through the real input path (so a bug in
input handling is reachable), and asserts on real game state (so a report is a fact rather than a
guess about a picture). One boot command. Deterministic enough that a failure is reproducible.

Non-goals, all of them permanent: anything reachable from a player build, gameplay bots, remote
access, headless CI use of this path (the existing suites own that), and driving a released client for
support or debugging.

## What exists today

Every claim below was read out of the tree at engine `18.11.0` and Grimhollow `0.3.0`.

**Input has exactly one producer and one consumer shape.** `AppWindow` translates Silk and GLFW events
into engine types through `InputAccumulator` (`KhaozEngine.Windowing/AppWindow.cs:667` to `:706`), and
folds them with this frame's cursor, framebuffer size and gamepads into one immutable snapshot in
`BuildInput` (`KhaozEngine.Windowing/AppWindow.cs:716`, the fold at `:726`). The frame loop calls it
once (`KhaozEngine.Windowing/AppWindow.Frames.cs:57`) and latches the result onto the frame
(`:65`). Everything downstream reads that snapshot: `GameApp` copies it at
`KhaozEngine.Game/GameApp.cs:541`, exposes it at `:288`, and drives both pointers from it (`:566`,
`:571`). `InputState` is a sealed immutable class with a public constructor and `IReadOnlySet` members
(`KhaozEngine.Windowing/Input.cs:31`, ctor at `:82`), so a composed snapshot is a union of sets and a
new instance. Nothing outside `AppWindow` touches the input statics, and nothing has to for this.

**Two focus behaviours will silently eat injected input if ignored.** `Pointer` copies
`InputState.WindowFocused` (`KhaozEngine.Windowing/Pointer.cs:91`) and `GuiSurface` refuses hover and
press while unfocused (`KhaozEngine.Gui/GuiSurface.cs:382` and `:404`), so a click injected into a
window the OS considers background is dropped by the GUI. Separately the background throttle drops an
unfocused window to a low frame cap and suppresses render and present entirely while minimized
(`KhaozEngine.Windowing/AppWindow.Frames.cs:62`, policy in
`KhaozEngine.Windowing/BackgroundThrottlePolicy.cs`). Both are correct for a game and both are wrong
for an automation run.

**A frame-bounded run already exists.** `KE_MAX_FRAMES` is read once at window construction
(`KhaozEngine.Windowing/AppWindow.cs:443`) and closes the window when the count is reached
(`KhaozEngine.Windowing/AppWindow.Frames.cs:85`). It is the right shape for a smoke test and the wrong
shape for a session an agent steps interactively, because it ends the process rather than yielding
control. An automation run must not set it.

**A background thread already hands work to the window thread.** The single-instance guard sets a
volatile flag from a listener thread and the frame callback consumes it on the window thread
(`KhaozEngine.Game/GameApp.cs:530` to `:534`), with the reason written down: the OS call is not
thread-safe off the window thread. A command queue drained at the frame boundary is the same pattern.

**A windowed backbuffer readback does not exist, and the seam to add one is missing in two places.**
This is the load-bearing finding, so it is stated in full:

- `IGpuFramebuffer` exposes `Outputs`, `Width` and `Height` and nothing else
  (`KhaozEngine.Gpu/GpuInterfaces.cs:84`). `IGpuDevice.SwapchainFramebuffer` hands back one of those
  (`:416`). There is no way to get at the swapchain's colour texture through the seam, so
  `GpuReadback.ToRgba` (`KhaozEngine.Gpu/GpuReadback.cs:40`), which needs an `IGpuTexture`, cannot be
  pointed at it.
- On Metal the drawable is configured `framebufferOnly(true)`
  (`KhaozEngine.Gpu.Metal/Internal/MetalSwapchainApi.cs:74`), which by the engine's own note means "a
  drawable's texture is an attachment and never a sampling or copy source"
  (`KhaozEngine.Gpu.Metal/Internal/ObjC/CAMetalLayer.cs:136`). A GPU test asserts it is true
  (`KhaozEngine.Render.Tests/Gpu/MetalSwapchainGpuTests.cs:111`), and the configure order it sits in is
  a pinned contract row (M-W1). So even with a texture accessor, a copy off the presented drawable
  would fail on the engine's primary desktop backend.

What DOES exist is readback off an OFFSCREEN target: `Render3DSnapshot.Capture`
(`KhaozEngine.Render3D/Render3DSnapshot.cs:59`) and `Render3DPreview.ReadbackRgba`
(`KhaozEngine.Render3D/Render3DPreview.cs:135`), both through `GpuReadback`, with
`KhaozEngine.Imaging/PngWriter.cs:13` to encode. `Render3DSnapshot` builds its own headless device and
its own scene, so it captures a scene that resembles the game rather than the frame the player is
looking at. `GpuFrameCapture` (`KhaozEngine.Gpu/GpuFrameCapture.cs:20`) is not a screenshot at all: it
arms an Xcode `.gputrace`. Its SHAPE is the useful part, and section 5 borrows it.

**The engine already ships stdio MCP servers, in C#.** `ke-tileedit` and `ke-mapedit` are
`PackAsTool` projects on the `ModelContextProtocol` package pinned at `1.4.1`
(`Directory.Packages.props:89`), hosted over stdio with logging forced to stderr
(`KhaozEngine.TileEdit.Tool/Program.cs`). Grimhollow already consumes one through
`.config/dotnet-tools.json` and registers it in `.mcp.json`.

**Grimhollow already gates a dev-only subsystem exactly the way this needs.** The Desktop head's
reference to `Grimhollow.Server` is `Condition="'$(Configuration)' == 'Debug'"`
(`Grimhollow.Desktop/Grimhollow.Desktop.csproj:22`), with the reason written in the csproj: a Release
client must not carry the server, the world store or `Microsoft.Data.SqlClient`, and that reference is
the only thing that would put them there. The `--with-server` flag it enables is inside `#if DEBUG`
(`Grimhollow.Desktop/Program.cs:61`), and everything a TEST asserts on lives in
`Grimhollow.Core.Config.DevToggles`, which compiles in every configuration because CI runs the suite in
Release. That is the precedent this design copies rather than inventing a new gate.

**The vendored feed picks up new packages for free.** `scripts/refresh-engine.sh:66` globs
`KhaozEngine.*.$VERSION.nupkg` out of the engine's `local-feed` into `vendor/khaozengine`, which
`nuget.config` exposes as the `khaoz-vendored` source. A new engine package needs no script change.

**The headless harness is real and good at what it does.** `Grimhollow.Tests/Client/LoopbackHarness.cs`
stands a whole server and a whole client in one process over the in-memory transport pair, with the
client's command clock deliberately phase offset from the server's. No sockets, no threads, no device.

## Alternatives

Criteria scored 1 to 10, higher is better. "Engine intrusion" and "maintenance" are scored as
cheapness, so 10 means it costs nothing. "Agent usefulness" is see plus act plus assert.

| Option | Player safety | Agent usefulness | Engine intrusion | Maintenance | Time to value | Total |
|---|---|---|---|---|---|---|
| 1. Screen-level automation (`screencapture` plus `osascript`) | 10 | 3 | 10 | 3 | 9 | 35 |
| 2. The headless loopback harness | 10 | 4 | 10 | 8 | 10 | 42 |
| 3. In-process automation endpoint plus MCP bridge | 8 | 9 | 4 | 6 | 3 | 30 |

Weighting player safety and agent usefulness at 3 each and the cost columns at 1 each (safety is a
constraint, usefulness is the entire point) gives 61, 70 and 64.

**The table ranks option 2 first and the recommendation is option 3 anyway, so the disagreement is the
part worth reading.** A weighted sum is the wrong instrument here, because two of the three columns it
adds up are not tradeable. The requirement is that an agent can SEE the running client and ASSERT on
what it finds. Option 1 sees and cannot assert. Option 2 asserts and cannot see, and no amount of
polish moves either one, because in each case the missing half is absent by construction rather than
unfinished. The loopback harness never creates a device, never runs the GUI, and never boots the real
head, so it cannot answer "does this content look right", which is the question that was asked. Option
3 is the only row that clears the requirement at all, and what the table is actually good for is
pricing that: the cost is concentrated in engine intrusion and time to first value, both of which
section 6 stages.

Neither loser is discarded. Option 1 becomes R1's screenshot stopgap, since it works today and needs no
engine change. Option 2 stays exactly where it is, as the assertion backstop for logic that does not
need pixels, and anything the loopback harness can already prove should keep being proved there rather
than through a booted client.

Scoring notes, so the numbers can be argued with:

- Option 1 maintenance is 3 because it is coordinate-driven and focus-dependent. Every UI move breaks
  it silently, it is macOS only, and it needs the window id.
- Option 2 usefulness is 4 rather than 1 because it genuinely covers a lot: server rules, movement,
  combat and persistence all have real coverage there.
- Option 3 player safety is 8 rather than 10 because it is the only option that puts any of the
  mechanism near the shipping client. The residual risk is a build misconfiguration, not a
  player-reachable surface, and section 3 is about making that risk mechanical rather than a matter of
  discipline.

## Recommendation, and the three gates

A new opt-in engine package `KhaozEngine.Automation`, in no umbrella, referenced only by a game's
Desktop head and only in Debug, plus an MCP bridge in the game repo.

Three gates, ALL required at once. Each one alone is enough to stop a player, and they are stacked
because they fail differently:

**Gate 1, compile time.** The Desktop head's reference is
`Condition="'$(Configuration)' == 'Debug'"`, so a Release binary contains no automation code, no
listener, and no reference to the package. This was verified empirically rather than assumed, because
the interaction between a configuration condition and NuGet restore is not obvious: a probe project
with a Debug-only `PackageReference` against Grimhollow's vendored feed restores clean in both
configurations, and its Release `deps.json` carries zero references to the package while its Debug one
carries the assembly. Restore is re-run per configuration and rewrites `project.assets.json` each time,
which is invisible and correct. This is the gate that answers the owner's constraint, and it is the
same mechanism the head already uses for `Grimhollow.Server`.

**Gate 2, run time.** Even in a Debug build the endpoint stays dark until the head asks for it, via an
explicit opt-in on the automation host plus `KE_AUTOMATION=1`. This gate exists for the developer, not
the player: it stops a normal Debug playtest from opening a socket nobody asked for.

**Gate 3, transport.** Bind loopback only, on an ephemeral port, with a per-run random token. The port
and the token are written together to one handshake file under the app data directory, and every
request carries the token. Be honest about what this gate is for: it is not what protects players
(gate 1 is), and a loopback bind is reachable by every process on the developer's machine. The token
raises the bar from "any local process can drive the client" to "any local process that can also read
the developer's app data directory", which is worth the fifteen lines it costs.

## Protocol and frame semantics

JSON lines over loopback TCP. One JSON object per line, one reply per request. No HTTP stack, no
framing library, no dependency beyond the BCL, because the whole point of the transport is that it is
too small to have bugs of its own.

Every command is QUEUED by the listener thread and APPLIED on the window thread at the frame boundary,
following the single-instance precedent at `KhaozEngine.Game/GameApp.cs:530`. Every reply names the
frame index the command took effect on. That is what makes a session reproducible: a report can say
"the panel was still open at frame 412" rather than "it seemed to still be open".

| Command | Effect |
|---|---|
| `input` | Pointer position in window pixels, button press or release, key press or release. Optional `holdFrames` to keep it held. |
| `step` | Run N frames, then reply. The pure-advance case of the above. |
| `screenshot` | Write the presented frame to a PNG at a path (see section 5 for what this costs). |
| `state` | The game's registered state provider returns a JSON document. |
| `call` | A game-registered named verb with JSON args. |
| `quit` | Close the window and end the process cleanly. |

`input` carrying `holdFrames` overlaps `step`, deliberately. The common agent action is "press here and
let three frames run", and forcing that into two round trips doubles the latency of the most frequent
operation for no gain.

Positions are in WINDOW PIXELS, matching `InputState.MousePosition`, which `AppWindow` has already
scaled from Silk's logical points into framebuffer space (`KhaozEngine.Windowing/AppWindow.cs:724` and
`:726`). Anything higher level than a pixel (a tile, a widget, an inventory slot) is a game `call` verb,
because the projection from world to screen belongs to the game and the engine has no business
guessing it.

**The engine seam is one composed line.** An `AutomationHost`, constructed by the game head alongside
its `GameApp`, holds the queue and the pending injected state. `AppWindow` gains an optional snapshot
filter applied immediately after `BuildInput()`, so `_frame.Input` is already the composed snapshot and
every consumer downstream (both pointers, the GUI, the HUD, the game) sees one coherent frame. The
filter unions the injected key and button sets into the real ones, overrides the pointer position when
one is injected, and forces `WindowFocused` true for the composed frame, without which the GUI drops
every injected click (`KhaozEngine.Gui/GuiSurface.cs:382`). It never reaches the accumulator and never
touches a Silk static, so the rule that `AppWindow` is the only class in the engine near those statics
is preserved exactly.

An automation run also sets `GameAppOptions.BackgroundThrottle` to
`BackgroundThrottlePolicy.Disabled`, or the loop crawls the moment the agent's terminal takes focus.

## The screenshot problem

Section 2 established there is no windowed readback. There are three honest ways out, and the round
plan (section 6) uses two of them in sequence.

**A. Present-boundary capture in the GPU seam.** A `GpuPresentCapture` static in `KhaozEngine.Gpu`
mirroring `GpuFrameCapture` exactly: arm with a path, each backend consumes the arm at its own present
boundary with its own device pointers in hand. Metal already does precisely this for the Xcode trace
(`KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.Capture.cs:67`), so the shape is precedented rather than
invented, and it keeps the swapchain texture private to the backend, which is why no accessor has to be
added to `IGpuFramebuffer`. The cost is real and should not be understated: three backends, three
implementations, three golden legs on `cross-platform-gpu.yml`. Metal carries one extra wrinkle. The
drawable must not be `framebufferOnly`, that flag is set when the layer is configured rather than per
frame, and the configure order is a pinned contract row with a test on it
(`KhaozEngine.Render.Tests/Gpu/MetalSwapchainGpuTests.cs:111`). So the flag is decided once at boot
from a process-wide "presentable must be copyable" opt-in that only an automation run sets, and the
shipped default does not move a byte. This is the recommended end state.

**B. Render to an offscreen target and blit.** `Render3DPreview`'s shape
(`KhaozEngine.Render3D/Render3DPreview.cs:135`) applied to the whole frame. No swapchain change at all,
and it works on every backend the day it lands. Rejected as the primary because it changes what the
game renders in order to observe it: every automation frame pays a full-screen blit, and the captured
image is one composite away from what the player sees, which is exactly the gap a content review is
supposed to close. Kept as the fallback if A turns out to be worse than sized.

**C. Screen-level capture, kept.** macOS `screencapture -l <window id>`. It captures precisely what the
compositor shows, chrome and colour management included, needs no engine change, and works the day the
endpoint has a window id to report. macOS only, and it cannot capture an occluded or offscreen window.
This is R1's screenshot, and it may well survive longer than planned, since it is the only option that
photographs the real thing rather than reconstructing it.

## The game side

Grimhollow registers a state provider and a verb table with the automation host. The engine defines the
seam and knows nothing about tiles, inventories or panels.

State provider, returning one JSON document: player tile and plane, hitpoints, current combat target,
inventory contents, which panels are open, the hover line's text, and visible remotes with tile and
kind. Every one of those is a thing a content review needs to assert on and none of them can be read
off a screenshot reliably.

Verbs: `click_tile`, `right_click_tile`, `menu_pick`, `walk_to`, `open_panel`. `click_tile` is the
important one and it is the reason `call` exists at all: projecting a tile to a screen pixel needs the
live camera, and only the game has it. An agent asking for a tile and getting back the frame the click
landed on is a completely different tool from an agent guessing pixel coordinates.

The MCP bridge lives in `tools/playtest-mcp/` in the Grimhollow repo, registered in the repo's
`.mcp.json` beside `ke-tileedit`. Its tools map onto the endpoint one for one, plus `boot` (start the
client with `--with-server` and the gates set, wait for the handshake file, connect) and `shutdown`.

**C#, not Python.** The engine already ships two stdio MCP servers on the `ModelContextProtocol`
package (`KhaozEngine.TileEdit.Tool`, `KhaozEngine.MapEdit.Tool`), Grimhollow already registers one in
`.mcp.json` and pins one in `.config/dotnet-tools.json`, and the repo is a .NET repo whose contributors
and CI already have the toolchain. A Python server would add a second language, a second dependency
manager and a second packaging story to save nothing, and it could not share the game's own DTOs, which
is the one real technical advantage on the table: the bridge and the endpoint can compile against the
same request and reply types, so a protocol change is a compile error rather than a runtime surprise.

## Rounds

**R1. The endpoint, minus pixels.** `KhaozEngine.Automation` with `input`, `step`, `state`, `call` and
`quit`, the three gates, the composed snapshot filter in `AppWindow`, the Grimhollow state provider,
and the MCP bridge. `screenshot` is served by option C above, so the round is useful end to end without
touching the GPU seam. Done means an agent can boot the client, walk somewhere, read where it ended up,
and take a picture of it.

**R2. Present-boundary capture.** `GpuPresentCapture` and its three backend implementations, the Metal
copyable-presentable opt-in, and PNG output through `KhaozEngine.Imaging`. Done means `screenshot`
needs no `osascript`, and works on Windows and Linux.

**R3. Game verbs and menu reading.** The full verb table, the right-click menu as readable state, and
tile-to-pixel projection. Done means an agent can act on content rather than on coordinates.

**R4. Assertion helpers.** `wait_until` over a state predicate with a frame budget, so an agent stops
polling in a loop and a timeout is a reportable fact.

## Owner decisions to make before R1

1. **Package name.** `KhaozEngine.Automation` is proposed. It is accurate and it is also the word a
   reader is most likely to misread as something a player might use, so `KhaozEngine.DevHarness` or
   `KhaozEngine.Playtest` are worth a moment.
2. **Is the Debug-only reference enough, or is an `#if` guard wanted as well?** The reference gate was
   verified to work and it matches the head's existing precedent. A belt-and-braces `#if DEBUG` around
   the host construction costs nothing and makes the guarantee legible in the source rather than only in
   the csproj. The argument against is that it puts the same rule in two places.
3. **Handshake file location.** Under the app data directory is proposed, alongside the existing state
   directory `Grimhollow.Core` already owns. The alternative is the system temp directory, which is
   easier for a bridge to find and easier for another local process to find.
4. **Where the bridge lives.** `Grimhollow/tools/playtest-mcp/` now, or `game-template` so every game
   gets one. Proposed answer is Grimhollow first and promote once there is a second consumer, because
   the verb table is the part that will move most and it is entirely game-specific. The engine half is
   already the shared piece.

## Risks

- **The gate is a build setting, and build settings drift.** The mitigation is a test in the game repo
  asserting the Release output carries no automation assembly, run in the same CI leg that already runs
  the suite in Release.
- **R2 is three backends and three golden legs.** If it prices out badly, option C is not a stopgap but
  the answer, and R2 is closed as not planned with the measurement written down.
- **A stepped session is not the same as a played session.** Frame pacing, held input and focus all
  behave differently under automation, so a bug that only appears at real frame cadence can hide from
  it. This tool reduces the number of human playtests, it does not end them.
