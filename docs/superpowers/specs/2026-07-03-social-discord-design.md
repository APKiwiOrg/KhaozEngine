# KhaozEngine.Social + KhaozEngine.Social.Discord - design

Date: 2026-07-03
Status: approved, ready to plan
Target engine version: 9.9.0 (additive minor; re-check for a concurrent bump at release time)

## Goal

Centralize Discord integration into the engine so games stop shipping bespoke Discord code.
Deliver a provider-neutral social seam plus a pure-managed Discord Rich Presence backend, then
retire the per-game implementations (only SpaceGame has one today).

This implements ROADMAP near-term item #1 (`Engine-level Discord social SDK`), with one deliberate
deviation from that item's wording: the roadmap assumed the **native** Discord Social SDK (C++,
per-RID native libraries). That is the wrong call for 2026 (no production-grade .NET binding, native
per-RID shipping, Linux experimental, comms gated behind Discord approval), and it breaks the
engine's MonoGame-free / managed-only posture. Instead the Discord backend is a **pure-managed Rich
Presence IPC client** (zero native, zero third-party). Friends/lobbies/voice (which genuinely need
the native SDK) are explicitly out of scope and can be added later behind the same seam as a separate
opt-in `.Native` backend if a game ever needs them.

## Why IPC, not the C++ SDK

Discord Rich Presence does not require Discord's C++ SDK. The Discord desktop app listens on a local
socket: a named pipe `\\.\pipe\discord-ipc-0` on Windows, a unix domain socket `discord-ipc-0` in the
temp dir on macOS/Linux (indices 0..9). Any process can connect and speak Discord's IPC protocol:

- Binary framing: 4-byte little-endian opcode + 4-byte little-endian length + UTF-8 JSON body.
- Opcodes: `Handshake=0`, `Frame=1`, `Close=2`, `Ping=3`, `Pong=4`.
- Handshake payload `{ "v": 1, "client_id": "<appId>" }`, server replies with a `READY` dispatch
  carrying the local user.
- `SET_ACTIVITY` command frame publishes presence; `SUBSCRIBE` + dispatch frames drive
  `ACTIVITY_JOIN` and `ACTIVITY_JOIN_REQUEST`.

.NET covers all of this with the BCL: `System.IO.Pipes.NamedPipeClientStream` transparently maps to
the Windows named pipe and (on modern .NET) the Unix domain socket, and `System.Text.Json` handles
the payloads. The whole transport is a few hundred lines of managed C# with nothing to ship per-RID.
This is the same approach Lachee's `DiscordRichPresence` library takes internally; we own it so the
engine stays on System.Text.Json with no third-party dependency (the engine tolerates Newtonsoft only
as a forced transitive CVE-override under `KhaozEngine.Gpu`, never as a deliberate Foundation dep).

## Package layout

Two new packages, mirroring the `Physics` / `Physics.Bepu` seam+opt-in-backend split.

| Package | Umbrella | Depends on | Role |
|---|---|---|---|
| `KhaozEngine.Social` | Foundation | `KhaozEngine.Diagnostics` | Provider-neutral seam + value types + `NullSocialProvider` no-op + `SocialPresenceController` orchestration. Pure BCL, headless-testable. |
| `KhaozEngine.Social.Discord` | none (opt-in) | `KhaozEngine.Social` | Pure-managed Discord IPC backend (`DiscordSocialProvider` + internal `DiscordIpcClient`). Zero native, zero third-party (System.Text.Json only). Added explicitly by client heads, like `Physics.Bepu`. |

Rationale for keeping the split even though the backend has no native/heavy dep to isolate:
- Umbrella hygiene: a game using Steam (or no social) should not pull Discord code. Foundation stays
  Discord-free; the backend is opt-in and referenced by id.
- Provider neutrality: the seam names none of Discord's concepts, so a later `.Steam` / `.Native`
  backend slots in beside `.Discord` without touching game code (the user's explicit "Discord may be
  on the way out one day" requirement).
- Consistency: identical shape to `Physics`/`Physics.Bepu` and `WorldStore`/`WorldStore.Sqlite`.

## KhaozEngine.Social (the seam)

### `ISocialProvider : IDisposable`
Provider-neutral contract. Every method is best-effort and must never throw into the caller.

```csharp
public interface ISocialProvider : IDisposable
{
    bool IsConnected { get; }

    // Connect to the platform for the given application/client id. Returns false on any failure.
    bool TryInitialize(string applicationId);

    // Pump platform callbacks; call once per frame on the main thread.
    void Update();

    // Publish / clear the local player's rich presence.
    void SetPresence(in RichPresence presence);
    void ClearPresence();

    // Local platform identity (e.g. the Discord username), once connected.
    bool TryGetLocalUser(out SocialUser user);

    // Medium tier - join / invite.
    // Raised when a friend clicks "Join" on the local player's activity; carries the join secret
    // the game's own netcode encoded into the presence. The engine cannot resolve it - the game does.
    event Action<string> JoinRequested;
    // Raised when another user asks to join (ask-to-join). The game accepts or rejects.
    event Action<JoinRequest> JoinRequestReceived;
}
```

### Value types (provider-neutral, not Discord-shaped)

```csharp
public readonly record struct RichPresence
{
    public string? Details { get; init; }        // top line
    public string? State { get; init; }           // second line
    public DateTime? StartTimestampUtc { get; init; }  // renders an elapsed timer
    public DateTime? EndTimestampUtc { get; init; }    // renders a countdown
    public PresenceImage LargeImage { get; init; }
    public PresenceImage SmallImage { get; init; }
    public PresenceParty Party { get; init; }
    public string? JoinSecret { get; init; }      // enables "Join Game" on the profile
    public string? SpectateSecret { get; init; }
    public IReadOnlyList<PresenceButton>? Buttons { get; init; } // up to 2
}

public readonly record struct PresenceImage(string? Key, string? Text);
public readonly record struct PresenceParty(string? Id, int Size, int Max);
public readonly record struct PresenceButton(string Label, string Url);

public readonly record struct SocialUser(string Id, string Username, string? GlobalName);

public sealed class JoinRequest
{
    public SocialUser User { get; }
    public void Accept();
    public void Reject();
}
```

Only `Details`, `State`, and `StartTimestampUtc` are exercised by SpaceGame today. The rest are
included so the shape is future-proof (SpaceGame MP / Hardpoint / Ruinborne want images + party +
join). Empty/default fields are simply omitted from the wire payload.

### `NullSocialProvider`
No-op default for headless servers, CI, tests, and any platform without Discord. `IsConnected` is
always false, every method is a silent no-op, `TryInitialize` returns false, `TryGetLocalUser`
returns false, events never fire. Never throws. This is what a game gets if it does not add the
Discord backend.

### `SocialPresenceController`
The generic orchestration lifted out of SpaceGame's `DiscordRichPresenceService` (which is ~90%
game-agnostic), made provider-neutral so every backend inherits it for free. Constructed over any
`ISocialProvider` (defaults to `NullSocialProvider` when none supplied). Owns:

- Lazy init on first use; `Initialize()` also callable explicitly at load.
- Throttle + republish intervals (configurable, default 15s), so a per-frame `SetPresence` call from
  gameplay does not spam the socket.
- Dedupe by presence content (only re-send when the presence actually changed).
- **Session self-disable on any error**: a throw from the provider permanently disables social for
  the session and disposes the provider, so a Discord failure never propagates into the game loop.
- `Update()` pass-through (pump) guarded by the same disable logic.
- `TryGetLocalUser` pass-through.
- An elapsed-timer helper (`DateTime.UtcNow - elapsed` -> `StartTimestampUtc`) so games express "in a
  run for MM:SS" without computing timestamps.

Public surface (game-facing):
```csharp
public sealed class SocialPresenceController : IDisposable
{
    public SocialPresenceController(ISocialProvider? provider = null, SocialPresenceOptions? options = null);
    public bool IsEnabled { get; }
    public void Initialize();
    public void SetPresence(in RichPresence presence, bool force = false);
    public void SetElapsedPresence(in RichPresence presence, TimeSpan elapsed, bool force = false);
    public void ClearPresence();
    public void Update();
    public bool TryGetLocalUser(out SocialUser user);
    public event Action<string> JoinRequested;              // forwarded from provider
    public event Action<JoinRequest> JoinRequestReceived;   // forwarded from provider
    public void Dispose();
}
```

Games keep only: their app id, their copy strings, and their mode -> `RichPresence` mapping.
SpaceGame's `SetMenuPresence` / `SetSpaceForgePresence` / `SetRunPresence` verbs stay in SpaceGame as
thin wrappers over `SetPresence` / `SetElapsedPresence`.

## KhaozEngine.Social.Discord (the backend)

- `DiscordSocialProvider : ISocialProvider` - the real implementation. Owns a `DiscordIpcClient`,
  maps `RichPresence` -> the Discord `SET_ACTIVITY` payload, subscribes to `ACTIVITY_JOIN` /
  `ACTIVITY_JOIN_REQUEST`, captures the local user from `READY`, and forwards join events. Every
  entry point is wrapped so a transport failure degrades to disconnected and never throws.
- `DiscordIpcClient` (internal sealed) - the pure-managed transport:
  - Socket discovery: enumerate `discord-ipc-0..9`. Windows -> `\\.\pipe\discord-ipc-N`. Unix ->
    `$XDG_RUNTIME_DIR`, `$TMPDIR`, `$TMP`, `$TEMP`, `/tmp`, including Flatpak
    (`app/com.discordapp.Discord/`), Snap (`snap.discord/`) and Vesktop sandbox subdirs.
  - Framing codec: read/write the 8-byte header + JSON body over the stream. This is the pure,
    deterministic core - fully unit-testable without a live Discord.
  - Handshake, `SET_ACTIVITY`, `SUBSCRIBE`, dispatch parsing, `Ping`/`Pong`, graceful reconnect.
- `DiscordSocialOptions` - `{ string ApplicationId; TimeSpan? RepublishInterval; ... }`.

Nothing here references any third-party package. Nothing is native. `System.IO.Pipes` +
`System.Text.Json` only.

## Data flow

```
game screen/controller
      | SetPresence / SetElapsedPresence (high level, per frame, cheap)
      v
SocialPresenceController   (throttle, dedupe, session-disable, elapsed helper)   [Foundation]
      | SetPresence(in RichPresence)  (only when changed / interval elapsed)
      v
ISocialProvider
   |- NullSocialProvider  -> no-op                                               [Foundation]
   `- DiscordSocialProvider -> DiscordIpcClient -> local Discord socket          [opt-in .Discord]
                                    ^ Update() pumps dispatches; READY -> local user;
                                      ACTIVITY_JOIN -> JoinRequested(secret);
                                      ACTIVITY_JOIN_REQUEST -> JoinRequestReceived(JoinRequest)
```

## Error handling

- No Discord running / socket absent: `TryInitialize` returns false, controller stays disabled,
  everything no-ops. No log spam beyond one debug line.
- Mid-session transport failure: provider catches, marks itself disconnected; controller's
  session-disable trips and disposes the provider. Game unaffected.
- All Discord problems are contained inside the backend and the controller. The game loop never sees
  a Discord exception. Debug-only logging via `KhaozEngine.Diagnostics` (not `Console.WriteLine`).

## Testing (KhaozEngine.Tests/Social/, headless, no live Discord)

- `NullSocialProvider` is silent and never throws; `IsConnected == false`.
- `SocialPresenceController`: throttle (no re-send within interval), republish (re-send after
  interval), dedupe (identical presence not re-sent), force override, elapsed-timer computation,
  session-disable on a provider that throws - all driven against a `FakeSocialProvider` double that
  records calls (the `FakeMusicBackend` / `FakeUpdaterEnvironment` pattern).
- Discord IPC codec: round-trip a frame (opcode+length+body), build a `SET_ACTIVITY` body from a
  fully-populated `RichPresence` and assert the JSON, parse a `READY` dispatch into a `SocialUser`,
  parse an `ACTIVITY_JOIN` dispatch into a secret. Pure and deterministic.
- Socket-path discovery: given a fake environment (injected path/env lookups), assert the candidate
  order across Windows and unix layouts incl sandbox subdirs.
- One live-socket smoke test tagged `[Trait("Category","LiveSocket")]`, excluded by CI's
  `--filter "Category!=LiveSocket"`.

The `DiscordIpcClient` is written so its side-effecting IO (opening the stream, env lookups) is behind
a tiny injectable seam, keeping the codec + discovery + provider logic headless-testable, mirroring
`Updates`' `IUpdaterEnvironment` discipline.

## Packaging + release (one additive minor bump)

- New dirs: `KhaozEngine.Social/` (csproj + README + sources), `KhaozEngine.Social.Discord/` (csproj
  + README + sources).
- `KhaozEngine.slnx`: add both projects.
- `Directory.Packages.props`: nothing to add (no third-party dep). CPM unaffected.
- `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`: add
  `<ProjectReference Include="../KhaozEngine.Social/KhaozEngine.Social.csproj" />` and extend the
  umbrella `<Description>` package inventory to include `Social`. `.Social.Discord` goes in NO
  umbrella.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj`: add ProjectReferences to both new packages.
- Version: `Directory.Build.props` `<KhaozEngineVersion>` 9.8.0 -> 9.9.0 (once, at end of batch).
  Re-check `origin/main` + tags for a concurrent bump first and take the next FREE version if 9.9.0
  is taken.
- Docs (full sweep, single source each):
  - `CHANGELOG.md`: newest-first 9.9.0 entry, one-line digest first sentence, then detailed bullets
    for the new public API. Same commit as the version bump.
  - `KhaozEngine.Social/README.md` and `KhaozEngine.Social.Discord/README.md`: the per-package
    `PackageReadmeFile` (required by the check-doc-versions package-inventory guard).
  - `README.md`: add a bolded `KhaozEngine.Social` catalog row and a bolded
    `KhaozEngine.Social.Discord` row (What / Depends-on, note Discord is opt-in / no umbrella like
    Physics.Bepu); add `Social` to the Foundation umbrella "Pulls in" list; add both dirs to the repo
    layout block.
  - `docs/DEPENDENCY-SEAMS.md`: add a Social/presence row to the seam table and the
    seam->contract->backend file-path table.
  - `docs/USING-KHAOZENGINE.md`: add a "Social / Discord presence" section with wiring snippets,
    modeled on the "3D physics" section (seam in Foundation, backend opt-in added explicitly).
  - `docs/CONSUMERS.md`: bump the "Engine current version" line (guard-checked); note
    `Social.Discord` in the opt-in-packages prose alongside Physics.Bepu / WorldStore.* / Server.Admin.
  - `docs/ROADMAP.md`: bump the "Current released version" line (guard-checked); once shipped, DELETE
    near-term item #1 (ROADMAP records future work only).
  - `README.md` `<PackageReference>` example version (guard-checked, third of the three declarations).
  - `CLAUDE.md`: add `Social.Discord` to the "Opt-in, in NO umbrella, added explicitly" list. Do NOT
    re-enumerate the packages (CLAUDE.md points at the README catalog).
- `scripts/check-doc-versions.sh` must pass: the three version declarations equal 9.9.0, and both new
  packable projects have a bolded README catalog row + ship their own README.md.
- `dotnet pack -c Release -o ./local-feed` (from the main repo root at release time, not the worktree
  local-feed which is deleted on worktree removal).
- Commit, `git tag v9.9.0`. HOLD the push + tag; batch and confirm with the user before pushing
  (engine heavy-CI policy: CI publishes to GitHub Packages on every `v*`).

## Consumer migration (delivered as handoff prompts, not done in this session)

Engine-first: the package ships first, each consumer adopts in its own repo/chat afterward. Handoff
prompts to produce at the end:

- SpaceGame (has an impl to migrate): pin engine 9.9.0; add `KhaozEngine.Social` (Core) +
  `KhaozEngine.Social.Discord` (Desktop head); replace `IDiscordRichPresenceBackend` /
  `DiscordRichPresenceService` / `LacheeDiscordRichPresenceBackend` with `SocialPresenceController` +
  `DiscordSocialProvider`; keep the app id, copy strings, and Menu/SpaceForge/Run -> `RichPresence`
  mapping game-side; keep `TryGetLocalUser` for the leaderboard name; drop the `DiscordRichPresence`
  NuGet. Net deletion of ~2 files + the Newtonsoft-based dependency.
- Hardpoint / Ruinborne / Nullwake (no impl): optional adoption prompts. Emphasize presence + join
  for the multiplayer ones (Hardpoint, Ruinborne); each is a per-game Discord Application id + a
  mode -> presence mapping over `SocialPresenceController`, plus the Discord backend on the client head.

## Out of scope (deferred, same seam later if needed)

- Native Discord Social SDK (friends list, lobbies, in-game voice, linked channels, provisional
  accounts). Needs the C++ native SDK + per-RID binaries + a .NET binding that does not yet exist at
  production grade. If ever required, add `KhaozEngine.Social.Discord.Native` as a separate opt-in
  backend behind the same `ISocialProvider`.
- Non-Discord providers (Steam, Epic, GOG). The seam is designed to host them; none built now.
- Achievements. The roadmap listed them as "optionally"; not in this scope. The seam can grow an
  `IAchievementProvider` sibling later without disturbing presence.
```
