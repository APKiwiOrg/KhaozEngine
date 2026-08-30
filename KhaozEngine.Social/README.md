# KhaozEngine.Social

Game-agnostic social/presence seam. `ISocialProvider` is the provider-neutral contract (Discord today,
Steam/other later) for rich presence, local identity, and join/invite. Depends only on
`KhaozEngine.Diagnostics`. The Discord backend is the opt-in
[KhaozEngine.Social.Discord](../KhaozEngine.Social.Discord) package (in no umbrella). In the
`Foundation` umbrella metapackage.

## Types

- **`ISocialProvider`** - `TryInitialize(appId)`, `IsConnected`, `Update()` (pump per frame),
  `SetPresence(in RichPresence)`, `ClearPresence()`, `TryGetLocalUser(out SocialUser)`, and the
  `JoinRequested(string secret)` / `JoinRequestReceived(JoinRequest)` events. Best-effort: never throws.
  `TryInitialize` must be re-attemptable on the same instance, because the controller retries a failed
  connect rather than rebuilding the provider. `IsConnected` is also how a provider reports that a live
  connection DROPPED, and the only answer that gets the session back (see the connect contract below).
- **`RichPresence`** - `Details`, `State`, `StartTimestampUtc`/`EndTimestampUtc`, `LargeImage`/
  `SmallImage` (`PresenceImage`), `Party` (`PresenceParty`), `JoinSecret`/`SpectateSecret`, `Buttons`
  (`PresenceButton`). Empty fields are omitted by the backend.
- **`SocialUser`** - `Id`, `Username`, `GlobalName` (the local platform identity).
- **`JoinRequest`** - an inbound ask-to-join. `Accept()` / `Reject()` answer it on the backend, and both
  are idempotent (only the first call lands) and best-effort. A game answers from its own UI, an
  unbounded time after the request arrived, so answering once the platform connection has dropped is a
  silent no-op rather than a throw.
- **`NullSocialProvider`** - silent no-op default (headless / no backend). The controller reads it as a
  deliberate opt-out and never arms its connect retry for it.
- **`SocialPresenceController`** - the orchestrator games use. `Initialize()`, `Retry()`, `State`,
  `IsEnabled`, `SetPresence`, `SetElapsedPresence`, `ClearPresence`, `Update()`, `TryGetLocalUser`, and
  the `StateChanged` / `JoinRequested` / `JoinRequestReceived` events.
- **`SocialPresenceState`** - `Uninitialized`, `Connecting`, `Connected`, `GivenUp`, `Disabled`,
  `Disposed`, `Reconnecting`. Poll `State` for a status line, or subscribe to `StateChanged`.
  `Reconnecting` is a session that HAD a connection and lost it, as against `Connecting`, which has never
  had one.
- **`SocialPresenceOptions`** - `RepublishInterval`, plus the connect-retry schedule
  (`ConnectRetryDelay`, `MaxConnectRetryDelay`, `ConnectRetryBackoff`, `MaxConnectAttempts`) and
  `StableConnectionSpan`, the age a connection has to reach before the drop that ends it refills the
  attempt budget.

## Usage

```csharp
using KhaozEngine.Social;

// Headless / no backend: silent no-op.
var social = new SocialPresenceController();

// With the Discord backend (add KhaozEngine.Social.Discord):
// var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<your-app-id>" });
// var social = new SocialPresenceController(provider);

social.ApplicationId = "<your-app-id>";
social.Initialize();

// From a menu:
social.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });

// In a run, with a live elapsed timer:
social.SetElapsedPresence(new RichPresence { Details = "In Game", State = "Boss Rush" }, elapsed);

// Once per frame:
social.Update();

// One-click join from a friend's profile:
social.JoinRequested += secret => myNetcode.JoinFromSecret(secret);
```

`SocialPresenceController` dedupes and throttles, so calling `SetPresence`/`SetElapsedPresence` every
frame is cheap.

## The connect contract

`Initialize()` is a one-shot call for the game, not a one-shot attempt. If the platform client is not up
yet (Discord takes a few seconds to start, and a player can easily launch the game first), the controller
goes to `Connecting` and re-attempts from `Update()` on a doubling backoff: roughly 0s, 3s, 9s, 21s, 45s,
1m33s, 2m33s and 3m33s by default, then `GivenUp`. So a game connects itself with no retry code of its
own, and a machine with no Discord at all is not polled for the whole session. Tune the schedule with
`SocialPresenceOptions`, and set `MaxConnectAttempts = 1` for the old fail-once behaviour: at that setting a
drop later in the session gets ONE reconnect attempt, one `ConnectRetryDelay` after the drop rather than
immediately as the cold-start attempt at `Initialize()` goes, and then `GivenUp`. Every wait is clamped to
`[0, 1 day]`, so `TimeSpan.MaxValue` reads as "no cap" and degrades to the day rather than overflowing the
schedule.

Presence set while the controller is still connecting is held (the latest one, never a queue) and
published as soon as the connect lands, so a menu line set at startup is not lost to the wait. A held
`SetElapsedPresence` keeps its absolute start instant, so the timer stays correct however long the connect
took. The hold publishes **before** `StateChanged` reports `Connected`, so a handler that publishes its own
line on that event wins and stays published, instead of being overwritten by the line the game had already
moved past.

## A connection that drops comes back

The same machinery covers the player who quits Discord halfway through a session, which is the commoner
half. `Update()` reads `provider.IsConnected` once a frame while connected, and a false takes the
controller to `Reconnecting`: the same backoff, with a fresh attempt budget and a fresh wait, the provider
kept rather than disposed, and `GivenUp` at the end of it if the platform never comes back (`Retry()`
re-arms that, exactly as it does from a cold start). The first attempt waits out `ConnectRetryDelay` rather
than going immediately, because the platform client is mid-shutdown at the instant its socket dies.

That fresh budget is what `StableConnectionSpan` (default 30s) qualifies. A drop only refills it if the
connection HELD for at least that long. A platform client that accepts a connect and loses it again
immediately is flapping rather than dropping (Discord mid-restart does this: its handshake succeeds the
moment the bytes are written, into a socket that is already going away), and a flap carries its spent
attempts forward instead, so the cycle ends in `GivenUp` rather than reconnecting and re-publishing every
few seconds for as long as the game is open. Set it to zero to opt out and treat every drop as a held
session.

The presence that was live at the drop is republished once when the reconnect lands, so a game does not
come back blank, and an elapsed timer keeps its absolute start across an outage of any length. A
`SetPresence` during the outage is held and wins instead (latest wins, as always), and a `ClearPresence`
during the outage cancels the republish. Nothing is published, and the provider is not even pumped, while
the session is down. The dedupe cache is dropped at the drop and re-primed by whatever the reconnect
publishes, so the line a game sets after coming back is judged against what is actually on the platform
now, not against what the dead client was showing.

**Writing a provider: which answer means what.** A transport that dies must be reported by returning false
from `IsConnected`, which is the recoverable signal: the controller keeps the provider and calls
`TryInitialize` again, so a provider that goes false has to leave itself connectable. THROWING means
something else and is terminal (below). A backend that can tell a plain disconnect from a real failure
should route the disconnect to `IsConnected`.

Two failures are NOT retried, on purpose. A provider that THROWS ends the session and disposes the
provider (`Disabled`), because a provider that threw is in a state the seam cannot promise anything about,
and the game loop is never touched either way. And a controller with no backend at all (the
`NullSocialProvider` default) goes straight to `Disabled` without arming any timer, so opting out costs
nothing per frame. Neither does a settled session: `Connected`, `GivenUp` and `Disabled` all read the clock
zero times per `Update()`, since only the backoff schedule needs one. The drop probe on the connected path
is one bool read on the seam, and reaches for the clock only once it finds a drop.

`Retry()` forces an attempt now and re-arms a controller that gave up, for a game that knows something the
controller cannot ("the player just launched Discord", "the user pressed Reconnect"). It works the same
whether the controller has never connected or is working its way back from a drop, and is a no-op once
connected, disabled or disposed.

Wiring a `StateChanged` handler straight to `Retry()` on `GivenUp` is worth one note: the forced attempt
runs inside the event, while the state is still `GivenUp`. With `MaxConnectAttempts = 1` that is one extra
attempt and no second `GivenUp` event, because the repeat transition is deduped by the equality guard. With
a larger budget the forced attempt re-arms the whole schedule and the controller lands back in `Connecting`,
so the handler is a reconnect loop rather than one extra try.

```csharp
// Strings.* are the game's own StringId constants: a status line is player-facing text, so it resolves
// through the localization catalog like every other label.
social.StateChanged += s => statusLine = s switch
{
    SocialPresenceState.Connecting => Strings.SocialConnecting,
    SocialPresenceState.Connected => Strings.SocialConnected,
    SocialPresenceState.Reconnecting => Strings.SocialReconnecting,
    SocialPresenceState.GivenUp => Strings.SocialUnavailable,
    _ => default,
};

// A "Reconnect" button in the settings screen:
social.Retry();
```

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
