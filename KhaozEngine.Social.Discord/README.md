# KhaozEngine.Social.Discord

Discord backend for the [KhaozEngine.Social](../KhaozEngine.Social) seam. `DiscordSocialProvider`
implements `ISocialProvider` over Discord Rich Presence with a pure-managed IPC client - no native
libraries, no third-party NuGet. Opt-in: NOT in any umbrella; add it explicitly on a game's client head
like `KhaozEngine.Physics.Bepu` or `KhaozEngine.WorldStore.Sqlite`. Depends only on `KhaozEngine.Social`.

## How it works

Discord Rich Presence does not need Discord's C++ SDK. The Discord desktop client listens on a local
socket (`\\.\pipe\discord-ipc-N` on Windows, a unix domain socket `discord-ipc-N` under the runtime/temp
dir on macOS/Linux). This package speaks the IPC protocol directly (4-byte opcode + 4-byte length +
UTF-8 JSON, System.Text.Json), so it ships zero native binaries and nothing per-RID.

The native Discord Social SDK (friends list, lobbies, voice) is out of scope; if ever needed it would be
a separate opt-in `.Native` backend behind the same `ISocialProvider`.

## Usage

```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;

// On the game's desktop head:
var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<your-discord-app-id>" });
var social = new SocialPresenceController(provider);
social.Initialize();
// ... social.SetPresence(...); social.Update() once per frame; social.Dispose() at shutdown.
```

Get a Discord Application id from the Discord Developer Portal. If Discord is not running the provider
stays disconnected and every call is a silent no-op.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
