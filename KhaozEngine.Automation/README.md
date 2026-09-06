# KhaozEngine.Automation

The dev-only playtest endpoint. An agent boots the client, clicks in it, reads its state, and reports back, over a
loopback socket that a shipping build does not contain.

Opt-in, in NO umbrella, and referenced ONLY from a game's Desktop head, ONLY in Debug. A player must never be able
to automate gameplay with any of it, which is a constraint rather than a preference, so this package is designed to
be ABSENT from a release binary rather than merely switched off in one.

## The three gates

All three are required at once. Each one alone is enough to stop a player, and they are stacked because they fail
differently.

**Gate 1, compile time.** The head's reference carries the configuration condition, so a Release binary contains no
automation code, no listener and no reference to the package:

```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="KhaozEngine.Automation" Version="18.13.0" />
</ItemGroup>
```

Restore runs per configuration and rewrites `project.assets.json` each time, so the Release `deps.json` carries zero
references to the package while the Debug one carries the assembly. This is the gate that answers the constraint.

**Gate 2, run time.** Even in a Debug build the endpoint stays dark until the head asks for it: `AutomationOptions`
must carry `Enabled: true` AND the environment must carry `KE_AUTOMATION=1`. This gate is for the developer, not the
player. It stops an ordinary Debug playtest from opening a socket nobody asked for. When it refuses there is no
thread, no socket and no handshake file.

**Gate 3, transport.** Loopback only, an ephemeral port, a per-run random token (256 bits, base64url). The port and
the token go into one handshake file, `automation.json`, under the directory the options name, owner-only where the
platform allows, deleted on dispose and again on process exit:

```json
{"port":51234,"token":"...","pid":4711,"startedAt":"2026-09-02T04:12:07.1234567+00:00"}
```

**A bridge checks the `pid` is alive before it trusts the rest of the file.** A hard crash leaves the file behind,
naming a port that is gone or, worse, one something else has since been given, so treat a file whose pid is dead as
absent and wait for the next one.

Be honest about what gate 3 is for. It is not what protects players (gate 1 is), and a loopback bind is reachable by
every process on the developer's machine. The token raises the bar from "any local process can drive the client" to
"any local process that can also read the developer's app data directory".

Two bounds sit AHEAD of the token check, because the token travels inside the request line and so everything up to
it is reachable by any local process that can connect:

- A request line past `AutomationHost.MaxRequestLineBytes` (64 KiB) gets one error and the connection closes. The
  reader stops copying at the cap, so a caller cannot grow the host's heap by writing a line that never ends.
- A connection has `AutomationOptions.FirstLineTimeout` (5 seconds) to deliver its first complete line, which is the
  one that must carry the token, and `AutomationOptions.IdleTimeout` (60 seconds) between lines after that. Both are
  settable, and zero or less means no deadline.

## Wiring it up

```csharp
protected override void OnLoad()
{
    _automation = new AutomationHost(Window, new AutomationOptions(Enabled: true, StateDirectory)
    {
        Log = (message, error) => Log.Warn(message, error),   // silent by default, which suits a test and not a head
    });
    _automation.StateProvider = DescribeState;          // Func<JsonNode?>, run on the window thread
    _automation.Register("click_tile", ClickTile);      // Func<JsonElement, JsonNode?>, same
    _automation.Start();                                // the gates decide whether anything happens
}

protected override void OnUnload() => _automation?.Dispose();   // or hold it in a using, or let ProcessExit do it
```

Register everything before `Start`, so no request can arrive against a half-built verb table. A running host wires
three things on the window and nothing else: the input filter, the background throttle (to
`BackgroundThrottlePolicy.Disabled`, or the loop crawls the moment the agent's terminal takes focus, restored to
whatever it found on dispose) and `AppWindow.Close` as the default `quit` handler. It never sets `KE_MAX_FRAMES`,
which ends the process at a frame count rather than yielding control.

Dispose it, and the host also hooks `ProcessExit` so an ordinary `quit` to `AppWindow.Close` to `Run` returning
still removes the handshake file rather than leaving one that names a dead port. `Log` is where a loop or a
connection ending for a reason other than shutdown is reported: an accept loop that died (the endpoint is gone for
the run, and the bridge sees nothing but connection refused), a refused over-long line, an expired read deadline.
It takes a message and an optional exception, because half of what is worth reporting is a deliberate close rather
than a throw. It is called from a socket thread, so it must not block, and a throw from it is swallowed.

## The protocol

JSON lines over the loopback TCP socket. One request object per line, one reply object per line, `System.Text.Json`,
no HTTP stack and no framing library, because the whole point of the transport is that it is too small to have bugs
of its own.

Every request carries `cmd` and the `token`, plus an optional `id` echoed on the reply. Every reply carries that
`id`, the `frame` the command took effect on, and exactly one of `ok` or `error`:

```json
{"id":7,"token":"...","cmd":"input","x":640,"y":360,"button":"left","holdFrames":3}
{"id":7,"frame":412,"ok":{}}
{"id":8,"frame":412,"error":"unknown verb 'walk_to'"}
```

The frame number is the point. A report can say "the panel was still open at frame 412" rather than "it seemed to
still be open", which is what makes a stepped session reproducible.

Every command except `ping` is QUEUED on the socket thread and APPLIED on the window thread at the frame boundary,
following the cross-thread precedent the single-instance guard already sets. `ping` is answered on the socket thread
because it is the bridge's readiness check and has to work between the handshake file appearing and the first frame
running.

| Command | Arguments | Effect |
|---|---|---|
| `input` | `x` + `y` (window pixels, given together), `releasePointer`, `button` (`left`/`middle`/`right`/`x1`/`x2`), `key` (a `Key` name, case-insensitive, and `None` is refused), `action` (`press`, the default, or `release`), `holdFrames` | Move the pointer, press or release a button or a key. `holdFrames: N` auto-releases N frames later: the press is live on frame F through F+N-1 and the release edge lands on F+N. An `input` carrying no pointer, no `releasePointer`, no button and no key is refused, since it has nothing to apply. Replies `{}` on the frame it was applied. |
| `step` | `frames` (default 1) | Run that many frames, then reply. Counted inclusive of the frame it landed on, so `step 1` replies on that frame. |
| `state` | none | The game's registered state provider, invoked on the window thread. Its JSON document is the `ok` payload. An error when no provider is registered. |
| `call` | `name`, `args` (any JSON) | Run a game-registered verb on the window thread. Its return value is the `ok` payload. An unknown name is an error, and a verb that throws becomes an error reply rather than taking the frame loop down. |
| `quit` | none | Ask the window to close. |
| `ping` | none | A no-op returning the current frame number, for the readiness check. |

A malformed line gets an error reply and the connection STAYS OPEN, since the caller can recover from its own typo.
A wrong or missing token gets ONE refusal and then the connection CLOSES, so a guesser pays a reconnect per attempt.
A line past the 64 KiB cap is refused the same way, since what is left of it on the wire cannot be resynchronised
against a request. A bad argument fails at submit time with a precise message rather than a frame later.

Disposing the host fails every command still waiting on a frame with `automation host stopped`, and those replies go
out BEFORE the sockets close, so a client parked on `step 9999` reads the reason rather than an EOF it has to guess
at.

Positions are in WINDOW PIXELS, matching `InputState.MousePosition`, which `AppWindow` has already scaled out of
Silk's logical points. Anything higher level than a pixel (a tile, a widget, an inventory slot) is a game `call`
verb, because the projection from world to screen belongs to the game and the engine has no business guessing it.

## How injected input reaches the frame

The engine seam is one composed line. `AppWindow.InputFilter` is applied to the snapshot `BuildInput()` just built,
before the frame latches it, so every consumer downstream (both pointers, the GUI, the HUD, the game) sees one
coherent frame. The host installs `AutomationHost.Pump` there, which is the frame pump and the filter in the same
call.

The merge is a UNION, never a replacement, so a key the developer is holding stays held while automation clicks.
Two exceptions are deliberate:

- The pointer POSITION is overridden while an injected pointer is live, because two cursors cannot both be the
  cursor. `{"cmd":"input","releasePointer":true}` hands it back.
- `InputState.WindowFocused` is forced true, without which `GuiSurface` drops every injected press the moment the
  agent's terminal takes focus.

A press and a release of the same button applied in ONE pump is a click, and the frame carries both edges: pressed
and released, with the button not down. That is the shape `Pointer` already completes as a same-frame tap, so
sending press and release in one batch works and does not need a `step` between them. `holdFrames` is still the
idiom when the game has to see the button held.

Nothing here touches a Silk or GLFW static. `AppWindow` remains the only class in the engine near those, and
`InputState` is immutable, so a composed frame is a union of sets and a new instance.

## Public API

- `AutomationOptions(bool Enabled, string HandshakeDirectory)` with the init-only `Log`, `FirstLineTimeout`,
  `IdleTimeout` and `CommandTimeout`, plus `AutomationOptions.Off`. `CommandTimeout` defaults to 5 seconds and
  must be positive and within the runtime timer range of about 49.7 days. An expired queued command is removed
  before the window thread can apply it, and an expired `step` leaves the wait list. A synchronous state provider,
  verb, or quit callback already running at the deadline cannot be cancelled safely, so it finishes and returns
  its actual success or failure instead of a false timeout reply.
- `AutomationHost`: `Start`, `Pump`, `Submit`, `Register`, `Dispose`, `StateProvider`, `QuitRequested`, `Frame`,
  `IsRunning`, `Port`, `Token`, `HandshakeFilePath`, `EnvironmentAllows`, and the constants
  `EnvironmentVariable`, `EnabledValue`, `HandshakeFileName`, `MaxRequestLineBytes`.
- `AutomationRequest` / `AutomationReply`: the wire shapes, `TryParse` and `ToJsonLine`.
- `AutomationInputInjector`: the compose half, usable on its own.
- `AutomationHandshake`: `NewToken`, `TokenMatches`, `Serialize`, `Write`, `Delete`, `CurrentProcessId`.

`Pump`, `Submit` and `Register` work whether or not the host started, on purpose: they are the machine the gates
decide whether to WIRE UP, and keeping the gate at the wiring point is what makes the endpoint testable headlessly
with no socket and no window.

## Not in this package

No screenshot. There is no windowed backbuffer readback in the engine, and the seam is missing in two places, so a
picture is taken at screen level for now (macOS `screencapture -l <window id>`, which needs no engine change and
photographs what the compositor actually shows). The present-boundary capture that removes that dependency is a
later round.

No game verbs. The engine defines the seam and knows nothing about tiles, inventories or panels.
