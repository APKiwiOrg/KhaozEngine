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
- **`RichPresence`** - `Details`, `State`, `StartTimestampUtc`/`EndTimestampUtc`, `LargeImage`/
  `SmallImage` (`PresenceImage`), `Party` (`PresenceParty`), `JoinSecret`/`SpectateSecret`, `Buttons`
  (`PresenceButton`). Empty fields are omitted by the backend.
- **`SocialUser`** - `Id`, `Username`, `GlobalName` (the local platform identity).
- **`JoinRequest`** - an inbound ask-to-join; `Accept()` / `Reject()`.
- **`NullSocialProvider`** - silent no-op default (headless / no backend).
- **`SocialPresenceController`** - the orchestrator games use.

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
frame is cheap. Any provider error disables social for the session without touching the game loop.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
