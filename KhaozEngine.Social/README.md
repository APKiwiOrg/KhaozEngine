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
  connect rather than rebuilding the provider.
- **`RichPresence`** - `Details`, `State`, `StartTimestampUtc`/`EndTimestampUtc`, `LargeImage`/
  `SmallImage` (`PresenceImage`), `Party` (`PresenceParty`), `JoinSecret`/`SpectateSecret`, `Buttons`
  (`PresenceButton`). Empty fields are omitted by the backend.
- **`SocialUser`** - `Id`, `Username`, `GlobalName` (the local platform identity).
- **`JoinRequest`** - an inbound ask-to-join; `Accept()` / `Reject()`.
- **`NullSocialProvider`** - silent no-op default (headless / no backend). The controller reads it as a
  deliberate opt-out and never arms its connect retry for it.
- **`SocialPresenceController`** - the orchestrator games use. `Initialize()`, `Retry()`, `State`,
  `IsEnabled`, `SetPresence`, `SetElapsedPresence`, `ClearPresence`, `Update()`, `TryGetLocalUser`, and
  the `StateChanged` / `JoinRequested` / `JoinRequestReceived` events.
- **`SocialPresenceState`** - `Uninitialized`, `Connecting`, `Connected`, `GivenUp`, `Disabled`,
  `Disposed`. Poll `State` for a status line, or subscribe to `StateChanged`.
- **`SocialPresenceOptions`** - `RepublishInterval`, plus the connect-retry schedule
  (`ConnectRetryDelay`, `MaxConnectRetryDelay`, `ConnectRetryBackoff`, `MaxConnectAttempts`).

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
`SocialPresenceOptions`, and set `MaxConnectAttempts = 1` for the old fail-once behaviour.

Presence set while the controller is still connecting is held (the latest one, never a queue) and
published as soon as the connect lands, so a menu line set at startup is not lost to the wait. A held
`SetElapsedPresence` keeps its absolute start instant, so the timer stays correct however long the connect
took.

Two failures are NOT retried, on purpose. A provider that fails once **connected** ends the session and
disposes the provider (`Disabled`): a dead transport is not a cold start, and the game loop is never
touched either way. And a controller with no backend at all (the `NullSocialProvider` default) goes
straight to `Disabled` without arming any timer, so opting out costs nothing per frame.

`Retry()` forces an attempt now and re-arms a controller that gave up, for a game that knows something the
controller cannot ("the player just launched Discord", "the user pressed Reconnect"). It is a no-op once
connected, disabled or disposed.

```csharp
social.StateChanged += s => statusLine = s switch
{
    SocialPresenceState.Connecting => "Connecting to Discord...",
    SocialPresenceState.Connected => "Discord connected",
    SocialPresenceState.GivenUp => "Discord not found",
    _ => string.Empty,
};

// A "Reconnect" button in the settings screen:
social.Retry();
```

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
