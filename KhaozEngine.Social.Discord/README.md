# KhaozEngine.Social.Discord

Discord backend for the [KhaozEngine.Social](../KhaozEngine.Social) seam. `DiscordSocialProvider`
implements `ISocialProvider` over Discord Rich Presence using a pure-managed IPC client (Windows named
pipe, unix domain socket) - no native libraries, no third-party NuGet. Opt-in: in no umbrella, added
explicitly by a game's client head like `KhaozEngine.Physics.Bepu`. Depends only on `KhaozEngine.Social`.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
