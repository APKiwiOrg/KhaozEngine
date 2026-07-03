# KhaozEngine.Social

Game-agnostic social/presence seam. `ISocialProvider` is the provider-neutral contract (Discord today,
Steam/other later) for rich presence, local identity, and join/invite. `NullSocialProvider` is the
silent no-op default; `SocialPresenceController` adds throttling, dedupe, and error self-disable on top
of any provider. Depends only on `KhaozEngine.Diagnostics`. The Discord backend is the opt-in
[KhaozEngine.Social.Discord](../KhaozEngine.Social.Discord) package (in no umbrella).

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
