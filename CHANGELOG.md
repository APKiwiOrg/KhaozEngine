# Changelog

All notable changes to KhaozEngine. One shared version line `<KhaozEngineVersion>` in `Directory.Build.props`
governs the whole MonoGame-free engine (custom stack + graduated foundation packages + the four umbrella
metapackages). The legacy 4.x MonoGame line was deleted from the repo. See the post-MonoGame plan in
`docs/ROADMAP.md`.

## 7.67.0

`ReplicatedCharacterAnimators` now derives velocity over a short sliding window instead of a single frame's
position delta, fixing the animation strobe/freeze on a plateauing position stream (local AND remote players).

- Game.Render3D: the bridge's locomotion derivation was reading speed from one frame's position delta, but the
  position it is fed PLATEAUS between server ticks - `ClientPrediction.RenderedState` clamps the inter-tick
  fraction at 1, so once interpolation saturates the rendered position is constant until the next `Predict`.
  Whenever render fps > tick rate that yields one or more zero-delta frames per tick; those frames read speed 0
  and strobed the locomotion state Idle<->moving every frame, and `AnimationPlayer.Play` restarts the clip on
  every state change - so the clip never advanced past its first frames (the animation "froze") while the draw
  transform kept moving (the avatar "glided"). Hit the local predicted player and every remote.
- Fix: `ReplicatedCharacterAnimators.Update` accumulates displacement + elapsed time per entity and recomputes the
  derived velocity only when the window fills (default one tick), holding the last good velocity across the
  intervening zero-delta frames; both the planar speed/facing AND the vertical velocity used by the grounded
  heuristic come from this windowed velocity. A genuine stop (no displacement across a whole window) still
  resolves to Idle within one window (~33 ms, imperceptible). Frame-rate independent.
- API: `CharacterAnimatorTuning.VelocityWindowSeconds` (default `1/30`) exposes the window length - set it to one
  tick of your position source; `<= 0` reverts to the old per-frame derivation. Additive; existing tuning unchanged.
- Tests: headless plateau/strobe regression (a render-fps > tick-rate sample stream that moves every Nth frame
  and holds between - asserts the state stays steady Walk with no Idle strobe), a real-stop-settles-to-Idle guard,
  windowed speed-band selection (Walk vs Run picked by the windowed speed, not a single frame), and a
  `VelocityWindowSeconds` hold-duration test. Existing lifecycle/facing/air-state/first-frame tests stay green.
- `NetworkedWalkSample` already draws via the bridge (since 7.66.0), so this path is now visually exercised
  in-engine; the sample needs no change.

## 7.66.0

Sample-character swap + the bridge demo: `TerrainWalkSample` now walks the CC0 Quaternius Universal character (one
glb with `Idle`/`Walk`/`Run`/`Jump`/`Fall`) instead of the old KayKit Knight, and `NetworkedWalkSample` renders one
animated avatar per replicated player through the 7.65.0 `ReplicatedCharacterAnimators` bridge instead of capsules.

- Samples: `TerrainWalkSample` swaps `Knight.glb` for the committed CC0 Quaternius Universal `Player.glb` (body +
  five in-place locomotion clips named exactly `Idle`/`Walk`/`Run`/`Jump`/`Fall`, one 65-bone universal rig; baked
  from Quaternius "Universal Base Characters" + "Universal Animation Library"). The clip map uses those five names;
  the existing auto-fit scale (model height -> 1.8 m capsule) is unchanged. `NetworkedWalkSample` now drives a
  `ReplicatedCharacterAnimators` over `WorldClient.Snapshot()` (local player fed its exact movement via the new
  `WorldClient.LocalGrounded`/`LocalVerticalVelocity`; remotes position-only) and draws over `Live`, with the capsule
  kept as the missing-asset fallback. This is the sample exercising the bridge end to end.
- `CharacterPose.Pose` is now a `Matrix4x4[]` (was `IReadOnlyList<Matrix4x4>`), the same type as
  `AnimatedCharacter.Pose`, so it passes straight to the span-taking `Scene3D.DrawSkinned` with no copy. Refines the
  7.65.0 bridge before it is published.
- Asset metadata: `assets/character/CREDITS.md` rewritten for the Quaternius Universal packs (CC0) with the bake
  provenance; the KayKit `LICENSE.txt` was dropped (CC0 note folded into CREDITS).

## 7.65.1

Terrain PBR splat materials render correctly on Metal (shipped broken in 7.64.0, which rendered the textured ground
~black). The 7.64.0 splat pipeline bound a SECOND uniform buffer (per-material params) alongside the frame UBO;
Veldrid/SPIRV-Cross on Metal mis-binds a second uniform buffer (it reads the first buffer's bytes), so the per-layer
tint read garbage and the albedo zeroed out (black ground, smeared to flat red/green under the night lights). The
params now ride in the SINGLE frame UBO (appended after the point-light arrays, re-synced per material each frame via
a splat-specific vertex shader), matching the model pass's proven one-UBO + textures + sampler shape. No public API
change. Adds an on-device regression test that renders a textured chunk and asserts the ground is lit + multi-channel,
not the black/primary output (the original test only checked it rendered without throwing, which is how the bug
shipped).

## 7.65.0

Position-driven replicated character animators: drive one `AnimatedCharacter` per networked player (local AND every
remote) from the world positions the netcode already surfaces, with locomotion (idle/walk/run/jump/fall) and facing
derived per entity. The reusable glue that was missing for an animated-avatar overworld; the rigged glTF asset stays
game content.

- Game.Render3D: `ReplicatedCharacterAnimators` owns one `AnimatedCharacter` per entity and turns a per-frame stream
  of position samples into draw-ready poses + transforms. New public API: `CharacterSample` (engine-neutral per-entity
  input - position-only, or position + exact movement; no netcode type, so the package keeps its layering and stays
  usable by non-NetWorld games), `CharacterPose` (the per-character output: `World` = `scale * RotationY(facingYaw) *
  Translation`, the bone `Pose`, the `LocomotionState`, `IsLocal`), `CharacterAnimatorTuning` (`Locomotion`/`Crossfade`
  applied to brains the set constructs + `YawSmoothing`/`MinPlanarSpeedForFacing`/`GroundedVerticalEpsilon`/`Scale`/
  `FacingYawOffset`), and `ReplicatedCharacterAnimators` itself (a `Func<AnimatedCharacter>` factory ctor + a
  convenience skeleton-plus-clips ctor). `Update(samples, dt)`: lifecycle (create on a new id, drop on an absent one,
  no leak on disconnect), derives planar speed / vertical velocity / facing from the position delta (exact grounded +
  vertical velocity used instead when a sample carries them), reuses `LocomotionStateMachine` for the clip, holds yaw
  below the facing threshold. Owns no GPU handle and never calls `Scene3D` - iterate `Live` and `DrawSkinned` yourself
  - so it is fully headless-testable. Client-cosmetic: no netcode/server changes, the server stays authoritative on
  position.
- NetWorld: `WorldClient` gains read-only `LocalRenderState` (the predicted `PlayerMoveState`), `LocalGrounded`, and
  `LocalVerticalVelocity` so a consumer can fill `CharacterSample`'s exact-movement fields for the local avatar
  instead of finite-differencing its position. Additive, no wire change.

## 7.64.0

Terrain PBR splat-textured materials: the terrain now renders five tileable PBR layers (grass/dirt/rock/sand/snow)
blended per-fragment by the baked splat weights, with world-space triplanar tiling, normal maps, mips, and
anisotropic filtering (opt-in; omit the material and the height/slope vertex-colour ramp path is byte-identical).

- Render3D: a new "splat" pipeline (`SplatFrag`, sibling of the model pipeline in `ModelRenderer`, shares the
  frame UBO + instance buffer). Two `texture2DArray`s (albedo + normal, 5 layers) + a per-layer-scalar-roughness
  params UBO; the five weights ride in the existing `ModelVertex.Color` (4 packed + a 5th derived as 1 - sum), so
  the vertex format is unchanged. New public API: `SplatProjection`, `SplatLayerImage`, `SplatMaterialConfig`/
  `SplatParamsData`, `SplatMath`, `Scene3D.SplatMaterialHandle`, `Scene3D.LoadSplatMaterial`,
  `Scene3D.LoadMesh(GltfMesh, SplatMaterialHandle)`, `Scene3D.UnloadSplatMaterial`.
- Terrain.Render3D: `TerrainSplatPacking`, `TerrainMaterialLayer`/`TerrainLayeredMaterial`,
  `TerrainMaterialPresets` (procedural placeholder), `TerrainScene3D.LoadTerrainMaterial` + the textured
  `LoadTerrainChunk` overload, and an optional material on `Scene3DChunkSink`. With no material the ramp path is
  byte-identical.
- Gpu seam: `GpuSamplerFilter.Anisotropic` + `GpuSamplerDescription.MaximumAnisotropy` (trilinear fallback when
  the device lacks anisotropy), `GpuTextureDescription.Texture2DArray`, an `UpdateTexture` overload with
  mip/array-layer, and `IGpuCommandList.GenerateMipmaps`.

## 7.63.0

Holding a key in a `KhaozEngine.Gui` text field now auto-repeats - a held Backspace deletes continuously and a held
character key keeps typing, at the OS repeat delay + rate - instead of acting once on the press edge.

**OS key auto-repeat surfaced through `InputState` (`KhaozEngine.Windowing`).** `InputState` gains an additive
`KeysRepeated` set plus `WasRepeated(Key)` (true on a frame the key fired an OS auto-repeat tick) and
`WasTyped(Key)` (`WasPressed(key) || WasRepeated(key)`, the "a character was typed this frame" signal). `WasPressed`
keeps its press-edge-only meaning (auto-repeat excluded), so existing callers are unchanged; the constructor's new
`repeated` argument is the last optional parameter and defaults to empty, so every current builder still compiles.
`AppWindow` fills it from GLFW's `REPEAT` key action: Silk's high-level keyboard maps only PRESS/RELEASE and drops
REPEAT, so `AppWindow` now installs its own GLFW key callback (the only place allowed to touch the GLFW statics) and
CHAINS to Silk's previous callback, so KeyDown/KeyUp - and thus the existing pressed/released sets - keep working
unchanged. GLFW key codes share the Silk key integer values, so the existing key mapping is reused (no second table).

**`TextEntry.Apply` honours it (`KhaozEngine.Gui`).** Backspace and the printable-key loop now act on `WasTyped`
instead of `WasPressed`, so a held key repeats at the OS rate. The signature is unchanged, so `TextInput.Update` and
every other caller get hold-to-repeat for free. The Ctrl/Super chord suppression still runs before the printable
loop, so holding Cmd/Ctrl never machine-guns a letter into the field; Backspace-repeat still works under a held
modifier. Out of scope (unchanged `TextEntry` non-goals): caret movement / selection / word-delete, and IME / dead
keys / locale layouts. Headless tests cover repeat-driven delete/type, `maxLength`/filter under repeat, the chord
block on repeat ticks, and the `WasPressed`-excludes-repeat / `WasTyped`-is-the-union contract.

## 7.62.0

Players can now carry a replicated display name (a nameplate string like "Daniel", distinct from the account id),
and any world point can be projected to a screen pixel - the two halves a consumer needs to float a name above a
remote player's head.

**A - display-name replication (`KhaozEngine.NetWorld` + `KhaozEngine.Netcode`).** A new `PlayerIdentity { string
DisplayName }` component is registered in `MoveProtocol.CreateRegistry()` as type id 3
(`MoveProtocol.IdentityTypeId`), with a length-prefixed UTF-8 codec capped at `MoveProtocol.MaxDisplayNameBytes`
(64) and no lerp - the cap is enforced on both ends (write truncates at a UTF-8 char boundary, read clamps), so a
hostile or corrupt name can neither exceed the bound on the wire nor blow the read buffer. The name is re-sent in
every AoI snapshot (names are static, so simple over clever at this player scale). Set it server-side via
`WorldServer.SetPlayerDisplayName(slot, name)` / `ShardedWorldServer.SetPlayerDisplayName(slot, name)` (e.g. from a
`PlayerJoined` handler against the game's DB), and read it client-side off the new additive
`EntityRenderState.DisplayName` (`null` when the entity carries no identity; the existing 3-arg ctor and every
current `Snapshot()` caller are unchanged).

The display name can also ride in on the connect token: `SignedToken` gains a v2 format
(`v2.<subject>.<base64url-name>.<expUnix>.<mac>`) minted with the new `Mint(subject, displayName, expiry, secret)`
overload and read with the new `TryVerify(..., out subject, out displayName, out reason)` overload. v1 tokens are
unchanged and still verify (empty name). A new opt-in `IConnectionDisplayName` companion interface lets an
authenticator surface that verified name; `HmacTokenAuthenticator` implements it, `NetServer` probes for it and
includes the name on `ServerSessionEvent.DisplayName`, and `WorldServer`/`ShardedWorldServer` auto-apply a
non-empty token name at join. So token games get nameplates for free while DB-sourced names use the setter. The
account id / verified subject and the persistence path are unchanged - the display name is purely additive and
cosmetic. `IConnectionAuthenticator` itself is untouched (the seam is the new optional companion interface), so
`AllowAllAuthenticator` and existing custom authenticators keep compiling and just yield an empty name.

**B - world-space text (`KhaozEngine.Render3D`).** `IIsoCamera3D` gains
`WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel)` - the forward
inverse of the existing `ScreenToRay`, implemented for both `FollowCamera3D` and `IsoCamera3D` (pure
`System.Numerics`, round-trips against `ScreenToRay`, returns `false` for a point not in front of the camera). A
new `WorldLabel.Draw(...)` static helper projects a world anchor + offset and draws centered screen-space text via
`SpriteBatch.DrawString`, reusing the shipped `SpriteFont` path (no per-name texture). Labels are drawn on top
(screen-space, not depth-tested): occluded/depth-tested nameplates are out of scope.

Additive across the board (new component + type id, new optional `EntityRenderState` field, new token format/overloads
+ companion interface, new camera method + label helper), so minor. Bumps the `NetWorld`/`Server` and `Game3D`
umbrellas. A consumer (Ruinborne) sets each player's display name server-side and draws it above the avatar head.

## 7.61.0

Client-prediction render smoothing is now 3D and pop-free, so a networked avatar (and the follow camera tracking
it) stops jittering when moving or jumping against a remote/high-latency server.

Three fixes in `KhaozEngine.Netcode` `ClientPrediction`, plus an opt-in camera knob:

- **Vertical axis smoothed (was XZ-only).** Both the inter-tick interpolation and the reconciliation glide now
  carry the vertical axis, so a jump/fall eases instead of stair-stepping at the tick rate and popping on every
  vertical misprediction. `IPredictedState<TSelf>` gains two default-implemented members - `float Vertical` and
  `TSelf WithRenderState(Vector2 position, float vertical)` - both defaulting to the old planar behaviour, so
  existing 2D implementers compile and behave unchanged (additive, non-breaking). `NetWorld.PlayerMoveState`
  overrides both to carry its `Y`, and `WorldClient`'s rendered local avatar now eases its height.
- **Mid-tick reconcile no longer pops (the moving jitter).** `Reconcile` previously discarded the un-played
  remainder of the inter-tick interpolation (it measured continuity from the full-tick position and collapsed the
  lerp), so a snapshot arriving mid inter-tick jolted the avatar forward by up to one tick of motion - irregular
  on a remote server, hence visible jitter. It now anchors the smoothing offset to the ACTUAL on-screen
  (inter-tick interpolated) position, so the rendered position is continuous across the rebase. The hard-snap /
  smoothing decision is gated on the pure prediction-divergence magnitude (3D), independent of the in-flight
  smoothing offset, so a residual glide never spuriously hard-snaps.
- **`PredictionSettings.Default.CorrectionDeadZone` lowered 1.5 -> 0.03 (3 cm).** At 1.5 world units every
  realistic latency misprediction snapped instead of gliding; the small default smooths them. The dead-zone is now
  a presentation-side cleanup threshold (the decaying offset snaps to zero once within it) rather than a reconcile
  gate. `HardSnapDistance` (100) and `CorrectionRate` (8) are unchanged. This changes feel for every prediction
  consumer; sanity-check the other games on adoption.
- **`FollowCamera3D` opt-in target damping (default OFF).** `EnableTargetDamping` + `TargetDampingRate` (10/s) make
  the camera follow a smoothed `EffectiveTarget` that eases toward `Target`, driven frame-rate-independently by
  `FollowCameraController.Update(input, dt)` (now uses its `dt`). Default off, so `Eye`/`View` read `Target`
  directly and every existing consumer is byte-for-byte unchanged. Belt-and-suspenders for any residual jitter.

Additive API (default-interface-method hooks + new optional camera fields) and behaviour-only changes to the
render smoothing, so minor. Headless tests cover the vertical inter-tick ease, a sub-hard-snap vertical correction
easing over frames, mid-tick reconcile continuity (planar + vertical), the new dead-zone default, and the camera
damping (convergence + frame-rate independence).

## 7.60.0

`WorldClient` can now predict against the same static prop/building colliders and walkable surfaces the server is
authoritative over, so a networked consumer can make props solid without the client rubber-banding.

The collision system (`WorldColliders`/`WorldSurfaces`, shipped 7.55.0 + the 7.56/7.58 domed-rock fixes) already
ran in the single-player `CharacterController3D` and on the server via `PlayerMoveSimulator`/`WorldServer`/
`ShardedWorldServer`, but the networked prediction client `WorldClient` built its internal simulator with no
colliders/surfaces. A consumer that wired colliders server-side only would have the client predict straight
through every tree while the server clamped at the collider, so every snapshot reconcile-snapped the player back.

`WorldClient`'s ctor gains two optional trailing params mirroring `WorldServer`:
`WorldClient(..., WorldBounds? bounds = null, WorldColliders? colliders = null, WorldSurfaces? surfaces = null)`.
They are passed straight to the internal `PlayerMoveSimulator` (which already threaded both into
`CharacterMovement.Step`), so prediction and authority run identical math once the client is given the same set.
Defaults `null` preserve the terrain-only behaviour for every existing caller (no breaking change). Additive
(new optional params + new test coverage), so minor.

## 7.59.1

Fix: holding Ctrl or Super (Cmd) while a focused `TextInput`/`TextEntry` field is active no longer types the
printable key, so shortcut chords like Ctrl+V / Cmd+V paste instead of inserting a stray letter. `TextEntry.Apply`
only checked Shift, so any Ctrl/Cmd chord still mapped its key and appended the letter into the buffer (in a
consumer this corrupted a pasted token, leaving a stray `v` from Cmd+V). `Apply` now returns right after the
Backspace handling when LeftControl/RightControl or LeftSuper/RightSuper is down; Shift remains a text modifier
and still applies (so Shift+A is still `A`). Headless tests cover Ctrl+V and Cmd+V leaving the buffer unchanged,
bare V still appending `v`, and Shift+A still uppercasing.

## 7.59.0

Fix: a player who reconnects (or any player who lands on a recycled slot) can move again instead of freezing,
shaking at spawn, for the minutes it used to take to self-heal. The 7.x anti-replay pass made
`RemoteCommandQueue<TCommand>.Store` reject any seq at or below a slot's processed high-water mark, but the
servers never cleared that per-slot state when a slot was released. `SlotAllocator` recycles the lowest free
slot, so the next client to land on it inherited the prior occupant's high-water mark (often tens of thousands
of ticks). That client's prediction restarts its seq at 0, so every command it sent was rejected as stale: the
queue stayed empty, the server froze the player at spawn and stamped each snapshot with the dead ack, and the
client's reconcile then dropped all pending inputs and snapped back each frame (the shake). It cleared only once
the client's seq crawled past the dead mark.

New `RemoteCommandQueue<TCommand>.Forget(int slot)` drops a slot's buffered commands and its high-water mark
(idempotent for an unknown slot). `WorldServer` and `ShardedWorldServer` now call it on `OnLeave` (when the slot
is released) and at the top of `OnJoin` (belt-and-suspenders for a missed Left). Forgetting on release does not
weaken anti-replay: a new connection on a recycled slot is a new session whose seqs legitimately restart from 0,
and replay protection still holds within a live session. Additive (`Forget` is new public API) + bugfix.

Known follow-up (separate, minor): `WorldClient.OnSnapshot` zeroes `nextSeq` via `prediction.Reset` on the first
snapshot, after the client may have already sent pre-basis commands during the connect window. On a clean slot
that causes a brief latency-proportional re-send-rejection hitch (not the multi-minute freeze fixed here).
Candidate fixes: gate input send until the basis is established, or stop zeroing `nextSeq` on `Reset`.

## 7.58.0

Fix: you can now walk/jump up the side of a domed rock onto its top, instead of only being able to drop onto it
from directly above. A domed prop's static collider is a vertical cylinder (radius = the footprint) whose `Top` is
the rock's PEAK, paired with a walkable surface that ramps from a low rim up to that peak. The height-aware
side-block gated "am I standing on it?" on the peak, so the rock's whole side acted as a wall up to peak height:
the cylinder kept the capsule centre outside the surface footprint and a jump's apex is below the peak, so the only
way on was to land from above. (7.56.1 fixed standing on the surface once already on top; the side approach was
unchanged.)

New `WorldColliders.Resolve(position, radius, footY, WorldSurfaces? surfaces, ...)` overload gates the side-block on
the WALKABLE SURFACE per collider rather than the prop's peak: a collider is skipped (you are on it, not hitting its
side) when either the capsule centre is already over the walkable footprint - the vertical support/step-up places
it, so a domed top is never shoved off mid-traverse - or, approaching from outside, once the feet clear the rim
height where you would step onto the prop (sampled inward from the footprint edge toward the player). A flat-top
prop's rim equals its top, so it stays mountable only from on top; a thin blocker (a tree: `Top = +inf`, no surface)
always blocks; a below-rim approach into the base still blocks. `CharacterMovement.Step` threads the `surfaces`
through, so the server sim and client prediction run the same step. Additive: the new overload sits beside the
7.56.1 scalar-`surfaceTop` overload; no breaking change. Verified against the shipped `rock_a` (footprint radius
1.31, peak 1.79): a jump up its side now mounts it.

Known limitation (unchanged from 7.56.1): the "over the footprint" / rim sample is the max prop-surface there, not
per-collider, so standing on a shorter walkable prop hard against a taller one can let the capsule walk through the
taller one's side. Trees and below-rim approaches are unaffected.

## 7.57.0

Connection auth now yields a verified identity, and the engine ships a signed connect-token so an exposed server
can gate joins and persist on a stable subject. `IConnectionAuthenticator.TryAuthenticate` changes signature to
`bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)`: on accept it returns
the verified `subject` (the stable account/player identity) the connection is bound to, not just accept/reject.
`AllowAllAuthenticator` returns the connect token decoded UTF-8 as the subject (empty when the token is empty), so
the dev default behaves exactly as before. The subject rides `NetServer`'s `Joined` event as the new
`ServerSessionEvent.Subject` (empty for the other event kinds; the `Joined` factory gains an optional `subject`
arg, the token still travels in `Data`). `WorldServer` and `ShardedWorldServer` gain an optional last constructor
argument `IConnectionAuthenticator authenticator = null` (default `AllowAllAuthenticator`), passed to the internal
`NetServer`, and their `OnJoin` now uses `ev.Subject` as the persisted `accountId`, falling back to `guest:{slot}`
when it is empty (was: decode the raw connect token). With the allow-all default the derived accountId is
unchanged (token-as-subject), so every existing caller and persisted key is preserved.

New in `KhaozEngine.Netcode`: `SignedToken`, a zero-dependency (BCL-only HMAC-SHA256) stateless connect-token.
`SignedToken.Mint(string subject, DateTimeOffset expiry, byte[] secret)` produces
`v1.<subject>.<expUnix>.<base64url-HMACSHA256>` (the signature covers `v1.<subject>.<expUnix>`; the subject may not
contain `.` so the four fields split cleanly - `Mint` throws `ArgumentException` if it does).
`SignedToken.TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string reason)`
checks the signature with a fixed-time compare (`CryptographicOperations.FixedTimeEquals`) before expiry and
returns the embedded subject on success, or false with a short reason (`"malformed"` / `"bad signature"` /
`"expired"`) and an empty subject. `HmacTokenAuthenticator(byte[] secret, Func<DateTimeOffset> clock)` implements
the authenticator seam over `TryVerify` (clock injected for determinism / NTP correction). Because a re-issued
token for the same account carries the same subject, persistence keyed on the subject survives token rotation.

Headless tests cover mint/verify roundtrip + token format, expiry rejection, tamper rejection, wrong-secret
rejection, subject-with-dot rejected at mint, malformed-token rejection, `HmacTokenAuthenticator` accept/reject,
`AllowAllAuthenticator` token-as-subject (empty when no token), and `WorldServer` binding the verified subject as
its `accountId` (plus rejecting a client whose token was signed with the wrong secret).

SemVer note: `IConnectionAuthenticator.TryAuthenticate` is a public interface, so its new signature is technically
source-breaking for an external implementer of a custom authenticator (the engine's own `AllowAllAuthenticator` /
`HmacTokenAuthenticator` and all *callers* are updated and behaviour-preserving). Versioned **minor**, treating
the auth seam as infra-internal and the change as additive in spirit (new types + optional ctor args, defaults
preserve allow-all); a custom authenticator adopts by adding the `out string subject` parameter.

## 7.56.1

Fix: jumping onto a domed rock no longer shoves you off its side onto the ground. The height-aware side-block in
`KhaozEngine.Collision.WorldColliders.Resolve(footY)` decided "am I standing on this prop?" by comparing the feet
against the collider's single max solid `Top`. On a domed/bumpy prop the walkable surface (`WorldSurfaces.Query`)
sits below that max almost everywhere, so standing on the surface (`footY < Top`) was mis-read as a side hit and
the capsule was pushed out by its radius, off the rock and onto the terrain. Only a flat-topped prop (surface near
`Top`) stood correctly.

The "standing on it" gate now also accepts the walkable surface height under the player: `Resolve` gains an
optional `surfaceTop` parameter and skips a collider's side-block when `footY >= surfaceTop - skin` (in addition to
the existing `footY >= Top - skin`). `CharacterMovement.Step` threads the per-position surface
(`surfaces.Query(x, z)`, or +inf when there is none) into the height-aware `Resolve`, so a domed rock is standable
across its whole top. A genuine below-the-surface side approach is still blocked, and a thin blocker (a tree:
`Top = +inf`, no surface) always blocks, even while the feet rest on a neighbouring prop's surface. Server-side
(`PlayerMoveSimulator`/`PlayerMovementSystem`) and client prediction (`CharacterController3D`) run the same step,
so the fix is authoritative and reconciles cleanly. New optional `WorldColliders.Resolve` parameter `surfaceTop`
(defaults to +inf = `Top`-only, the old behaviour); no breaking change.

Known limitation (out of scope, noted): the surface fed to the gate is the max prop-top under the player, not
per-collider. Standing on a shorter walkable prop next to a taller one can let the capsule walk through the taller
one's side (its finite `Top` is gated against the shorter surface). Trees and below-surface approaches are
unaffected; coupling each surface to its own collider is the precise fix if this ever matters.

## 7.56.0

Animated characters: glTF animation-clip playback + a locomotion blend, so skinned capsules become characters
that idle/walk/run and jump/fall. `KhaozEngine.Render3D` gains a GPU-free animation layer beside the existing
rig/skinning: `GltfLoader.LoadAnimations(path)` reads SharpGLTF `LogicalAnimations` into `AnimationClip`s
(per-joint TRS keyframe tracks keyed by glTF logical node index, with `InterpolationMode` LINEAR/STEP, CUBICSPLINE
reduced to its value keys), and `GltfLoader.LoadSkinned` now also attaches a `Skeleton` (the joint hierarchy:
topologically-ordered parent links + rest-local `JointPose` TRS + bone-to-node map + a logical-node lookup) to
`SkinnedGltfMesh` (new optional `SkinnedGltfMesh.Skeleton`; existing constructors unchanged). `AnimationSampler`
samples a clip at a time into per-node local poses and composes the hierarchy into the joint-WORLD bone palette
`Scene3D.DrawSkinned` already consumes (`SamplePose`/`Compose`/`SampleToBonePalette`/`BlendPoses`/`Wrap`);
`AnimationPlayer` advances + loops a clip and CROSSFADES into a new one over a short blend (blends the two clips'
local TRS, composes once). New public types: `JointPose`, `Skeleton`, `AnimationClip` (+ `JointTrack` /
`Vector3Track` / `QuaternionTrack` / `InterpolationMode`), `AnimationSampler`, `AnimationPlayer`.
`KhaozEngine.Game.Render3D` adds the locomotion mapping: `LocomotionState` (Idle/Walk/Run/Jump/Fall),
`LocomotionThresholds`, and `LocomotionStateMachine.Evaluate(horizontalSpeed, grounded, verticalVelocity, thresholds)`
(speed picks idle/walk/run; the air state wins while airborne - rising = Jump, otherwise Fall), plus
`AnimatedCharacter`, which wraps a mesh `Skeleton` + the per-state clips + an `AnimationPlayer` + the state machine
and turns a movement state (`speed`, `Grounded`, `VerticalVelocity`) + `dt` into the bone palette for the skinned
draw - driven identically for the LOCAL player (own movement) and REMOTE players (replicated movement). Purely
client-cosmetic: clips are picked from already-known/replicated movement state, no netcode or server-side animation
changes. `TerrainWalkSample` swaps the greybox capsule for a committed KayKit CC0 rigged + animated character
(Character Pack: Adventurers, skinned-ingested so the rig + 76 animation clips survive - NOT the flatten-prop path),
mapping `Idle`/`Walking_A`/`Running_A`/`Jump_Start`/`Jump_Idle` to the locomotion states; it idles, walks, runs,
and jumps/falls, facing its move direction. Out of scope (unchanged): animation events, root motion, IK,
additive/facial layers, full blend trees, networked animation, the prop flatten path. All new pose/palette/SM math
is headless-tested; no GPU golden added (the math coverage is sufficient).

## 7.55.0

Walkable prop/building surfaces (stand on / jump onto rocks + roofs), character-physics sub-project B, building on
the 7.54.0 vertical physics. Each walkable-solid prop kind bakes a render-free top-down max-height grid (single-
valued top contour; no overhangs), placed by the deterministic scatter and queried at runtime so the player's
support height becomes `max(terrain, prop surface)`. New in `KhaozEngine.Collision`: `PropSurface` (the unit-scale
height grid + bilinear `SampleLocal` + versioned binary IO), `WorldSurface` (a placed surface, scale/yaw applied at
query time so client + server match), and `WorldSurfaces` (a `SpatialHashGrid`-backed set whose `Query(x,z)`
returns the max top under you). `WorldCollider` gains a `Top` (the prop's solid top) and `WorldColliders` a
height-aware `Resolve(position, radius, footY)` that blocks a prop's side only while the feet are below its top, so
standing on a roof is not shoved off (a thin blocker keeps `Top = +inf` and always blocks). `KhaozEngine.Locomotion`:
`MoveTuning` gains `StepHeight` (default 0.4) and the vertical `CharacterMovement.Step` takes an optional
`WorldSurfaces?` - the capsule lands/rests on the higher of terrain and a prop surface, the static-collision
push-out is height-aware, and a support rise no greater than the step height is auto-mounted (step-up); null = the
7.54.0 terrain-only behaviour. `KhaozEngine.Render3D`: `PropSurfaceBake.Bake(GltfMesh)` rasterizes the grid from a
normalized mesh and `IsWalkableSolid` classifies walkable-solid (rock/log/building) vs thin-blocker (tree);
`PropSurfaceLoader` reads the baked `.surf` render-free and bakes-to-file for tooling; `AssetEntry` gains
`Surface` + `Heightmap`. New tool package `KhaozEngine.PropSurface.Tool` (`ke-propbake`): folds the surface bake
into kit ingest (bakes a `.surf` per walkable-solid prop next to the glTF, stamps the manifest; re-ingest = re-bake).
`KhaozEngine.Terrain.PropSurfaces.FromScatter` builds the surface set from the scatter + an obstacle list, and a
top-aware `PropColliders.FromScatter` overload stamps each collider's `Top`. `WorldSurfaces` threads through
`PlayerMoveSimulator`/`PlayerMovementSystem`/`WorldServer`/`ShardedWorldServer`/`CharacterController3D`, server-
authoritative + client-predicted (the surface is a deterministic function of (x,z), like terrain). `TerrainWalkSample`
makes the scattered rocks solid + jumpable and adds a jumpable platform with a walkable roof; trees stay thin
blockers. Out of scope (named): overhangs/interiors/caves, full 3D mesh collision, dynamic/moving surfaces,
player-vs-player, fall damage, climbing/mantling, streaming surfaces. Additive; new tool package; minor.

## 7.54.0

Vertical character physics (gravity + jump), character-physics sub-project A. Movement was purely horizontal
(`CharacterMovement.Step` ground-clamped Y every tick - no air, no falling); this adds the vertical axis,
server-authoritative with client prediction from the start, over terrain. New `KhaozEngine.Locomotion.MoveState`
(the kinematic state carried tick-to-tick: capsule position + `VerticalVelocity` + `Grounded` + the coyote /
jump-buffer feel timers) and a new `CharacterMovement.Step(in MoveState, ...) -> MoveState` overload integrate
gravity (clamped to `MaxFallSpeed`), do land-and-clamp ground contact (with a `GroundedEpsilon` skin so a downhill
run does not jitter grounded/airborne), and jump (coyote-time + jump-buffer, both consumed on a jump so there is no
double-jump at the apex), with `AirControl`-scaled horizontal movement while airborne. The original
`Step(Vector3, ...) -> Vector3` overload is unchanged (horizontal-only, instant ground-clamp), so this stays an
additive minor bump. `MoveCommand` gains a `Jump` bit (the move wire format grows by one byte); `MoveTuning` gains
`Gravity`/`JumpSpeed`/`MaxFallSpeed`/`CoyoteTime`/`JumpBuffer`/`AirControl`/`GroundedEpsilon` (defaults
25 / 8 / 50 / 0.1 / 0.1 / 1 / 0.3; jump apex ~1.28 m). `KhaozEngine.NetWorld.PlayerMoveState` now wraps a
`MoveState` (still exposes `Position`, plus `VerticalVelocity` / `Grounded`). New replicated `MovementState`
component (type id 2) carries the vertical axis on the wire alongside `ReplicatedPosition`, so it survives a sharded
cell handoff (handoff transfers registered components) and reaches the client as the exact reconciliation basis.
`PlayerMoveSimulator` / `PlayerMovementSystem` step the vertical state (the play-area `WorldBounds` is now folded
into the step as an XZ clamp, so an airborne player is not snapped to the ground at the wall);
`WorldServer` / `ShardedWorldServer` write and replicate it (added at spawn); `WorldClient` reconciles
`y` / `VerticalVelocity` / `Grounded` alongside XZ - the full authoritative basis (`ReplicatedPosition` +
`MovementState`) is rebased and the unacked commands replay the same `Step`, so a jump in flight reconciles and
converges with no permanent desync and no snap once converged. `CharacterController3D` jumps on Space
(edge-triggered) and exposes `Grounded` / `VerticalVelocity`; `TerrainWalkSample` jumps, and you can run off the
rim/cliffs and fall. Out of scope (named): standing on / jumping onto props & buildings (sub-project B), building
interiors / ledges, step-height over terrain ledges, double/wall-jump, climbing, swimming, fall damage, and a full
physics engine (still a kinematic character controller). Additive; no new package; minor.

## 7.53.2

Security dependency fix, and a clean build is now warning-free (0 warnings). `KhaozEngine.WorldStore.Sqlite`
direct-references `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 (which bundles the patched SQLite 3.50.3) to override the
vulnerable 2.1.11 that `Microsoft.Data.Sqlite` 10.0.9 still pulls transitively: CVE-2025-6965 / NU1903, a
memory-corruption bug in SQLite before 3.50.2 (High, CVSS 7.2). Only the native binary moves; the managed
`SQLitePCLRaw.core` / `provider.e_sqlite3` stay at 2.1.11 (the versions Microsoft.Data.Sqlite is built against),
so `SqliteWorldStore` behaviour is unchanged. The pin is a temporary override, to be dropped once
Microsoft.Data.Sqlite references a patched SQLitePCLRaw of its own. (Complements 7.49.1, which stopped the
`KhaozEngine.Server` umbrella from bundling the SQLite backend at all.)

Separately, a clean (non-incremental) build surfaced a backlog of pre-existing warnings that incremental builds
hid; all are now fixed, none runtime-affecting. Test-only: CS8604 (clipboard null/empty-guard tests), CS8600
(font `out byte[]?`), and xUnit1031 (sharded-persistence tests made `async`/`await` instead of blocking
`GetAwaiter().GetResult()`). Engine code: CS0419 x32 overloaded-member `<see cref>`s disambiguated by appending
the intended overload's parameter list (generics in `{}`, `in` modifier kept); CS1734 x7 / CS1574 x3 paramref/cref
targets corrected (a real parameter, a property, or `<c>` where a method param was referenced from a type-level
doc); CS8600 x4 nullable reflection locals (`GetType`/`GetMethod`) in `ClipboardInterop`; and CA2255 on
`FpNative`'s deliberate `[ModuleInitializer]` (the native-library resolver registration) suppressed with
justification. No public API or behaviour change. Fix; no new package; patch.

## 7.53.1

Rigid glTF now honours node world transforms (glTF conformance). `KhaozEngine.Render3D.GltfLoader`'s rigid path
(`Load` / `LoadWithMaterial`, via `BuildRigid`) walked `root.LogicalMeshes` with raw `POSITION` and ignored the
scene graph, so a mesh positioned by its node (Blender exports, multi-piece / instanced kits) loaded mis-placed,
and a mesh instanced by several nodes loaded once at the local origin. `BuildRigid` now walks the scene nodes:
`POSITION` is baked by `node.WorldMatrix`, and `NORMAL` + `TANGENT.xyz` by the normal matrix (transpose of the
inverse upper-3x3, renormalized, `TANGENT.w` / bitangent sign preserved) so they stay correct under non-uniform
scale. A mesh referenced by N nodes emits N transformed copies; a mesh referenced by no node loads once at
identity; an exact-identity world matrix is a no-op fast path, so identity-node and pre-baked assets are
byte-identical to before. This matches the skinned path (`BuildSkinned`), which already baked `node.WorldMatrix`.
The kit-ingest `transform_apply` bake is no longer required for placement (still harmless). `LoadWithMaterial`'s
per-primitive material mapping is unchanged (base color still read per primitive, now aligned with the transformed
corners). Regression sweep: no GPU golden or automated test loads an affected asset, so nothing was re-baked. The
only assets with non-identity mesh nodes are the seven `TerrainWalkSample` props (translation + uniform scale, no
rotation), loaded through `PropLoader.Normalize`, which re-measures and renormalizes and is algebraically invariant
to translation + uniform scale, so the sample renders unchanged. Out of scope (named): morph targets, rigid-mesh
animation, camera/light nodes, the skinned path, the asset pipeline / scatter. Fix; no new package; patch.

## 7.53.0

Mesh-derived prop collider footprints (follow-up to 7.52.0 static world collision). New
`KhaozEngine.Render3D.PropFootprint.Derive(GltfMesh)` turns a `PropLoader`-normalized prop mesh into a correctly
sized `ColliderShape` with no hand-authored radii: a short prop (<= `PropFootprintOptions.SolidHeightMeters`,
default 2.5 m) uses its full XZ footprint, a tall prop uses only the bottom `TrunkHeightMeters` (default 1 m)
slice so a tree's wide canopy is excluded while a building's vertical walls still measure their full footprint,
and the footprint becomes a cylinder (radius = the larger half-extent, never under-covering) or an oriented box
when its long/short aspect exceeds `BoxAspectThreshold` (default 1.5). `PropFootprint.DeriveAll(AssetManifest)`
builds the `id -> ColliderShape` lookup `KhaozEngine.Terrain.PropColliders.FromScatter` takes (an explicit
`AssetEntry.Collider` still wins; otherwise the glTF is loaded headlessly and its footprint derived). This fixes
under-sized rock colliders in `TerrainWalkSample` (the boulders read ~1.0-1.24 m radius from the mesh, not the
0.6-0.7 m guessed before, so the capsule no longer sinks into them); the sample now derives every prop's
footprint from its mesh and drops the hand-authored manifest radii. Additive; no new package; minor.

## 7.52.0

Static world collision: kinematic capsule-vs-static-collider in the XZ plane, authoritative, the standard MMO
character-controller approach (NOT a physics engine). `KhaozEngine.Collision` gains the geometry: `BoxCollision`
(circle-vs-AABB, circle-vs-oriented-box, circle-vs-circle minimum-translation push-out), `ColliderShape` (an
unplaced cylinder/box prop footprint), `WorldCollider` (one placed static collider), and `WorldColliders` (a
`SpatialHashGrid`-backed queryable set with `Query(x,z,radius)` plus an iterate-and-slide `Resolve` that pushes a
capsule footprint out of overlaps, removing only the penetrating component so motion slides along surfaces).
`KhaozEngine.Locomotion.MoveTuning` gains `CapsuleRadius` (default 0.4, a defaulted 5th positional param so
existing call sites are unchanged), and `CharacterMovement.Step` takes an optional `WorldColliders?`; the push-out
runs inside the single movement step, so `CharacterController3D` (local + prediction), `PlayerMoveSimulator`,
`PlayerMovementSystem`, `WorldServer`, and `ShardedWorldServer` all resolve identically (server-authoritative +
client-prediction-consistent). A null or empty collider set leaves movement exactly as before. `AssetEntry` carries
an optional `Collider` (manifest `"collider": { "type": "cylinder", "radius" }` or `{ "type": "box", "halfW",
"halfD" }`), and `KhaozEngine.Terrain.PropColliders.FromScatter` builds a `WorldColliders` from deterministic
scatter placements (footprint per prop id, with a default-shape fallback) plus an explicit obstacle/building list -
streaming-consistent because it shares the coordinate-hash scatter. `KhaozEngine.Locomotion`, `KhaozEngine.NetWorld`,
`KhaozEngine.Terrain`, and `KhaozEngine.Render3D` now reference `KhaozEngine.Collision` (acyclic; Collision stays a
`System.Numerics` leaf). `TerrainWalkSample` makes the nearby scattered props solid and adds a hand-placed inn box
(12 m north). Headless tests cover the push-out math (depth + direction, glancing slide vs head-on stop), the
oriented-box-equals-AABB-at-zero-yaw identity, `WorldColliders` built-from-scatter matching the scatter + `Query`
neighbours + obstacle inclusion, movement push-out / cannot-enter / slide-along-wall, server-resolves-identically-
to-client, and no-collider-unchanged. Out of scope (named): dynamic/moving colliders, player-vs-player, vertical /
full-3D collision, gravity / jump / step-height, a general physics engine, navmesh. Additive; no new package; minor.

## 7.51.2

Perspective-correct toon outline plus two outline bug fixes in `KhaozEngine.Render3D` (`Internal/ShaderSources`
`EdgeFrag` + `BlitFrag`, `Rendering/PixelPostProcess`). The depth/normal edge outline was built for the
orthographic `IsoCamera3D`; the overworld's perspective `FollowCamera3D` is the first to drive it and exposed
three issues, all fixed here. The orthographic path is unchanged (the outline-on iso-camera goldens are
byte-identical).

- **Bug A (vertical flip on pass parity).** Each fullscreen post pass flips the image vertically, so the
  on-screen orientation depended on the parity of how many optional passes (quantize / outline / dither) ran -
  toggling `Outline` rendered the scene upside down. `BlitFrag` now cancels the flip based on the preceding
  post-pass count (new `Final.Params.z` = flipV, set in `PixelPostProcess.PrepareUniforms`), so every
  combination is upright. Outline-on (the default and the committed outline-on goldens) is byte-identical; the
  two outline-off goldens (`scene3d_normalmap`, `scene3d_skinned_normalmap`) were encoding the upside-down image
  and are re-baked upright on all three backends.
- **Bug B (dead normal-edge term on Metal).** `EdgeFrag` sampled its MRT inputs in the order Color, Depth,
  Normal, but the resource layout binds them Color, Normal, Depth. On Metal SPIRV-Cross assigns MSL texture
  indices by first-sample order, so Normal and Depth were swapped and the normal-crease term silently read depth
  data (the same class of bug already documented in `ModelFrag` for Albedo/NormalMap/Roughness). The shader now
  samples in binding order, so the normal term catches interior creases the depth term misses. D3D11/Vulkan bind
  by explicit decoration and were unaffected.
- **Fix C (perspective-correct depth threshold).** Under perspective the stored `z/w` is non-linear, so a fixed
  threshold over-detected near and under-detected far and the outline popped on zoom/distance. `EdgeFrag` now
  reconstructs view-space eye distance from the camera near/far (plumbed into the `Edge` UBO from
  `Scene3D`/`PixelPostProcess` via the new internal `OutlineMath`, which derives perspective-vs-ortho + near/far
  from the projection matrix, so no camera-interface change) and compares a second-difference (Laplacian) of
  linearized depth relative to depth - stable at any zoom and far less prone to grazing-plane flooding than a
  first difference. The orthographic branch keeps the original raw `abs(d - d0) > threshold` per-neighbour test,
  byte-identical.
- **Optional D (distance fade).** New `PixelPostProcessSettings.OutlineDistanceFade` (default off) +
  `OutlineFadeStart`/`OutlineFadeEnd` fade the outline out beyond a view-space distance (perspective only); off
  by default so the ortho path and existing look are unchanged.

New GPU golden `perspective_outline` (a perspective `FollowCamera3D` scene) locks the corrected stable outline +
the interior crease + the upright orientation; baked on Metal, D3D11, and Vulkan. New headless `OutlineMath`
tests cover the linearization (identity for ortho, near/far recovery, and a relative depth metric that stays
stable across two zoom levels where the raw metric collapses). Additive (one new public knob); no new package;
minor.
## 7.51.1

Slope-gate completion for the bounded play area (follow-up to 7.51.0): the rim is only a real border if it
can't be walked up. Patch; no API change.

- **`MoveTuning.Default.MaxSlopeRadians` lowered 50 deg -> 45 deg** (and the matching
  `CharacterController3D.MaxSlopeRadians` default), so a `RimFeature` mountain wall clearly exceeds the budget and
  is rejected while normal hills stay walkable. 50 deg was nearly a cliff and still walkable.
- **Slope gate wired everywhere movement runs:** `TerrainWalkSample` now passes `TerrainCollision.GroundNormal`
  as the `groundNormal` delegate in BOTH modes (not just bounded) - it previously passed only `GroundHeight`, so
  the gate was dormant and you could walk straight up cliffs. The reference `NetworkedWalkServer` (authoritative)
  and `NetworkedWalkSample` (client prediction) now pass `GroundNormal` too, so a hacked client still can't climb.
- Headless tests: the default budget blocks a 47 deg slope (it did not at 50 deg) and allows a gentle 30 deg
  slope; the authoritative server sim and the client prediction sim gate a steep slope identically every tick.

## 7.51.0

Bounded play area: the engine's first border/bounds mechanism, the missing capability for designed bounded
zones (a start town/lake ringed by impassable mountains with one road out). Two complementary pieces plus the
wiring that finally activates the dormant slope gate. Additive; no new package; minor.

- **`KhaozEngine.Terrain.RimFeature`** (+ `RimPass`): an `ITerrainFeature` that raises terrain into an enclosing
  wall around a bounded region - unchanged inside `InnerRadius`, a smoothstep ramp up to `WallHeight` by
  `OuterRadius` and held there beyond (a plateau, so you cannot see/walk past it), modulated by a coordinate-hash
  jagged crest (`Ruggedness`, reusing `TerrainNoise.Fbm`) so it reads as mountains not a smooth berm. `RimPass`
  (heading + half-width + falloff) cuts a corridor through the wall on its heading side (the road out). MVP is
  circular; `Apply` is shaped around a "distance to the play-area boundary" (here distance from `Center`) so a
  later rect/polygon variant swaps only the distance metric and reuses the ramp. Pure in (x, z) like every feature.
- **`KhaozEngine.NetWorld.WorldBounds`** (abstract `Contains`/`Clamp`) + **`CircleBounds`** + **`RectBounds`**: the
  authoritative play-area shape. `Clamp` returns the nearest in-bounds point - a no-op inside (idempotent) and a
  projection onto the boundary outside, which yields clamp-and-slide when applied every tick (the tangential part
  of a blocked move survives, so movement stays smooth instead of hard-stopping).
- **Movement clamp**: `PlayerMoveSimulator` and `PlayerMovementSystem` take a nullable `WorldBounds` and clamp the
  new XZ (re-deriving Y from the ground) after `CharacterMovement.Step`; threaded through `WorldServer`,
  `ShardedWorldServer` (so the clamp is authoritative across the cell grid), and `WorldClient` (so client
  prediction clamps identically and reconciliation stays clean at the wall). Null bounds = today's unbounded
  behaviour, unchanged.
- **Slope-gate wiring**: `TerrainCollision.GroundNormal(x, z)` is exposed (= `TerrainField.SampleNormal`) so it can
  be passed as the `groundNormal` slope-gate delegate that `CharacterMovement.Step` already supported but nothing
  passed before. With it wired, terrain steeper than `MoveTuning.MaxSlopeRadians` (default 50 deg) blocks the step,
  so the rim wall cannot be climbed; the same delegate runs on the server sim (authoritative - a hacked client
  still cannot climb the rim) and in client prediction.
- **`TerrainPresets.BoundedClearing()`**: the first ready-made bounded zone - a single gentle meadow ringed by a
  `RimFeature` mountain wall with one +Z pass and a carved lake. `TerrainWalkSample bounded` (a `bounded` arg)
  walks it with the slope gate wired, so you are held inside by the mountains and the +Z pass is the one way out.
- Headless tests: `RimFeature` (unchanged inside, ramps to ~`WallHeight`, jagged-but-bounded crest, pass corridor
  open + heading-side-only, deterministic, composes with Lake/Flatten, rim unwalkable while the pass stays
  walkable); `WorldBounds` (`Contains`/`Clamp` for circle + rect, outside clamps onto the boundary, idempotent
  inside, rect slide); movement integration (player held inside a circle bound, slide along a rect edge, slope gate
  blocks too-steep ground, bounded prediction reconciles against a bounded server with no persistent error,
  `WorldServer` + `ShardedWorldServer` both hold a player inside).

## 7.50.0

Multi-cell server sharding, overworld render-scale sub-project 6b (finishes "6" with 6a streaming). The
authoritative overworld now runs across a grid of cells with seamless cross-cell ghosting + exactly-once
handoff, so the world holds many players / a huge area without one giant `World`. This is integration, not new
netcode: it wires the existing movement stack onto the shipped `KhaozEngine.Sharding` `ShardHost`.

- **`KhaozEngine.NetWorld.ShardedWorldServer`** (+ `ShardedWorldServerConfig`): the single-`World` `WorldServer`
  movement stack run across a `ShardHost` cell grid. Each tick routes every client's `MoveCommand` to the cell
  that **owns** its player (`TryGetOwner`), steps every cell's new `PlayerMovementSystem` (`CharacterMovement.Step`,
  ground-clamped) via `ShardHost.Tick` (fanned across the opt-in scheduler - cells are disjoint worlds, so the
  result is scheduler-independent), transfers authority for boundary crossers exactly-once
  (`ShardHost.ProcessHandoffs`, the player's `NetId` stable across the migrate), refreshes border ghosts
  (`ShardHost.SyncGhosts`), then serves each client its single **home-cell** area-of-interest snapshot (owned +
  ghosts) framed with the existing `[localNetId][ack]` header. `Scheduler` is settable (default single-threaded;
  pass a `ThreadPoolJobScheduler` to tick cells across cores, deterministically).
- **`WorldClient` and `MoveProtocol` are unchanged.** A player's `NetId` is stable across handoff, so the client's
  replication view + prediction continue without a respawn or hitch; the client has no cell concept. The
  single-`World` `WorldServer` path is intact.
- **`PlayerMovementSystem`** (ECS `ISystem`): advances each owned player's `ReplicatedPosition` via the shared
  `CharacterMovement.Step`; skips read-only `Ghost`s and in-flight `Migrating` entities (the owner is the sole
  simulator). Stateless, one instance shared across cells.
- **`PendingMove`** component: the per-tick command a cell's `PlayerMovementSystem` applies to an owned player
  (server-local, not replication-registered, not carried across a handoff - the post-handoff cell re-routes).
- **`IWorldPersistenceHost`**: extracted from `WorldServer` so the shipped `WorldPersistence` (load-on-join,
  save-on-leave, periodic dirty snapshot, keyed `player:{accountId}`) drives both the single-`World` `WorldServer`
  and the new `ShardedWorldServer` unchanged. Player-keyed and cell-agnostic: a loaded player spawns at its saved
  position in whatever cell contains it (the next handoff pass relocates the entity there). `WorldServer` now
  implements `IWorldPersistenceHost` (no behaviour change).
- `KhaozEngine.NetWorld` now references `KhaozEngine.Sharding` (acyclic; both are already in the `Server` umbrella,
  so a sharded game server is one umbrella reference plus its `WorldStore.*` backend). No new package.
- Demo: `NetworkedWalkServer` now drives a multi-cell `ShardedWorldServer` (cellSize 60 = one terrain chunk) over
  `TerrainPresets.Clearing()`, persisting via SQLite; `NetworkedWalkSample` (client) is unchanged.
- Headless tests (over `InProcessCellLink` + `LoopbackTransport`/in-memory hub): handoff exactly-once (NetId
  stable, position continuous, no dup/drop), ghosting (two adjacent-border players each see the other; a far
  player does not), AoI = owned + in-range ghosts, movement continuity through a handoff with the real unchanged
  `WorldClient` (no prediction snap), persistence across cells (load-on-join into the saved position's cell +
  restart-survival), and multi-cell determinism (single-threaded `ShardHost.Tick` == `ThreadPoolJobScheduler`).

Additive API in existing packages; no new package; minor.

## 7.49.1

Packaging fix (security): the `KhaozEngine.Server` umbrella no longer bundles the `WorldStore.Sqlite` /
`WorldStore.SqlServer` backends. They had been transitive references on the umbrella, so every Server consumer
pulled `Microsoft.Data.Sqlite` -> SQLitePCLRaw (high-sev `NU1903` / `GHSA-2m69-gcr7-jv3q`) +
`Microsoft.Data.SqlClient`, even when using one backend or none, which defeated the engine's own opt-in-sibling
-backend design. The umbrella now carries only the dependency-free `KhaozEngine.WorldStore` core (`IWorldStore`
+ `InMemoryWorldStore`); consumers add the backend package they want (`KhaozEngine.WorldStore.Sqlite` or
`.SqlServer`) explicitly. Non-breaking in practice: the demo `NetworkedWalkServer` references `WorldStore.Sqlite`
directly and Ruinborne references `WorldStore.SqlServer` directly, so nothing relied on the umbrella for a
backend. A project referencing only `KhaozEngine.Server` no longer pulls SQLitePCLRaw / Microsoft.Data.Sqlite.
Patch.

## 7.49.0

Persistent world store: the authoritative world now survives a server restart (it was in-memory only). Two new
opt-in `IWorldStore` backend packages (each pulls its own ADO.NET provider without touching the dependency-free
`KhaozEngine.WorldStore` core, same pattern as `Netcode.LiteNetLib`), plus a backend-agnostic save/load
orchestration in `KhaozEngine.NetWorld`. Two new packages; additive; minor.

- NEW `KhaozEngine.WorldStore.Sqlite` (`Server` umbrella): `SqliteWorldStore : IWorldStore` over
  `Microsoft.Data.Sqlite`. The embedded, zero-infra dev/test + single-node backend (and what keeps persistence
  headless-testable). One `world_store(key, data, updated_at)` table bootstrapped on construction, upsert via
  `INSERT ... ON CONFLICT(key) DO UPDATE`, raw parameterized async ADO.NET, no EF/ORM. Holds one connection,
  serializes ops behind a semaphore. `SqliteWorldStoreOptions` (or a raw connection-string ctor) injects the
  connection string; `IDisposable`.
- NEW `KhaozEngine.WorldStore.SqlServer` (`Server` umbrella): `SqlServerWorldStore : IWorldStore` over
  `Microsoft.Data.SqlClient` (production = Azure SQL). Same contract; `world_store([key], data, updated_at)`
  table, `MERGE ... WITH (HOLDLOCK)` upsert, short-lived pooled connection per op, raw parameterized async
  ADO.NET, no EF/ORM. `SqlServerWorldStoreOptions` / raw connection-string ctor.
- NEW `KhaozEngine.NetWorld.WorldPersistence` (+ `WorldPersistenceConfig`, `PlayerRecord`): wires an
  `IWorldStore` into the `WorldServer` lifecycle - load-on-join (spawn at the saved position, default if absent),
  save-on-leave, and a periodic snapshot (`SaveIntervalSeconds`, default 30) of players whose state changed since
  their last save. Keys `player:{accountId}`. Backend-agnostic (only `IWorldStore` + `KhaozEngine.Serialization`).
  Async loads are applied to the server on the server thread inside `Update(dt)` (never from a background
  continuation), so a genuinely-async backend can't race the tick loop; `FlushAsync()` reaches a quiescent,
  fully-persisted point (shutdown / tests). `PlayerRecord` is a forward-tolerant JSON DTO (unknown / missing
  fields ignored), so adding fields later never breaks an old save.
- `WorldServer` gains a persistence seam (no new wire protocol): `PlayerJoined`/`PlayerLeaving` events,
  `TryGetAccountId`/`TryGetPlayerState`/`JoinedSlots`/`SetPlayerState`. The account id derives from the connect
  token the client already presents in its Hello, now surfaced on `ServerSessionEvent.Joined` (carried in `Data`)
  and `NetServer` (empty token -> `guest:{slot}` fallback). `KhaozEngine.NetWorld` now also depends on
  `KhaozEngine.WorldStore` + `KhaozEngine.Serialization`.
- `NetworkedWalkServer` persists players via `SqliteWorldStore` + `WorldPersistence` (optional `[dbPath]` arg,
  flush on Ctrl+C); `NetworkedWalkSample` sends a stable account token (optional third arg, default `player1`),
  so walking somewhere, disconnecting, and reconnecting (or restarting the server) restores position.
- Tests: one shared `IWorldStore` conformance suite (save/load round-trip, overwrite, load-absent -> null,
  delete present/absent, exists, key isolation, byte-exactness, basic concurrency) run against `InMemoryWorldStore`
  + `SqliteWorldStore` always, and `SqlServerWorldStore` gated behind `KE_SQLSERVER_TEST_CONNSTRING` (skipped in
  CI). Plus `WorldPersistence` load-on-join / save-on-leave / periodic-snapshot tests and the restart-survival
  proof (reopen a fresh store + server on the same SQLite file -> the player is restored).

## 7.48.0

Client world streaming, overworld render-scale sub-project 6a: the world is now effectively endless. New
streaming layer in the existing `KhaozEngine.Terrain.Render3D` package keeps a ring of terrain chunks (+ their
deterministic props) loaded around the player; `TerrainWalkSample` walks an endless world instead of a fixed
7x7 grid. Server stays single-`World` (multi-cell sharding is 6b). Additive in one existing package; no new
package; minor.

- **`ChunkCoord` + `ChunkGrid` (new, `KhaozEngine.Terrain.Render3D`)** - the streaming grid. `ChunkCoord(int X,
  int Z)` is the integer index of a square chunk; `ChunkGrid` maps a coord to and from world space for a chunk
  size: `CoordOf(worldX, worldZ, size)` (floors toward negative infinity, matching Sharding's `CellCoord`),
  `CenterOf`, `RegionOf` (a `TerrainChunkRegion`), and `AreaOf` (a half-open `RectArea` so adjacent chunks tile
  `PropScatter` exactly once). One source of truth shared by the streamer, the sink, and the tests.
- **`IChunkSink` (new, `KhaozEngine.Terrain.Render3D`)** - the load/unload callback seam the streamer drives:
  `object Load(ChunkCoord coord, int lod)` (returns an opaque handle), `void ReLod(ChunkCoord coord, object
  handle, int lod)`, `void Unload(ChunkCoord coord, object handle)`. All GPU work lives behind it, so the
  streamer is headless-testable with a fake sink.
- **`StreamerConfig` (new, `KhaozEngine.Terrain.Render3D`)** - `(int LoadRadius, int UnloadRadius, int
  MaxLoadsPerFrame, float ChunkSize)` in chunk units, with `StreamerConfig.Default` = LoadRadius 4 (~240 m view) /
  UnloadRadius 6 (2-chunk hysteresis band) / MaxLoadsPerFrame 3 / 60 m chunks. `UnloadRadius > LoadRadius` is the
  hysteresis that stops churn when the player oscillates across a chunk boundary.
- **`TerrainStreamer` (new, `KhaozEngine.Terrain.Render3D`)** - keeps the world loaded in a ring around the
  player. `Update(Vector3 playerPos, float dt)`: (1) unloads chunks beyond `UnloadRadius` (Euclidean
  chunk-distance, immediate), (2) loads chunks inside the `LoadRadius` disk not yet loaded, (3) re-LODs loaded
  chunks whose `TerrainLod.PickLod(metre-distance-to-center)` tier changed, processing at most `MaxLoadsPerFrame`
  load/re-LOD ops per update (nearest first) so a build burst never hitches. `Loaded` exposes the loaded set;
  `LodOf(coord)` the current tier. Pure bookkeeping (no GPU, no field), fully headless-tested via a fake sink:
  loaded set equals the expected disk, moving loads/unloads the right chunks, oscillation does not churn,
  requested LOD equals `PickLod(distance)` with a tier crossing yielding a ReLod, at most `MaxLoadsPerFrame` ops
  per update with the backlog draining over frames, and nearest-first ordering.
- **`Scene3DChunkSink : IChunkSink` (new, `KhaozEngine.Terrain.Render3D`)** - the production sink (ships in the
  package so every game gets streaming for free). `Load` builds the chunk mesh at the requested LOD
  (`TerrainChunkBuilder`) + scatters its props (`PropScatter.Generate` over the chunk's `RectArea`) and uploads
  the mesh; `ReLod` rebuilds the mesh at the new tier in place (props are LOD-independent, kept); `Unload` frees
  the mesh; `Draw(Vector3 focus)` queues every loaded chunk mesh + its in-range props (XZ-culled to a
  `propDrawRadius`). The per-chunk prop scatter matches `PropScatter.Generate` for the chunk area (headless-tested).
- **`TerrainWalkSample` now streams an endless world** - the fixed 7x7 chunk grid is replaced by a
  `TerrainStreamer` + `Scene3DChunkSink` driven by the player position; the full initial ring is primed at load
  time, then `OnUpdate` amortizes streaming so a brisk walk never outruns the load budget. Terrain and props are
  computed per-area from the seed, so this composes with the networked client unchanged (each client streams
  locally; nothing about the world is replicated).

## 7.47.0

Networked overworld, the fifth overworld render-scale sub-project: the 3D walkable client and the shipped
authoritative server stack meet, so two clients see each other walking the same terrain. Two new packages
(`Locomotion`, `NetWorld`) + a controller refactor + two demo exes; additive; minor.

- **`KhaozEngine.Locomotion` (new render-free leaf, `Foundation` umbrella; deps Primitives only)** - the shared
  movement core. `CharacterMovement.Step(Vector3 position, in MoveCommand cmd, float dt, Func<float,float,float>
  groundHeight, in MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null)` is a pure XZ move:
  resolves a camera-relative `MoveCommand` (`Vector2 Move` (X = right/strafe, Y = forward) + `bool Run` +
  `float CameraYaw`) into a world direction, normalizes diagonals, applies walk/run speed over `dt`, optionally
  rejects a step onto too-steep ground, and clamps Y onto the ground delegate + the capsule half-height.
  `MoveTuning(WalkSpeed, RunSpeed, CapsuleHalfHeight, MaxSlopeRadians)` with `MoveTuning.Default` (walk 3, run 6,
  half-height 0.9, ~50 deg) is the one source of truth. No input/render/netcode dependency.
- **`CharacterController3D` (`KhaozEngine.Game.Render3D`) refactored to wrap `CharacterMovement.Step`** - the
  walkable-slice controller now maps the input snapshot to a `MoveCommand` + `MoveTuning` from its public fields
  and delegates to the shared core, so local feel and networked feel are the same code. Public API, behaviour,
  and the existing tests are unchanged; it gains a `KhaozEngine.Locomotion` reference.
- **`KhaozEngine.NetWorld` (new render-free package, `Server` umbrella; deps Locomotion/Netcode/Replication/Ecs)**
  - the networked-world layer:
  - `PlayerMoveState : IPredictedState<PlayerMoveState>` (a `Vector3 Position`; the interface is satisfied over
    its XZ plane) and `ReplicatedPosition : IComponent` (the one replicated, interpolatable component).
  - `PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>` runs `CharacterMovement.Step` both
    server-authoritatively and inside client prediction (identical code, in lockstep).
  - `WorldServer` - a single-`World` authoritative movement server over an injected `INetTransport`: a
    `NetServer`/`AllowAllAuthenticator` session spawns one player entity per connection, `Poll` ingests joins/
    leaves and decoded `MoveCommand`s into a `RemoteCommandQueue`, and `Tick(dt)` advances each player, then
    serves each client a per-area-of-interest snapshot (`SnapshotWriter.WriteFiltered` over an `InterestGrid`)
    framed with that client's net id + last-acked move seq. The terrain is an injected ground delegate (no
    terrain dependency).
  - `WorldClient` - wraps `NetClient` + `ClientReplicationView` + `ClientPrediction`. `Poll()` applies AoI
    snapshots and reconciles the local avatar against the authoritative basis; `SendInput(cmd)` predicts one
    tick and transmits; `Snapshot()` returns `IReadOnlyList<EntityRenderState>` (`{ NetId Id; Vector3 Position;
    bool IsLocal; }`) - local predicted/reconciled, remotes replicated.
  - `MoveProtocol` - the shared wire codec: the `ReplicationRegistry`, the move encoding
    `[seq][move.x][move.y][run][cameraYaw]`, and the server frame `[localNetId][ackSeq][snapshot]`.
  - Design note: `PlayerMoveState`/`IPredictedState` live in `NetWorld` (not `Locomotion`) and
    `CharacterMovement.Step` takes/returns `Vector3`, so the movement core and the local controller stay
    netcode-free and the `Foundation` umbrella keeps its no-networking guarantee. The single-`World` slice
    serves full-state per-AoI snapshots (the shipped `MmoServer.SnapshotForClient` pattern); the
    `ServerReplicator` baseline/delta path folds in with multi-cell sharding + streaming later.
- **Demos (`IsPackable=false`)** - `NetworkedWalkServer` (a headless `WorldServer` on a `FixedTickHost` over
  LiteNetLib UDP, terrain `TerrainPresets.Clearing()`) and `NetworkedWalkSample` (a windowed `--connect [host]
  [port]` client running a `WorldClient`, rendering a capsule per `EntityRenderState` - local predicted with the
  follow camera on it, remotes from replicated positions - over the same terrain + deterministic prop scatter).
  Props are not replicated; each client scatters them from the seed. Run the server, then two clients on
  localhost to see two players.
- **Tests (headless, over `LoopbackTransport` / an in-memory hub; one live-socket smoke)** - `CharacterMovement`
  step semantics; `PlayerMoveSimulator` tick + ground-clamp; a single-client round-trip (a client's
  `MoveCommand` moves its server entity and returns via replication); two clients each seeing the other move;
  `ClientPrediction` reconciling an injected misprediction (local converges to the server basis; unacked
  commands replay).

Spec: `docs/superpowers/specs/2026-06-27-networked-overworld-design.md`.

## 7.46.0

Prop scatter + asset pipeline, the fourth overworld render-scale sub-project: the walkable terrain is now
forested. Additive public API in three existing packages (Render3D, Terrain, Terrain.Render3D); no new
package; minor.

- **`AssetManifest` + `AssetEntry` (`KhaozEngine.Render3D`)** - parses a prop-kit manifest, a JSON
  `{ "props": [ { id, file, heightMeters, source, license } ] }`. `Parse(json, baseDir?)` / `Load(path)`
  resolve a relative `file` against the manifest directory; the manifest is also the CC0 provenance record.
  Malformed JSON / a missing `props` array / an entry missing `id` or `file` throws
  `InvalidOperationException` with context.
- **`PropLoader` + `PropValidation` (`KhaozEngine.Render3D`)** - `LoadProp(entry)` loads the (decompressed)
  glTF via `GltfLoader`, then `Normalize` scales the mesh uniformly to its declared `heightMeters`, drops the
  origin to the base (feet on the ground), and re-centres X/Z on the origin. Validation throws loudly on an
  implausible declared-vs-actual size (the 1.8 m human-scale guard): a declared height outside
  `PropValidation.MinHeightMeters..MaxHeightMeters`, or an implied raw-to-declared scale outside
  `MinScale..MaxScale` (the asset is in the wrong units). The engine still has no meshopt decoder - kit assets
  are decompressed offline (`gltf-transform`) as an ingest step.
- **`PropScatter` + `PropPlacement` + `ScatterConfig` + `BiomeScatterRule` + `PropKind` + `RectArea`
  (`KhaozEngine.Terrain`, render-free leaf)** - `PropScatter.Generate(field, config, area)` returns
  deterministic coordinate-hash placements (reusing `TerrainNoise.Hash2`): a jittered grid with per-biome
  density + weighted kind mix, exclusions (below `WaterLevel`, inside a clearing radius, above a height cap),
  and per-instance scale/yaw/variant from independent hashes. `Y` comes from `field.SampleHeight`. A placement
  depends only on `(cell, seed)`, so generating over an area equals the union over its tiles (streaming-ready,
  half-open `RectArea` on cell centres). `ScatterConfig.ForestRing()` reproduces the greybox forest ring
  (`tools/blender/make_clearing_greybox.py`: cell 4.5 m, clearing radius 26 m, keep 0.55, off-mountain at
  height > 6 m, scale 0.8..1.35).
- **`PropRenderer.Queue` + `Scene3D.DrawProps` (`KhaozEngine.Terrain.Render3D`)** - given placements + a
  `id -> MeshHandle` map + a focus point + a draw radius, queues `SceneInstances.Add` (scale + yaw +
  translation) for placements within the horizontal (XZ) radius and distance-culls the rest, skipping unknown
  ids. Pure use of the existing instancing path, so an N-tree forest batches into a handful of draws (one per
  kit mesh). `DrawProps` is the Scene3D convenience; `Queue` is the headless-testable core.
- **`TerrainWalkSample`** - loads a small committed CC0 Quaternius nature kit (3 pine / 2 oak / 2 rock,
  decompressed to plain glTF) through the pipeline, scatters `ScatterConfig.ForestRing()` over the clearing,
  and draws the forest instanced around the player (distance-culled). Walk through it.
- **Ingest note** - kit glTF is meshopt-compressed; the engine loads only plain glTF 2.0. Decompress offline:
  `npx --yes @gltf-transform/cli@latest cp <in>.glb <out>.glb` (and `dequantize` / texture-flatten as needed).
  See `docs/USING-KHAOZENGINE.md` (Prop scatter + asset pipeline) and the kit `CREDITS.md`.
- Out of scope (named so they are not forgotten): a meshopt decoder, mesh-LOD/impostors, PBR splat textures,
  prop/obstacle collision, chunk streaming, animated props. All later sub-projects.

## 7.45.0

Follow-camera tuning (drag direction + terrain ground-clamp), correcting two issues found playtesting the
7.44.0 walkable slice. Behaviour fixes to the new-in-7.44.0 follow camera plus additive fields; minor.

- **`FollowCameraController.InvertX` / `InvertY` (`KhaozEngine.Render3D`)** - per-axis drag inversion, default
  false. The default orbit mapping is now `Yaw -= dx`, `Pitch += dy` (was `Yaw += dx`, `Pitch -= dy`, which felt
  inverted on both axes); setting a flag restores the old direction for that axis (e.g. an "invert axis" setting).
- **`FollowCamera3D.GroundHeight` / `GroundClearance` (`KhaozEngine.Render3D`)** - optional ground-height delegate
  + clearance that keeps the eye above the ground at its own XZ, so the camera no longer sinks through the floor
  when the target is in a dip (the surrounding terrain rises behind it). Terrain-agnostic (a plain delegate, the
  same pattern `CharacterController3D` uses); null (default) leaves the eye purely geometric, so existing behaviour
  is unchanged. `TerrainWalkSample` wires it to `TerrainCollision.GroundHeight`.

## 7.44.0

Walkable overworld slice: sub-project 2 of the overworld render-scale track, making the terrain shipped in
7.43.0 walkable in a window. Additive, minor (new public API in two existing packages, `KhaozEngine.Render3D`
and `KhaozEngine.Game.Render3D`; no new package). The locked design: a **third-person follow camera** (orbit
behind a moving target, mouse-look) built as a sibling of `IsoCamera3D`, a **greybox capsule character** (the
engine has no glTF keyframe-clip playback yet, so the capsule is static), and **local/direct movement** over a
**fixed chunk grid** (no netcode, no streaming). Camera + character are the reusable basis for the later
world-client glue.

- **New `FollowCamera3D` (`KhaozEngine.Render3D`)** - a perspective third-person camera that orbits behind a
  `Target` at a clamped `Pitch`/`Distance` and always looks at the target. Sibling of `IsoCamera3D`: same Y-up
  right-handed convention, same `Eye`/`Forward`/`ScreenToRay`/`ScreenToGround`, implements `IIsoCamera3D`. Perspective
  (not orthographic) so scroll-zoom-via-distance reads naturally. Tuning is exposed as fields (`MinPitch`/`MaxPitch`,
  `MinDistance`/`MaxDistance`, `HeightOffset`, `FieldOfView`, near/far).
- **New `FollowCameraController` (`KhaozEngine.Render3D`)** - drives a `FollowCamera3D` from the immutable
  `InputState` snapshot: hold the `OrbitButton` and drag to swing yaw/pitch, scroll to zoom. Touches no input
  statics (headless-testable). Sensitivity (`OrbitYawSpeed`/`OrbitPitchSpeed`/`ZoomStep`) and the orbit button are
  fields. Mirrors `IsoCameraController`.
- **New `Scene3D.CameraOverride` (`KhaozEngine.Render3D`)** - optional `IIsoCamera3D` that overrides the built-in
  iso `Camera` for the render path, so a sibling camera (e.g. `FollowCamera3D`) can drive the view/projection. Null
  by default (behaviour unchanged). The caller owns the override's aspect ratio (set it from the framebuffer each
  frame); the built-in `Camera`'s aspect is still maintained by the scene.
- **New `CharacterController3D` (`KhaozEngine.Game.Render3D`)** - terrain-agnostic third-person locomotion: WASD
  moves camera-relative on the XZ plane (normalized diagonals, left/right shift to run), and each frame the Y is
  clamped onto a caller-supplied ground-height delegate plus a capsule half-height, with an optional ground-normal
  slope gate that rejects steps onto too-steep ground. References no terrain package (ground supplied as delegates)
  and does no physics beyond the ground-clamp (no jump/gravity). Speeds, half-height, and max slope are fields.
- **New `TerrainWalkSample`** (`IsPackable=false`, not published) - a windowed sample that walks a 1.8 m greybox
  capsule (from `MeshPrimitives.Capsule`) over a fixed 7x7 grid of `TerrainPresets.Clearing()` chunks
  (`Scene3D.LoadTerrainChunk`/`DrawTerrainChunk`) with the follow camera, ground-clamped through `TerrainCollision`.
  Controls: WASD move, mouse-drag orbit, scroll zoom, shift run, Esc quit.

Out of scope (later sub-projects): animation/walk-cycle (needs a glTF animation-clip feature first), netcode-driven
movement, chunk streaming, prop/obstacle collision, jump/gravity/physics beyond ground-clamp.

## 7.43.0

Terrain system: the first sub-project of the overworld render-scale track. Two new packages give the engine a
deterministic analytic ground field and a chunked-LOD mesh builder for it, modeled on the world-of-claudecraft
`terrainHeight`/`baseHeight`/`shapeAt` pipeline. The locked design decisions: an **analytic field** (height comes
from a deterministic `SampleHeight(x, z, seed)` evaluated at runtime, not baked heightmaps, so there are no terrain
assets to stream and server/client agree automatically), **authoritative server + visual client** (plain `float`,
no `DeterministicFp`; the replication layer corrects the invisible cross-platform float drift), and **stateless
coordinate-hash noise** (height at a point depends only on `(x, z, seed)`, never on which neighbour cells are
loaded, which the sharded-streaming world needs). The mesh builder stays CPU-only and headless-testable: it asserts
on produced vertex data, never a GPU device. Additive, minor.

- **New package `KhaozEngine.Terrain`** (render-free leaf, in the `Foundation` umbrella; references only
  `Primitives`, never `Render3D`). `TerrainField` is the single source of truth for ground height:
  `SampleHeight(x, z)` folds three layers in order - biome shape (`BiomeBand`s smoothstep-blended along Z give
  designed meadow/mountain regions), base fractal coordinate-hash noise (`TerrainNoise.Fbm`/`Turbulence` over a
  stateless integer-mix `Hash2`/`ValueNoise`), then an ordered `ITerrainFeature[]` (`LakeFeature` carves a basin,
  `RidgeFeature` raises a gaussian wall pierced by a pass, `FlattenFeature` levels a hub). Plus `SampleNormal`
  (central finite difference), `SampleBiome`, `WaterLevel`, a `TerrainConfig`, the `TerrainPresets.Clearing()`
  greybox-parity preset, and `TerrainCollision` (`GroundHeight` + slope `IsWalkable`) for the sim to keep entities
  on the ground. `TerrainCollision` lives here (not in `Collision`) so the dependency edge stays
  `Terrain -> Primitives` and the field is not dragged into 2D collision consumers.
- **New package `KhaozEngine.Terrain.Render3D`** (companion, in the `Game3D` umbrella; references the leaf +
  `Render3D`). `TerrainChunkBuilder.Build(field, region, lod)` meshes one finite chunk off the analytic field: a
  `(res+1)^2` grid of field-sampled vertices into a Render3D `GltfMesh`, ~0.3 m edge skirts that hide cracks where
  a dense chunk meets a coarse neighbour, a per-vertex `TerrainSplatWeights` set (grass/dirt/rock/sand/snow, baked
  from height + slope, plumbed for the later PBR splat-TEXTURE upgrade) rendered now as a height/slope vertex-colour
  ramp (`TerrainRamp`), and a `TerrainChunkBounds` AABB for frustum culling. `TerrainLod.PickLod(distance)` maps
  camera distance to one of three tiers (dense near, coarse far). `Scene3D.LoadTerrainChunk`/`DrawTerrainChunk`
  extensions upload and draw a built chunk.
- **Scope:** this slice is the terrain foundation only. World streaming (which chunks load/unload and when), prop
  scatter, PBR splat textures, the character controller, and a water shader are later sub-projects of the overworld
  program and are deliberately out of scope here.
- **Umbrellas:** `KhaozEngine.Foundation` now includes `Terrain`; `KhaozEngine.Game3D` now includes
  `Terrain.Render3D`.

## 7.42.0

Parallel `ForEach`: an ECS `World` can now fan a hot query's per-entity work across CPU cores - the entities axis,
which breaks the single-hot-cell ceiling the cell axis (7.41.0) leaves. Archetype rows are independent memory, so a
per-row-pure action over disjoint row ranges is race-free and order-independent: the parallel result is bit-identical
to the sequential `ForEach` regardless of how rows partition. Opt-in and default-inline, so the existing `ForEach`
path is byte-unchanged and lockstep / single-player sims are untouched. Layer 2 of the parallel-job-system program
(`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`), built on the jobs-0 benchmark and the 7.41.0
`IJobScheduler` seam. Additive, minor.

- **Ecs: `World.ParallelForEach<...>` overloads (arity 1-8).** Mirror `ForEach<...>`, partitioning each matched
  archetype's row range `[0,Count)` into a few contiguous chunks per core and fanning them across an `IJobScheduler`
  (trailing optional arg; default an inline `SingleThreadedJobScheduler`, so omitting it is identical to `ForEach`).
  Chunk boundaries depend only on row count and core count, never the scheduler, so inline and thread-pool produce
  the same partition. The action must be **per-row-pure** (touch only the `ref` components handed in for the current
  entity). `KhaozEngine.Ecs` now references `KhaozEngine.Simulation` for the seam (a zero-dependency leaf, so the
  reference stays acyclic).
- **Ecs: a read/write access-declaration model (`AccessSet` / `Access`).** `Access.Read<T>()` / `Access.Write<T>()`
  build an immutable `AccessSet` describing a unit of work's component reads vs writes; `a.ConflictsWith(b)` is true
  iff one writes a type the other touches (write-write or read-write hazard; two readers never conflict). Keyed by
  `Type` so a declaration is world-portable; write beats read for the same type. This is the shared vocabulary the
  system scheduler (layer 3) reuses to decide which systems may overlap.
- **Ecs: a debug hazard guard.** While a `ParallelForEach` section runs, any reentrant world call from a worker
  action - a structural change (`Spawn`/`Despawn`/`Set`/`Add`/`Remove`), a component read/write through the world
  API (`Get`/`TryGet`), or a nested `ForEach`/`ParallelForEach` - throws `ParallelAccessViolationException` (it
  breaks per-row-purity). On by default (`World.ParallelHazardChecks`, one bool check per world call, negligible
  outside a section); a shipping server may disable it for a proven-pure hot loop.
- **Ecs: a thread-safe deferred-structural-change path.** Buffered `ParallelForEach<...>(RefBufferAction<...>)`
  overloads (arity 1-4) hand each worker chunk its own `EntityCommandBuffer`; the buffers are played back in
  archetype-then-chunk (row) order after the section, so recorded `Create`/`Set`/etc. land identically to a
  sequential `ForEach` recording into one buffer - and deterministically run to run. Inline structural changes inside
  a parallel action stay forbidden (the hazard guard catches them); this is the supported way to mutate structure.
- **Benchmark: an entities-axis sweep.** New `EntitiesAxisBenchmark`; the exe prints a one-hot-`World` section
  sweeping per-row work, `ForEach` vs `ParallelForEach`. It shows the honest crossover: trivial per-row work is
  fork/join-bound (parallel < 1x), but as the per-row compute grows toward a real hot system it amortizes the
  fork/join and scales toward ~P× (on a 12-core box: ~0.3x at work=1, ~5x at work=32, ~7.5x at work=512).

## 7.41.0

Parallel cell ticks: a `ShardHost` can now tick its independent cells across CPU cores - the biggest MMO-shape
server-scale win for the least risk, since cells are disjoint `World`s and the fan-out is hazard-free. Opt-in and
default-off, so the single-threaded path is byte-unchanged and lockstep / single-player sims are untouched. Layer 1
of the parallel-job-system program (`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`), built on
the jobs-0 benchmark. Additive, minor.

- **Simulation: a worker-pool seam (`IJobScheduler`).** New `IJobScheduler.For(int count, Action<int> body)` runs
  N independent jobs and blocks until all complete. `SingleThreadedJobScheduler` (the default everywhere) runs them
  inline in index order - deterministic, allocation-free, the single-threaded baseline a parallel result is asserted
  equal to. `ThreadPoolJobScheduler` (optional `maxDegreeOfParallelism`) fans them across the BCL thread pool via
  `Parallel.For`, with a fast path for `count <= 1`. This is the one seam layers 2-3 (parallel `ForEach`, the system
  scheduler) will reuse.
- **Sharding: `ShardHost` ticks cells across the scheduler.** `ShardHost` gains a settable `Scheduler` property and a
  trailing optional ctor arg, defaulting to an inline `SingleThreadedJobScheduler`. `Tick` fans the per-cell sim steps
  across it over a reused cell-list snapshot; because each cell touches only its own `World`, the parallel result is
  identical to the single-threaded one. `SyncGhosts` and `ProcessHandoffs` stay single-threaded (they mutate
  neighbouring cells via the `ICellLink`, so they are not cell-independent).
- **Benchmark reports inline vs parallel.** `ServerTickBenchmark.Run` takes an optional scheduler; the exe now prints
  inline ms/tick, parallel ms/tick, and the speedup per regime. On a 12-core box at N=65,536: many small cells
  (C=1024) ~10x, mid (C=64) ~4x, one hot cell (C=1) ~1x (a single cell can't split - that is layer 2).

## 7.40.0

GLFW-backed text clipboard: text get/set now works on Windows and Linux (it was a silent no-op before). The
inherited SDL2 text path needed an `SDL_Init` the engine's Silk.NET/GLFW windowing never calls, so it produced
nothing on the shipped runtime and fell through (macOS still worked via NSPasteboard; Windows/Linux did not).
Additive surface plus a bug fix. Non-breaking, minor.

- **Platform: text clipboard now dispatches through a registered provider seam.** `Clipboard` gains
  `RegisterTextProvider(Func<string?> read, Func<string, bool> write)` and `ClearTextProvider()`. Text get/set
  tries the registered provider first, then macOS `NSPasteboard`, then the mobile bridge (the same dispatch shape
  as before, with the provider replacing the dead SDL2 link). A `read` that returns `null`, or a provider that
  throws, means "could not read" and falls through to the OS backends; an empty (non-null) string is a produced
  value and wins. The image paths (Windows `CF_DIB`, macOS / mobile PNG) are unchanged. The SDL2 `DllImport`s and
  their `SdlGetText` / `TrySdlFree` helpers were removed.
- **Windowing: `AppWindow` wires the GLFW clipboard automatically.** A new internal `GlfwClipboard` reads/writes
  via Silk's `Glfw.GetClipboardString` / `SetClipboardString` over the window's native GLFW handle; `AppWindow`
  registers it on construction and calls `Clipboard.ClearTextProvider()` on `Dispose` (so a torn-down GLFW handle
  is never dereferenced). It is the primary text path on every desktop platform, including macOS, with
  `NSPasteboard` kept as the fallback for windowless / headless consumers that never open an `AppWindow`.
  `KhaozEngine.Windowing` now references `KhaozEngine.Platform` and `Silk.NET.GLFW` and enables
  `AllowUnsafeBlocks` (the native `WindowHandle*`).
- **Tested.** The pure provider adapters (`ReadFromProvider` / `WriteToProvider`: no-provider, null result, empty
  string, and exception cases), the retyped set-dispatch spine, and the public `RegisterTextProvider` /
  `ClearTextProvider` routing on `Clipboard` are headless-tested. The native GLFW path cannot be validated
  headless, so it needs an on-device check: a windowed copy/paste round-trip on Windows (and ideally Linux).

## 7.39.0

Cruft cleanup pass: remove superseded / dead code and fix doc drift accreted from the MonoGame -> Silk pivot and
the phased MMO build. No consumer-visible behaviour change; additive surface only. Non-breaking, minor.

- **Render3D: removed the dormant GPU skinned-mesh path.** `SkinnedModelRenderer` (internal) and its
  `SkinnedModelVert` shader were never instantiated anywhere (engine, tests, or any consumer): `Scene3D` skins on
  the CPU via `SkinningMath` through the rigid `ModelRenderer` pipeline, because the GPU bone read corrupts past
  element 0 in the windowed Veldrid/Metal context. Deleted the renderer, the shader source, and the two
  `ModelRenderer` members only it used (`MaterialLayout` / `DefaultMaterialSet`). The one still-live constant, the
  per-skin bone cap, moved to `SkinningMath.MaxBonesPerDraw` (now public) and is consumed by `Scene3D` +
  `GltfLoader`. The orphaned instance-build test was removed; the headless skinned GPU test (which actually drives
  the CPU path) had its misleading "proves SkinnedModelVert cross-compiles" comment corrected.
- **Audio: new `AudioSystem.StopAllSfx()`.** Surfaces the existing `ISfxBackend.StopAll` through the facade (stop
  every SFX voice on a scene / screen transition or pause; music unaffected). Was implemented by every backend but
  unreachable through `AudioSystem`. Additive.
- **Gpu: `GpuDeviceContext.Device` is no longer an exposed accessor.** The raw Veldrid device is now a private
  field; renderers already consume the engine-owned `IGpuDevice`. Refreshed the `GpuDeviceContext` /
  `VeldridGpuDevice` docs and the package description, which still narrated a "transitional ... phase 3b/3c"
  Veldrid-hiding migration that had already landed.
- **Telegraphs: `TelegraphStyle.ZoneSense.Safe` documented as reserved / no-op.** It renders identically to
  `Danger` in v1; the field now says so explicitly (kept, not removed, so presets can declare intent ahead of the
  feature).
- **Platform: documented the SDL2-clipboard limitation.** The clipboard's SDL2 text path needs an SDL video
  subsystem the host has initialised; the engine's Silk.NET/GLFW windowing never calls `SDL_Init`, so on the
  shipped runtime SDL get/set produce nothing and fall through (macOS uses NSPasteboard; Windows/Linux text
  clipboard currently no-ops; Windows image paste still works via GDI). Code unchanged this release; a GLFW-backed
  text path is the proper fix, tracked as a follow-up.
- **Doc drift swept.** Added `KhaozEngine.Determinism` to the CLAUDE.md package map; rewrote the ROADMAP "Particle
  unification" section (the `Effects.ParticleSystem` it described was removed when `Effects` narrowed to
  `ScreenShake`); trimmed Gui doc-comments pointing at the deleted 4.x `UI.*` types; dropped the stale "(MonoGame)"
  framing from the Netcode type-forward + decoupling-test comments (the `DoesNotContain("MonoGame.Framework")`
  guards stay); fixed stale "SDL2 window" / "from SDL" / "future Silk.NET backend" comments; dropped "experimental"
  from three csproj line comments.

## 7.38.0

MMO netcode stack, Phase 3: seamless sharded world topology. A new package `KhaozEngine.Sharding` (in the
`Server` umbrella) partitions the world into a uniform grid of authoritative cells run in one process, with
seamless cross-boundary movement, plus a reference dedicated-server sample. Design:
`docs/superpowers/specs/2026-06-25-mmo-phase3-seamless-shard-design.md`.

- **Cell grid** (3A): `CellCoord` (world position -> integer cell coord; `FromWorld` floor math mirroring
  `InterestGrid`), `CellSim` (one cell = an ECS `World` + a `FixedTickHost` + a `ServerReplicator` + an
  `InterestGrid`; `Tick` steps the cell's systems per fixed tick), and `ShardHost` (owns the `CellCoord->CellSim`
  map, creates cells on demand, `CellFor`/`CoordFor`/`TryGetCell`, routes spawns by position via `SpawnAt`, ticks
  every cell at one shared fixed rate).
- **Cross-cell ghosting** (3B): the `ICellLink` inter-cell messaging seam (`CellMessage`/`CellMessageKind`) with
  the in-process `InProcessCellLink` default; `ShardHost.SyncGhosts` mirrors owned entities within an
  `OverlapMargin` of a cell edge into the neighbour(s) across it as read-only `Ghost` entities (edge + corner
  neighbours) via the Replication codecs, over a game-supplied `CellPositionAccessor`. A cell's world = owned +
  ghosts; the owner stays the sole simulator.
- **Authority handoff** (3C): `ShardHost.ProcessHandoffs` transfers ownership when an entity crosses a boundary
  with exactly-once semantics (never two owners, never zero) via a `Migrate`/`MigrateAck` handshake over a
  kind-scoped `ICellLink.Drain`, a `Migrating` freeze tag, and `OwnerCount`/`TryGetOwner`. The entity keeps its
  `NetId`; the in-process link completes the handshake within the call.
- **Client home-cell serving** (3D): `ShardHost.BindClient`/`UnbindClient`/`TryGetHomeCell`/`SnapshotForClient`
  (+ `CellSim.RebuildInterest`) serve a client its whole area-of-interest from the single cell owning its player,
  relying on and enforcing the invariant overlap margin >= interest radius; the home cell re-binds automatically
  on a crossing, so the client's view is continuous (nothing in-interest disappears then reappears).
- **Reference dedicated server** (3E): `MmoServerSample` (a sample, `IsPackable=false`) wires a multi-cell
  `ShardHost` over the `NetServer` session layer (any `INetTransport`), per-client home-cell serving,
  `RemoteCommandQueue` input, and `IWorldStore` persistence, on a `FixedTickHost`. `ICellLink` is finalized as the
  inter-cell seam with a documented network-impl contract (route by target `CellCoord`, kind-scoped FIFO `Drain`,
  reliable delivery) for an infrastructure implementation. End-to-end headless test over `LoopbackTransport`
  (connect -> join -> cross a boundary -> re-bind -> continuous, single-ownership view); a `LiveSocket` smoke runs
  over real LiteNetLib UDP.

In-process and deterministic (no sockets in tests); multi-process distribution is infrastructure implementing the
seams. Additive (one new package + a new sample), minor.

## 7.37.0

Window-focus tracking so games (and the Gui) can ignore input while the window is in the background. Fixes the
Hardpoint symptom where moving the mouse over the (unfocused) window still fired UI hover SFX.

- **`InputState.WindowFocused`** (`KhaozEngine.Windowing`): new `bool` on the per-frame snapshot, true while the
  owning window has OS focus. Added as a trailing optional ctor arg (`windowFocused = true`) so the many existing
  `new InputState(...)` builders keep compiling and keep reporting focused. `InputState.Empty` is `false` (a blank
  snapshot is "no window / not focused"). `AppWindow` subscribes to Silk's `IView.FocusChanged` and stamps the bit
  onto every frame (windows open focused). Gate world input (clicks, scroll-zoom, hotkeys) on it, e.g.
  `if (Input.WindowFocused) { ... }`.
- **`Pointer.WindowFocused`** (`KhaozEngine.Windowing`): the pointer reads the bit from `InputState` on each
  `Update`. `IsHoveringIn` now returns false while unfocused (a background window reports no hover). The
  press-origin / tap queries (`IsTapIn` / `IsPressingIn` / `IsDragStartIn` / ...) are deliberately NOT focus-gated,
  so the press-origin click-through invariant is unchanged.
- **`GuiSurface`** (`KhaozEngine.Gui`): hover (`IsHovering` / `HoverEntered`) and both capture gates
  (`PointerCaptured` / `HoverCaptured`) report false while the window is unfocused, via `Pointer.WindowFocused`.
  No new `Begin` overload and no game code needed: a game already calls `pointer.Update(input)`, so the focus bit
  flows through automatically. This kills background-window UI hover SFX / highlights for every consumer.

Additive (new public API; the default-focused path preserves all existing behaviour), minor.

## 7.36.0

MMO netcode stack, Phases 1 + 2 (session lifecycle, entity replication, interest management, world store).
Builds on the Phase 0 transport seam + fixed-tick host. Two new packages; design + decomposition in
`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`.

- **Session lifecycle** (`KhaozEngine.Netcode`, 1D): `NetServer` / `NetClient` over `INetTransport` — a
  Hello/Welcome/Reject handshake (`SessionFrame`/`SessionOpcode`), `IConnectionAuthenticator` seam (+ the dev
  `AllowAllAuthenticator`), `SlotAllocator` (lowest-free player slot, recycled, capped), and Joined/Left/Data
  session events (`ServerSessionEvent` / `ClientSessionEvent`).
- **Entity replication** (new package `KhaozEngine.Replication`, depends on `Ecs` only, 1C): `NetId` identity,
  a closure-based `ReplicationRegistry` (per-type serialize/deserialize/lerp/capture/remove over the public
  `World` API), `SnapshotWriter` (full-state, `byte[]`), `ClientReplicationView` (`Apply` + `ApplyDelta`:
  spawn/despawn/update + interpolation), and `ServerReplicator` (per-slot acked baselines + baseline+delta
  encode — only changed entities/components per client).
- **Interest management / AoI** (`KhaozEngine.Replication`, 2E): `InterestGrid` (uniform spatial hash,
  exact-radius query) + `SnapshotWriter.WriteFiltered` (per-client interest-filtered snapshot; the existing
  `ClientReplicationView.Apply` then spawns entities that entered the set and despawns those that left).
- **World store** (new package `KhaozEngine.WorldStore`, zero deps, 2F): `IWorldStore` — an async keyed `byte[]`
  durable-state seam shaped for a database backend — plus a thread-safe `InMemoryWorldStore` reference impl
  (SQLite/Postgres/cloud implement the seam as infrastructure).
- Both new packages added to the `KhaozEngine.Server` umbrella. All headless-tested over `LoopbackTransport` /
  in-process. Additive (two new packages + new public API), minor.

## 7.35.0

MMO netcode stack, Phase 0 (transport seam + fixed-tick host). First foundation of the authoritative
multiplayer program (`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`).

- `KhaozEngine.Netcode`: new `INetTransport` byte-transport seam (`Poll` / `TryDequeueEvent` / `Send` /
  `Disconnect`) with the `NetConnectionId`, `NetEvent`, `NetEventType` value types, plus `LoopbackTransport` -
  a deterministic, socket-free, thread-free in-memory transport pair for headless tests and local play
  (`CreatePair` returns two linked endpoints; a Send surfaces on the peer after it Polls).
- `KhaozEngine.Netcode.LiteNetLib`: `LiteNetLibServerTransport` / `LiteNetLibClientTransport` implement
  `INetTransport` over reliable-UDP, reusing `ChannelSplitter.ToDeliveryMethod` for the reliability mapping
  (peer surfaced as `NetConnectionId` = `peer.Id + 1`).
- New package `KhaozEngine.Simulation` (zero-dependency leaf): `FixedTickHost`, a headless fixed-timestep
  accumulator that turns variable elapsed time into a deterministic whole number of fixed-dt ticks (with a
  spiral-of-death backlog guard), decoupling simulation rate from render rate. Promoted from SpaceGame's
  `FixedStepRunDriver`, reduced to a single authoritative tick stream. Added to the `KhaozEngine.Server` umbrella.
- All headless-tested over the loopback transport; the live UDP round-trip is a `Category=LiveSocket` smoke.
  Additive (new package + new public API), minor.

## 7.34.0

Attack telegraph / danger-zone indicator system (presentation-only). Two new packages:
`KhaozEngine.Telegraphs` (style model + the pure `TelegraphResolve` progress->visual mapping + the
immediate-mode `TelegraphRenderer2D` 2D path; in the `Game2D` umbrella) and
`KhaozEngine.Telegraphs.Render3D` (the `Scene3D` ground-plane extensions
`GroundCircle/Ring/Beam/Cone/Arc`; in the `Game3D` umbrella). Shapes: circle, ring, beam, cone, arc.
Styles: `Generic`/`Fire`/`Poison` presets plus a `TelegraphStyle` (fill/outline color, edge thickness,
opacity, fill mode, composable `OutlinePulse | FillSweep | ColorRamp | ImpactFlash` animations,
alpha/additive blend, reserved `Safe` zone sense). Render3D gains a generic `DrawGroundDecal` primitive:
a depth-sampling ground decal (new `DecalVert`/`DecalFrag` shaders + a pass between the beam pass and the
post chain) that reconstructs each pixel's surface position from the linear-depth buffer, paints an
analytic shape SDF onto the ground/terrain within a Y-band, is occluded by meshes, and rejects
no-geometry background via a read-only hardware depth test. Render2D gains generic `DrawFilledSector` /
`DrawFilledArcBand` primitives. Telegraphs hold no sim state and never enter a game's determinism hash.
New GPU golden `telegraph_ground` (Metal baked; D3D11 + Vulkan baked via cross-platform-gpu.yml).

## 7.33.0

Generic headless snapshot harness so a game's art/UI screenshot tool is just its scenes, not capture/encode/write/log boilerplate. Three new packages. `KhaozEngine.Imaging` (BCL-only, zero engine deps) is the new canonical home for the dependency-free RGBA8 PNG encoder: `PngWriter.Save(string path, ReadOnlySpan<byte> rgba, int w, int h)` and `PngWriter.Encode(...)`. The previously-internal-to-Render2D `KhaozEngine.Render2D.Png` is now a thin back-compat shim that forwards to `PngWriter` (no behaviour change; existing callers such as SpaceGame's clipboard-copy `Png.Encode` keep working), and `Render2D` gains a project reference to `Imaging`. `KhaozEngine.Snapshot` (depends on `Render2D` + `Imaging`, NO Render3D) adds `SnapshotRunner`: construct with `new SnapshotRunner(string outDir, Action<string>? log = null)` (creates the dir; logger defaults to `Console.WriteLine`), then `Shot2D(name, w, h, Color clear, Action<Render2DContext> draw)` runs `Render2DSnapshot.Capture`, PNG-encodes, writes `<outDir>/<name>.png`, logs the path, and returns it; `Save(name, rgba, w, h)` is the shared encode+write+log+`Count` sink for an already-captured buffer; `Done()` emits the final `done -> <dir> (N shots)` summary; `OutDir`/`Count` are exposed. `SnapshotHost` makes a tool's `Program.cs` one line: `Main(string[] args, Action<SnapshotRunner> register, Action<string>? log = null)` (and a `Run` returning the dir) resolves `outDir` from `args[0]` or the deterministic `SnapshotHost.DefaultOutDir` (`<temp>/ke-snapshots`, no timestamp), runs `register` against a fresh runner, prints the summary, returns exit code 0. `KhaozEngine.Snapshot.Render3D` (depends on `Snapshot` + `Render3D`) adds the `Shot3D(this SnapshotRunner, name, w, h, Action<Scene3D> setup, Action<Scene3D> drawFrame, int frames = 1)` extension wrapping `Render3DSnapshot.Capture` through the same sink. The 3D path is split into its own package so a Game2D-only game (SpaceGame, Nullwake) uses `Shot2D` without dragging in the 3D renderer; both snapshot packages are tooling and are intentionally NOT in the `Game2D`/`Game3D` umbrellas, so a snapshot tool project references `KhaozEngine.Snapshot` (2D) plus `KhaozEngine.Snapshot.Render3D` (3D) directly. Deterministic (no timestamps in output/filenames), window-free (the underlying capture still needs a GPU device). New `SnapshotSample` console app is a runnable one-2D-one-3D acceptance example (`dotnet run --project SnapshotSample -- <dir>`). Headless-tested (16 new no-GPU tests: `PngWriter` signature/round-trip/reject-bad-length/file-write, `SnapshotRunner` dir-creation/named-write/path-return/decode/log/count/summary/default-logger, `SnapshotHost` args-dir/default-dir/exit-code) plus a gated `[GpuFact]` end-to-end (`SnapshotHarnessGpuTests`: a runner drives one `Shot2D` + one `Shot3D` to a temp dir, both PNGs decode to the requested size). SemVer: additive (three new packages; `Render2D.Png` behaviour byte-unchanged), so minor.

## 7.32.0

Reusable versioned save-migration chain in `KhaozEngine.Persistence`. Previously `SettingsManager<T>` offered only a single `sanitizeOnLoad: Func<T,T>` hook, so consumers hand-rolled all schema migration as one branching blob with a manual version bump inside that callback (e.g. Hardpoint's `CampaignSave.Sanitize`). New `MigrationChain<T>` is a standalone, immutable, validated chain of per-version steppers. Build it with the fluent `MigrationChain.For<T>(getVersion, setVersion)` (any POCO) or the zero-config `MigrationChain.For<T>()` for reference types implementing the new `ISchemaVersioned { int SchemaVersion }` interface, then register one `Step(fromVersion, Func<T,T>)` per version and `Build(currentVersion)`. Each step does ONLY the data transform; the chain stamps the version field after each successful step. `Build` is fail-fast: a gap in the step run, a duplicate `fromVersion`, or a step targeting at/beyond the current version throws `ArgumentException` at startup, so a misconfigured chain can never reach runtime. `Migrate` is lenient on user data and never throws: a value already at/above current is a no-op (so a save from a newer build is left intact), a value older than the oldest step is logged at Warn and returned unchanged, a step that throws is logged at Error and halts the chain returning the partially-migrated value (version stamped only for completed steps), and a throwing get/set delegate is swallowed and logged. An empty chain is a silent no-op. Recommended convention: default a type's version field to the current version so a fresh `new T()` no-ops silently. Wired into `SettingsManager<T>` via a new optional `migrations` ctor arg that runs the chain on every load BEFORE `sanitizeOnLoad` (clamp/normalize still runs last), and into `GameStorage` via optional `migrations` params on `CreateSettingsManager<T>` and the raw `Load<T>` (which had no migration hook at all before). All additions are appended optional parameters, so existing call sites are unchanged. Headless-tested (20 new tests): ordered stepping + auto-stamp, no-op at/above current, too-old Warn, step-throws-halts-with-partial, delegate-throws-swallowed, null value, build-time gap/duplicate/out-of-range validation, empty-chain silent no-op, both factory forms, `SettingsManager` order-before-sanitize + ctor-load + explicit-reload + back-compat, and `GameStorage` load-with-chain + absent-file-with-chain + `CreateSettingsManager` forwarding. SemVer: additive, minor.

## 7.31.0

Centralised pointer-gesture consumption to kill same-frame click-through across a scene transition (`KhaozEngine.Windowing` + `KhaozEngine.Game`). The press-origin tap invariant (`Pointer.IsTapIn`) is purely geometric: it only checks that the press-origin AND release both fall inside a widget rect, with no notion of when the widget appeared. So a release that triggers a `SceneManager` push (or pop) and draws a new overlay the SAME frame had that overlay's button register a completed tap from a gesture that began before the button existed (Hardpoint's campaign map: releasing on a level node pushes the tier-select overlay, and the same release auto-selected whichever difficulty button happened to land under the click). The frame loop never re-reads the pointer between the update pass that pushes and the draw pass that renders the new scene, so `IsJustReleased` + the press-origin were still live when the overlay's `Button` ran `IsTapIn`. New `Pointer.ConsumeGesture()` marks the current press/release gesture as handled so `IsTapIn`/`IsTapFromTo` report false for the rest of that gesture; it clears automatically on the next fresh press (`IsJustPressed`). New `Pointer.IsConsumed` exposes the flag. Drag/hover/press-visual queries (`IsDragStartIn`, `IsPressingIn`, `IsHoveringIn`, `IsDraggingIn`, ...) are deliberately left untouched, so an in-progress slider grab survives a consume. `SceneManager` now calls `Pointer?.ConsumeGesture()` on every applied transition (`ApplyPush`/`ApplyPop`, so `Replace`/`SwitchTo`/`Clear` inherit it) via the shared `SceneManager.Pointer`, spending the gesture that caused the transition so widgets the incoming scene draws later in the same frame ignore it. Null-safe when no `Pointer` is wired. A real fresh click on the new overlay still works (a new press starts a fresh, unconsumed gesture). Headless-tested (no GPU): consume suppresses the tap on the release frame, a fresh press clears it and taps normally again, consume leaves a held drag grab intact, and `SceneManager` push/pop each consume the in-flight gesture (the campaign-map regression spelled out) plus a null-pointer transition no-ops safely. SemVer: additive (new `Pointer.ConsumeGesture()` + `IsConsumed`; the `IsTapIn`/`IsTapFromTo` change only fires once a consumer opts in by consuming, and `SceneManager`'s auto-consume is a bug fix to a previously-broken interaction), so minor.

## 7.30.0

Turn-key `SkinnedLimb` convenience component in `KhaozEngine.Render3D`: one stateful object that bundles the whole procedural-limb pipeline (`SkinnedMeshBuilder.BuildTube` -> `ProceduralChainSolver` -> `PolylineFrames` -> `Scene3D.DrawSkinned`) so a game stands a tentacle / cable / tail up in two calls instead of hand-wiring four primitives every frame. Pure orchestration over EXISTING public API: no new rendering, shaders, or GPU goldens. Construct with `new SkinnedLimb(Scene3D scene, float radius, float length, int ringSegments, int radialSegments, int boneCount, in ChainConfig config, Axis axis = Axis.Z)` (plus a `Scene3D.TextureHandle` overload for an albedo, and a `Scene3D.SurfaceMaps` overload for full PBR-lite albedo+normal+roughness) - the ctor builds the tube via `BuildTube` and uploads it via the matching `LoadSkinnedMesh` overload; `boneCount` sets both the rig and the spine length. Drive it per frame with `Update(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds)` (writhe only) or `Update(root, forward, up, clockSeconds, Vector3 target, float reachWeight)` (writhe + FABRIK reach); both run `ProceduralChainSolver.Solve`/`SolveReach` -> `PolylineFrames.BuildInto` into reusable scratch buffers (one `Vector3[]` spine + one `Matrix4x4[]` bones), so the per-frame motion path allocates ZERO bytes. Draw with `Draw(Scene3D scene, Matrix4x4 model, Color tint)` (+ a `Material` overload), which calls `DrawSkinned` with the stored bones. `Bones` (`ReadOnlySpan<Matrix4x4>`) and `Spine` (`ReadOnlySpan<Vector3>`) expose the current pose so a game can read it (with the usual buffer-reuse caveat); `BoneCount`, `RunAxis`, and `Handle` round out the surface, and a mutable `Config` field lets a game retune the writhe at runtime (e.g. ramp amplitude as a boss enrages). `Dispose`/`IDisposable` frees the tube's GPU buffers via `Scene3D.UnloadSkinnedMesh` (idempotent; `Update` after dispose throws `ObjectDisposedException`). The bone-computation/state plumbing is fully headless-testable WITHOUT a GPU: a static `SkinnedLimb.CreateHeadless(int boneCount, in ChainConfig config, Axis axis = Axis.Z)` factory builds a limb with no GPU mesh whose `Update`/`Bones`/`Spine` all work (its `Draw` is a no-op, `Dispose` frees nothing), so the whole solve->frames->bones step is asserted with no device. Also new (and used by the limb): `PolylineFrames.BuildInto(ReadOnlySpan<Vector3> points, Axis runAxis, Vector3 up, Span<Matrix4x4> framesOut)`, an in-place, zero-alloc sibling of `PolylineFrames.Build` (which now delegates to it); it throws if `framesOut` is shorter than `points`. Headless-tested (no GPU): one bone/spine entry per bone, the bones equal a hand-wired `ProceduralChainSolver` + `PolylineFrames` reference exactly, a straight (zero-amplitude) config marches the bones along forward at `SegmentLength`, `reachWeight=1` pulls the tip bone onto the target while `reachWeight=0` matches the writhe-only tip, determinism, zero per-frame allocation over 400 `Update` calls (`GC.GetAllocatedBytesForCurrentThread`), in-place buffer reuse across frames, runtime `Config` retune changes the motion, a headless limb's `Draw` is a no-op + `Dispose` is safe + idempotent, `Update`-after-dispose throws, a zero-bone ctor throws, and `BuildInto` matches `Build` + rejects a too-small destination. A gated `[GpuFact]` integration (`SkinnedLimbGpuTests`, skipped unless `KE_GPU_TESTS=1`) proves the GPU mesh ownership end to end on a real device: build a limb against a `Render3DPreview` scene, `Update`, `Draw`, assert opaque pixels render, then `Dispose` and assert a draw with the now-stale handle renders nothing. SemVer: additive (new `SkinnedLimb` type + `PolylineFrames.BuildInto`; `PolylineFrames.Build` behaviour byte-unchanged), so minor.

## 7.29.0

Opt-in glTF material texture auto-read in `KhaozEngine.Render3D` (the follow-up 7.25.0 and the beam release punted as "future work"). Today `GltfLoader` ignores a material's textures: a game must export PNGs and bind them by hand via `Scene3D.SurfaceMaps`. This adds a convenience that reads them straight off the glb, WITHOUT changing the default explicit-bind path. New public `GltfLoader.LoadWithMaterial(string path)` returns `(GltfMesh Mesh, GltfMaterialMaps Maps)`, and `LoadSkinnedWithMaterial(string path)` returns `(SkinnedGltfMesh Mesh, GltfMaterialMaps Maps)`. The mesh half is BYTE-IDENTICAL to `Load`/`LoadSkinned` (the loader shares the same mesh-build path, now factored into private `BuildRigid`/`BuildSkinned`); only the maps are new. The loader has NO GPU device, so it cannot create `TextureHandle`s: auto-read DECODES to raw RGBA8 only. New public `GltfMaterialMaps` (readonly struct) carries three optional `DecodedImage?` (also new: a readonly struct of `byte[] Rgba` + `Width`/`Height`, tightly-packed RGBA8, row-major, top-left origin): `Albedo` (glTF `BaseColor`), `Normal` (tangent-space RGB, passed through unchanged), and `Roughness` (glTF `MetallicRoughness` packed texture, passed through unchanged - the model shader already samples `.g` for roughness, so it is NOT repacked), plus an `IsEmpty` convenience. Decoding reuses the engine's existing `ImageRgba.Decode` (StbImageSharp, the same PNG/JPG->RGBA8 path `Scene3D.LoadTexture(pngPath)` already uses); no new image library. Both embedded GLB images and external image files (SharpGLTF resolves them relative to the glb on load) are read. Graceful degrade: a material with no textures - or a channel whose image is missing, an unresolved external ref, or undecodable - leaves that map `null` (absent), never a throw, so the mesh just renders without it; the first material that references any auto-read texture is chosen (falling back to the first primitive's material), matching the loaders' single-mesh flattening. New `Scene3D.LoadSurfaceMaps(GltfMaterialMaps)` uploads the bundle into a `SurfaceMaps` with one `LoadTexture` call per present map (absent maps stay a `default` handle -> the renderer's 1x1 default for that slot: white albedo / flat normal / zero roughness), and the scene owns + disposes the uploaded textures as usual. Two one-call convenience overloads, `Scene3D.LoadMesh(GltfMesh, GltfMaterialMaps)` and `LoadSkinnedMesh(SkinnedGltfMesh, GltfMaterialMaps)`, do upload+bind together (= `LoadMesh(mesh, LoadSurfaceMaps(maps))`). The whole feature is opt-in: existing `Load`/`LoadSkinned` + explicit `SurfaceMaps` callers are byte-unchanged. Fully headless-tested (no GPU): a textured-triangle glb fixture (distinct embedded 1x1 PNGs per channel, hand-authored in-test so no image encoder is needed) asserts the decoded RGBA dimensions/pixels for albedo/normal/roughness and that metallicRoughness comes through unrepacked; a baseColor-factor-only material yields an all-absent `GltfMaterialMaps`; the `LoadWithMaterial` mesh matches the default `Load` mesh vertex-for-vertex; and `LoadSkinnedWithMaterial` reads both the skin and the albedo while its mesh matches `LoadSkinned`. No shader change, so no new GPU golden. SemVer: additive (new `LoadWithMaterial`/`LoadSkinnedWithMaterial`, `GltfMaterialMaps`, `DecodedImage`, `Scene3D.LoadSurfaceMaps` + the two `GltfMaterialMaps` load overloads; default path untouched), so minor.

## 7.28.0

PBR-lite materials on the SKINNED lit model pass (`KhaozEngine.Render3D`), extending 7.25.0's rigid normal/roughness to rigged meshes (gap E of the realistic-tentacle-boss spec). `SkinnedVertex` gains a tangent (xyz model-space direction + handedness `w`; now 96 bytes), mirroring `ModelVertex`; the field defaults to `Vector4.Zero`, so every existing object-initializer call site is unchanged and a tangent-less skinned vertex keeps the no-TBN (geometric-normal) fallback. `GltfLoader.LoadSkinned` reads the glTF `TANGENT` accessor when present and otherwise computes a per-vertex tangent from UV+position (Lengyel, accumulated over the directly-indexed triangle list then Gram-Schmidt orthogonalized against the normal); `SkinnedMeshBuilder.BuildTube` computes tangents from its ring UVs too, so a procedural tentacle/cable takes a normal map. The Lengyel face-direction + resolve math is now a shared `TangentMath` helper used by both `MeshAssembler` (rigid) and the skinned loader/builder, so the two paths produce the same basis. Bind maps with the new `Scene3D.LoadSkinnedMesh(SkinnedGltfMesh, SurfaceMaps)` overload (mirrors the rigid `LoadMesh(mesh, SurfaceMaps)`): albedo + optional normal + optional roughness, each unset map falling back to the renderer's 1x1 default (white albedo / flat normal `(0,0,1)` / zero roughness). Skinned meshes deform on the CPU through the rigid `ModelRenderer`/`ModelFrag` pipeline (the GPU bone read corrupts past element 0 in the windowed Veldrid/Metal context, unchanged since the skinned pass shipped), so `SkinningMath.SkinVertex` now carries the tangent through the same skin matrix as the normal (re-normalized, handedness preserved) into the produced `ModelVertex`, and the existing `ModelFrag` TBN + roughness math applies with no fragment-shader change. `SkinnedModelVert` (the dormant but revivable GPU-skinning reference) is also extended: a new per-vertex `Tangent` attribute at location 6 (the per-instance stream shifts 6..12 -> 7..13) deformed through skin*model and emitted as `vTangent`, kept in sync with the CPU path. Because the skinned path reuses `ModelFrag`, the Metal albedo-first binding-order fix from 7.25.0 (SPIRV-Cross assigns MSL texture indices in first-sample order, so the shader samples Albedo before the normal/roughness maps) covers the skinned pass for free. The no-maps / zero-tangent skinned path is BYTE-IDENTICAL to 7.27.0: a zero source tangent transforms to zero (the shader lights with the geometric normal) and zero roughness collapses the spec terms to the previous per-instance Blinn-Phong, so all committed skinned goldens pass with no re-bake. New gated GPU golden `scene3d_skinned_normalmap` (a bent procedural tube with a normal + roughness gradient, baked on Metal; D3D11 + Vulkan to follow in CI, matching the beam/normalmap precedent). New headless tests: the 96-byte layout + zero-tangent default, the tangent carried through `SkinVertex` (rotates with the skin, keeps handedness, stays zero when absent), the zero-tangent stream deforming byte-identically to the pre-tangent geometry, the tube's computed tangents being finite/unit/orthogonal, and `LoadSkinned` computing tangents from UV when `TANGENT` is absent. Verified on a real Metal device that the normal-mapped skinned tube renders measurably differently from the albedo-only one. SemVer: additive (new `SkinnedVertex` tangent field with a zero default, new `Scene3D.LoadSkinnedMesh(SkinnedGltfMesh, SurfaceMaps)` overload; the no-map path is byte-identical), so minor.

## 7.27.0

Procedural chain-limb animator in `KhaozEngine.Render3D`: new pure, deterministic `ProceduralChainSolver` + `ChainConfig` that generate a per-frame 3D spine for tentacles / cables / tails and optionally bend it onto a target with FABRIK reach, feeding the existing `PolylineFrames.Build` -> `Scene3D.DrawSkinned` path (no new presentation code). `Solve(root, forward, up, clock, cfg, spineOut)` writes one point per bone: it starts a direction at `forward` and accumulates a per-segment bend from two counter-travelling sine waves (so the cumulative bend crosses zero - the limb extends, curls and flicks both ways rather than coiling one fixed direction), with an optional out-of-plane pitch (`ChainConfig.OutOfPlaneFrac`) for a 3D curl; a zero `WritheAmplitude` yields a straight limb along `forward`, and every segment keeps `ChainConfig.SegmentLength`. `SolveReach(..., target, reachWeight, ...)` runs `Solve` then FABRIK toward `Lerp(naturalTip, target, reachWeight)`, so `reachWeight` 0 leaves the natural writhe tip untouched and 1 pulls the tip onto the target (clamped to the limb's reach); segment lengths are preserved by the solve. `Fabrik(spine, root, target, segmentLength, iterations)` is exposed as a reusable in-place uniform-length FABRIK (pins the root, iterates backward/forward passes, stretches straight toward an out-of-range target). `SlamEnvelope(phase, snap)` is a `[0,1]` power-stroke envelope (hold at reach -> snap to slam -> brief hold -> recover) to drive `reachWeight` or a whip over time; `phase` wraps and `snap` (clamped `[0.05,0.5]`) sets the transition width. This generalizes SpaceGame's game-side 2D `SlathTentacleLayout` (sine writhe + asymmetric power stroke, solved in `Vector2` and lifted to 3D) into an engine primitive that works in an arbitrary 3D frame; SpaceGame can later retire its bespoke layout onto this. Headless-tested: root anchored at `spineOut[0]`, uniform segment length, determinism, straight-at-zero-amplitude, planar when `OutOfPlaneFrac` is 0 vs leaving the plane when it is not, FABRIK reaches a reachable target and stretches to max reach for an unreachable one while preserving segment lengths, the reach blend matches the writhe tip at weight 0 and the target at weight 1, and the envelope stays in `[0,1]` and rests/peaks correctly. Presentation-only (no GPU, sim, or RNG deps). SemVer: additive (new types only, no existing signature touched), so minor.

## 7.26.0

3D beam primitive on `Scene3D` (`KhaozEngine.Render3D`): `DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)` queues a camera-facing, additive, glowing beam between two world points (lasers, thrusters, tethers): a bright core inside a soft halo. It draws INTO the lit model pass with the depth test on (less-equal, no write), like the textured billboard, so geometry occludes the beam (a nearer mesh hides it, the beam draws over a farther mesh) and it goes through the pixel post chain; the normal/depth MRT targets use a PreserveDestination blend so the edge-outline pass never traces the strip. New public `BeamStyle` (immutable record struct): nullable `CoreColor`/`GlowColor` (null resolves from the `color` arg, with the halo a 0.4x-alpha copy of the core), `CoreFraction`, `GlowSoftness`, end `Taper`, and time-driven `PulseSpeed`/`PulseAmount` + `ScrollSpeed`, with `BeamStyle.Default`. New public pure helper `BeamGeometry` (a view-aligned strip `side = normalize(cross(viewDir, axis))`, headless-tested like `BillboardGeometry`; degenerate `a≈b`/`width<=0` and `axis∥viewDir` handled). Animation reads a new `Scene3D.EffectTimeSeconds`, a per-frame clock the host sets in its draw callback (NOT cleared by `Begin`); 0 renders a static beam. Internally a new `BeamRenderer` (one additive draw for all beams; per-beam style baked per-vertex, so no per-draw uniform rebinding) and `BeamVert`/`BeamFrag` shaders (GLSL 450, cross-compiled). The recommended combo (in `docs/USING-KHAOZENGINE.md`) is `DrawBeam` + `AddLight` at both endpoints + a particle spark burst at the impact point. New headless `BeamGeometryTests`/`BeamStyleTests`, gated `Scene3DBeamQueueTests`, and a `scene3d_beam` depth-interleave golden (baked on Metal; D3D11 + Vulkan to follow on their backends). Zero beams render bit-identical to 7.25.0 (the model pass adds an empty additive draw that is skipped when nothing is queued). SemVer: additive (new `DrawBeam`, `BeamStyle`, `BeamGeometry`, `EffectTimeSeconds`), so minor.

## 7.25.0

PBR-lite materials on the rigid lit model pass (`KhaozEngine.Render3D`): optional tangent-space NORMAL and ROUGHNESS maps alongside the albedo. `ModelVertex` gains a tangent (xyz direction + handedness `w`; now 64 bytes) with a new 5-arg ctor; the 3-arg/4-arg ctors still work and leave the tangent zero, so all existing callers are unchanged. `MeshAssembler` computes a per-vertex tangent from UV+position (Lengyel, accumulated across shared faces then Gram-Schmidt orthogonalized), and `GltfLoader.Load` reads the glTF `TANGENT` accessor when present, otherwise falls back to the computed tangent. Bind maps explicitly with the new `Scene3D.SurfaceMaps` (albedo/normal/roughness `TextureHandle`s) via `LoadMesh(GltfMesh, SurfaceMaps)`; any unset map falls back to a 1x1 default (white albedo / flat normal `(0,0,1)` / zero roughness). `ModelFrag` builds a TBN and perturbs the lit normal by the normal map; roughness (glTF metallic-roughness `.g` convention; metallic ignored) lowers specular strength and broadens the highlight. The MRT normal target the edge-outline post pass samples keeps the GEOMETRIC normal, not the perturbed one, so normal maps do not spawn spurious outlines. Untextured meshes and `MeshPrimitives` carry a zero tangent and render BIT-IDENTICAL to 7.24.0 (the committed goldens pass with no re-bake): a zero tangent skips the TBN (lit by the geometric normal), and the default zero roughness collapses the spec terms to the previous per-instance Blinn-Phong exactly. Skinned meshes stay albedo-only this release (no tangents; normal maps would be inert). New `PixelPostProcessSettings.UseSmoothPreset()` turns off cel bands / palette quantize / dither / edge outline / starfield / pixelated upscale in one call for a smooth realistic look to pair with PBR-lite materials (a realistic surface is otherwise still quantized/outlined by the fullscreen post chain). New pure helper `SurfaceShading` (the CPU mirror of the fragment shader's TBN perturb + roughness math, headless-tested, like `SkinningMath`). New gated GPU golden `scene3d_normalmap` baked on Metal + D3D11 + Vulkan. Metal note: the model shader samples all three textures up front in binding order (Albedo first) because SPIRV-Cross assigns MSL texture indices in first-sample order, so sampling a higher-binding map first mis-bound the albedo sampler to the normal map (untextured meshes came out flat-normal coloured); D3D11/Vulkan bind by explicit decoration and are order-insensitive. glTF material normal/metallic-roughness texture auto-read is NOT included (bind explicitly); noted as a future follow-up. SemVer: additive (new `ModelVertex` ctor, `Scene3D.SurfaceMaps` + `LoadMesh` overload, `SurfaceShading`, `UseSmoothPreset`; the no-map path is byte-identical), so minor.

## 7.24.0

Hardening (`KhaozEngine.Netcode` + `KhaozEngine.Render3D`): an adversarial security pass over the untrusted-input surfaces closed four findings in engine primitives whose contracts the security baseline tells games to trust. (1) `UnitAxisQuantizer.Dequantize` now clamps its input to `[-127,127]` before dividing by 127, so a hostile or garbage wire byte of `-128` (which `Quantize` never emits) can no longer escape the documented `[-1,1]` axis range (`-128/127 = -1.0079`); no value `Quantize` can produce changes, so the hash-gated round-trip is unaffected. (2,3) `RemoteCommandQueue<TCommand>` gained two optional constructor caps, `maxQueuedPerSlot` (default 256) and `maxSlots` (default 64): a hostile peer flooding distinct seqs or spraying distinct slot ids can no longer grow the per-slot buffer or the slot map without bound (when a per-slot buffer is full the oldest buffered command is evicted to admit a newer seq), and `Store` now rejects any seq at or below the slot's processed high-water mark, so an already-dequeued seq cannot be replayed/reprocessed and a stale lower seq cannot regress the acknowledged seq the host stamps on snapshots (the ack is monotonic). The existing single-argument constructor still works (the caps are optional), so existing callers are unaffected. (4) `GltfLoader.LoadSkinned` now validates the rig at load: it rejects a skin whose joint count exceeds the 128-bone per-draw cap, and rejects any vertex whose `JOINTS_0` references a bone index outside `[0, jointCount)`, via the new pure `SkinningMath.AreBoneIndicesValid(Vector4 indices, int boneCount)`. Previously a malformed or malicious glTF could carry an out-of-range bone index that indexed past the per-draw bone palette and threw inside the per-frame CPU-skinning loop (`SkinningMath.BlendSkinMatrix` reads the palette unconditionally for all four bones); a clean load-time `InvalidOperationException` replaces the mid-frame crash. All four are reachable only when a game feeds untrusted input to these primitives (no shipping game does today: SpaceGame is the only netcode/skinned-mesh consumer and ships trusted assets), but the contracts are documented for current and future games in `docs/SECURITY-BASELINE.md`, which is updated to match. Headless tests added: `Dequantize(-128)` stays in `[-1,1]`; queue per-slot and slot caps, replay rejection, non-regressing ack, and non-positive-cap ctor throws; `AreBoneIndicesValid` range checks. SemVer: additive public API (two new members, the queue caps as optional ctor params and `SkinningMath.AreBoneIndicesValid`; no existing signature or behaviour for valid input changed), so minor.

## 7.23.0

Additive (`KhaozEngine.Foundation`): the "CET off on game heads" standard now ships as an overridable, auto-inherited default, so every consumer head (current and future) gets it without re-deciding. .NET 9+ marks the x64 apphost CET-compatible by default, which hard-aborts at boot on Windows 10 builds with only partial CET / shadow-stack support (e.g. 20H2): "Your Windows doesn't fully support CET." `CETCompat` is an apphost (game-head) MSBuild property and the engine ships libraries, not an apphost, so it cannot be set on consumers from engine code. Instead `KhaozEngine.Foundation` now packs two build assets to both `build/` and `buildTransitive/`: `KhaozEngine.Foundation.props` sets `<CETCompat>false</CETCompat>`, and `KhaozEngine.Foundation.targets` emits a `normal`-importance build message announcing the default. Foundation is the common ancestor of every umbrella (`Game2D` -> `Foundation`, `Game3D` -> `Game2D` -> `Foundation`, `Server` -> `Foundation`), so each game head inherits the default whether Foundation is a direct reference or pulled transitively; the `buildTransitive/` copy is what makes the transitive case work (NuGet auto-imports `buildTransitive/<PackageId>.props|.targets` by exact name). The default is overridable and non-breaking on adoption: the props `PropertyGroup` is conditioned on `'$(CETCompat)' == ''`, so a head that already pins `CETCompat` (either value, as all three consumers do today) wins, and the build message stays silent whenever a head pins its own value: it is dual-gated on a private `_KhaozEngineCetDefaulted` marker (the props condition leaves it unset for a `Directory.Build.props` override) plus a final `CETCompat == false` re-check in the `.targets` (imported after the project body, so it suppresses the message when a `.csproj`-body override flips the value back to `true` after the marker was set). `CETCompat` is a no-op outside the Windows-x64 apphost, so the `Server` head and macOS/Linux builds are unaffected. Rationale: CET is a hardware ROP mitigation over the small native surface; KhaozEngine games are overwhelmingly managed (memory-safe), and DEP/ASLR plus the signed auto-updater remain, so disabling it buys broad old-Windows compatibility for a narrow, reversible tradeoff. A headless test asserts the props default + overridable condition + marker, the targets message gate, and that the csproj packs both assets to `buildTransitive/` (the silent failure mode is a rename/path typo that stops the NuGet auto-import). Consumers adopt by bumping their `KhaozEngine.Foundation`/`Game2D`/`Game3D` pin to 7.23.0; they may then delete their own manual `<CETCompat>false</CETCompat>` line (keeping it is harmless, just redundant). SemVer: additive (no public type changed), so minor.

## 7.22.0

Additive (`KhaozEngine.Render3D`): 32-bit (uint) mesh indices, so detailed/sculpted models past the old 65,536-vertex ceiling load and render instead of throwing or truncating. Indices are now stored authoritatively as 32-bit on both `GltfMesh` and `SkinnedGltfMesh`: new `uint[] Indices32` (always valid) and `GpuIndexFormat IndexFormat` (UInt16 when the largest index still fits in 16 bits, else UInt32). The renderer uploads and binds the matching index width per mesh, so a mesh that stays small keeps a byte-identical 16-bit index buffer (existing renders are unchanged, verified by the GPU golden suite). New `uint[]` constructors on both meshes sit alongside the existing `ushort[]` ones; `MeshAssembler`, `GltfLoader.Load`/`LoadSkinned`, and `MeshBuilder` (whose parts now fuse freely across the 65,536-vertex boundary) emit 32-bit indices and dropped their ushort-ceiling throws. The legacy `ushort[] Indices` property is retained for back-compat: it returns the 16-bit view for fitting meshes (so every existing small-mesh caller is unaffected) and throws `InvalidOperationException` for a 32-bit mesh, directing callers to `Indices32`. `MeshOps` operates on `Indices32` so its smoothing/flat-normal helpers are large-mesh-safe. Internal renderer plumbing (`Scene3D` index-buffer creation, `ModelRenderer.DrawMeshInstanced`/`DrawCpuSkinned`, `SkinnedModelRenderer.DrawSkinnedInstance`) threads the per-mesh `GpuIndexFormat` through; the `KhaozEngine.Gpu` layer already mapped `GpuIndexFormat.UInt32` to Veldrid, so it needed no change. Headless tests cover small=UInt16 / >65k=UInt32 selection, the `Indices` 16-bit-view throw, `MeshAssembler` past the ceiling, and `MeshBuilder` fusing across the boundary; an on-device `[GpuFact]` renders a triangle referenced only by indices > 65535 (which would vanish if the buffer were wrongly 16-bit) and asserts the 32-bit and 16-bit paths rasterize identically (verified on Metal; the same Veldrid format path drives D3D11 + Vulkan in golden CI). SemVer: additive (no public type changed), so minor.

## 7.21.0

Additive (`KhaozEngine.Collision`): capsule (pill) collision. New static `CapsuleCollision`, a sibling of `CircleCollision`, where a capsule is the segment `[a, b]` inflated by a radius. Three tests: `bool Intersects(Vector2 a, Vector2 b, float capsuleRadius, Vector2 circleCenter, float circleRadius)` (circle vs capsule), `bool Contains(Vector2 a, Vector2 b, float capsuleRadius, Vector2 point)` (point in capsule), and `bool Intersects(Vector2 a1, Vector2 b1, float radiusA, Vector2 a2, Vector2 b2, float radiusB)` (capsule vs capsule). All reduce to a distance compared against the summed radii, and a degenerate capsule (`a == b`) reduces exactly to a circle (the circle-vs-capsule and capsule-vs-capsule overloads then match `CircleCollision.Intersects`). Touching counts as intersecting (`<=`), matching `CircleCollision`. Built on a new building block: `float Segment2D.SegmentToSegmentDistance(Vector2 a1, Vector2 b1, Vector2 a2, Vector2 b2)`, the shortest distance between two segments (clamped closest points, not infinite lines; 0 for crossing segments), with the standard closest-point-between-segments solve (Ericson) and the degenerate reductions (one endpoint-pair collapsed -> point/segment, both -> point/point). Like the rest of `Collision`, the math is deterministic explicit-component arithmetic (no `Vector2.Dot`/`Length` helpers) so it stays bit-stable for lockstep sims. Headless tests cover point in/out/cap/on-surface, circle grazing/overlap/disjoint, capsule-capsule parallel/crossing/disjoint, the `a == b` circle case (incl. equivalence to `CircleCollision`), and `SegmentToSegmentDistance` parallel/crossing/disjoint/degenerate. No behaviour change to existing members; SpaceGame adopts it to model the Slath as a core capsule plus a few tentacle capsules.

## 7.20.1

Fix (`KhaozEngine.Gpu`): the window went black after going fullscreen (and equally after maximising or drag-resizing) on Windows. `VeldridGpuDevice` wrapped `MainSwapchain.Framebuffer` once at construction and `SwapchainFramebuffer` always returned that cached wrapper. On the D3D11 backend (Veldrid's default on Windows) and Vulkan, `Swapchain.Resize` disposes the old backbuffer framebuffer and builds a brand-new object, so after any resize the cached wrapper dangled on a disposed framebuffer; every subsequent frame cleared and composited into the dead target while the live, resized backbuffer was presented untouched - a black screen that persisted until the window closed. Metal kept the same swapchain-framebuffer object (it resolves a fresh drawable each frame), so the dev box never reproduced it - the bug was Windows/D3D11-only. `ResizeSwapchain` now re-wraps `MainSwapchain.Framebuffer` after the resize, but only when the underlying object actually changed (a `ReferenceEquals` check, so Metal keeps its stable wrapper with no churn). No public API change: `IGpuDevice.ResizeSwapchain`/`SwapchainFramebuffer` signatures are unchanged, and the offscreen render targets + camera aspect already tracked the resize correctly (`Scene3D.EnsureSize`), so the swapchain wrapper was the only stale link. Behaviour-only fix; the swapchain path needs a live window, so no headless test can reproduce it (the GPU test device has no swapchain) - verified by running fullscreen on Windows. A game adopts the fix by bumping its `KhaozEngine.Game3D`/`Game2D` pin to 7.20.1.

## 7.20.0

Fix (`KhaozEngine.Updates`): the Windows auto-updater could never replace its own binaries, so any update that changed the updater itself rolled back. Two root causes. (1) Self-lock: the updater shim ran in place from the install dir, and on Windows a process holds an exclusive lock on its own loaded `.exe`/`.dll`, so copying the staged `HardpointUpdater.dll` over the running one always failed (10/10 retries then roll back). (2) Too-short lock window: a just-exited game can keep its DLLs locked for several seconds (slow native/GPU teardown, antivirus scan-on-close), longer than the old 15s parent-wait plus 5s (10 x 500ms) copy-retry budget allowed, so `Hardpoint.Core.dll` failed and the rollback's restore failed too.

Fix for (1): self-relocation. When the updater is launched from inside the install dir, stage 1 copies just its own dependency closure (the host quartet `<app>.exe`/`.dll`/`.runtimeconfig.json`/`.deps.json` plus the managed assemblies its `.deps.json` lists, each resolved to a flattened filename) into `<AppDataDir>/updater-relocate/<version>` and re-launches that copy with `--relocated`. Stage 2 runs from the scratch dir, where the install-dir updater binaries are no longer locked, applies in place, relaunches the game, and fires a detached OS one-shot to delete its own scratch dir after it exits. The scratch dir lives under the game's own app-data dir (never a shared/system temp location), and `UpdateService` sweeps any stale `updater-relocate/*` on boot, so nothing is left behind on the machine. Relocation is Windows-only (`SystemUpdaterEnvironment` reports a self-exe path only on Windows; POSIX replaces a running executable's inode in place) and degrades to an in-place apply if the closure cannot be staged. Fix for (2): the parent-exit wait is raised 15s to 30s and the per-file copy-retry budget 10 to 40 (x 500ms = 20s); the fast path (an unlocked file copies on the first attempt, no delay) is unchanged.

New public API: `IUpdaterEnvironment` gains `ReadAllText`, `GetSelfExecutablePath`, `GetSelfBaseDirectory`, `LaunchRelocatedUpdater`, and `ScheduleDirectoryDeletion`; `ApplyUpdateConfig` gains `AppDataDir`; `UpdateApplier` gains the pure, unit-tested `ResolveUpdaterClosure(depsJsonText, apphostFileName)` and now accepts a `--relocated` flag. Headless tests cover the relocation handoff (closure copied, no apply, config kept for stage 2), the relocated apply (in-place copy plus scratch-cleanup scheduled), the outside-install / POSIX no-relocation path, and closure resolution (host quartet, flattened deps paths, malformed/empty deps). A game adopts the fix by rebuilding its updater shim against `KhaozEngine.Updates` 7.20.0.

## 7.19.0

Additive (`KhaozEngine.Render2D`): fonts no longer need a system-font path. The engine ships a bundled default face (Roboto Regular, Apache-2.0) embedded in the package and a key-based font registry, so a game never hard-codes the macOS-only `/System/Library/Fonts/Supplemental/Arial.ttf` (which threw `DirectoryNotFoundException` on first-run Windows/Linux). New byte overload `SpriteFont LoadFont(byte[] ttf, float pixelHeight, int oversample = 1)` on `Render2DSurface`/`Render2DContext` (and `Render2DCore`) exposes the already-internal byte bake path, killing the path dependency. New `SpriteFont LoadDefaultFont(float pixelHeight, int oversample = 1)` bakes the embedded face; the bytes are reachable directly via the new static `DefaultFont.Bytes` (read from the assembly manifest once, then cached). New `FontManager` mirrors `AudioSystem`'s register/resolve shape: `RegisterFont(string key)` probes `{ContentDirectory}/{key}.ttf` then `.otf` (key == path under the dir without extension, default dir `{AppContext.BaseDirectory}/assets/fonts`), `RegisterFont(string key, byte[] ttf)` registers raw bytes (and can override a reserved key), `IsFontRegistered`/`GetFontBytes`/`TryGetFontBytes` resolve, and the reserved `FontManager.DefaultKey` (`"default"`) is pre-registered to the embedded face. Resolution is GPU-free (headless-testable). Turn-key sugar `SpriteFont LoadFont(FontManager fonts, string key, float pixelHeight, int oversample = 1)` bakes straight from a key. The four engine samples (GuiSample, Render2DSample, MiniGame, WindowingSample) switched to `LoadDefaultFont`, so the macOS system-font path is gone from the repo. The existing `LoadFont(string path, ...)` overload is unchanged. Headless tests cover the embedded default baking through the device-free CPU path, key resolution (bytes, content-dir probe incl. nested keys, missing-file throw, override, unknown-key throw/try, default content dir). No behaviour change to existing members.

## 7.18.0

Skinned meshes render correctly in the WINDOWED Veldrid/Metal swapchain-present context (they had rendered as a screen-spanning garbage triangle - SpaceGame's tentacles). Root cause, bisected live: the skinned vertex shader's bone-palette buffer ARRAY read corrupts past element 0 in that context - only `bones[0]` survives; a constant `bones[1]` or any data-dependent index reads garbage - independent of buffer type (uniform/SSBO), binding (range/whole/dynamic), per-draw dynamic offset, and submit structure; headless/fenced rendering is always clean, which is why the unit tests never caught it. A `texelFetch` texture read dodges the corruption but vertex-stage texture data did not deliver on this Veldrid/Metal path. Fix: `Scene3D` now skins skinned meshes on the CPU. New public `ModelVertex SkinningMath.SkinVertex(in SkinnedVertex, ReadOnlySpan<Matrix4x4> composedBones)` (an exact mirror of the shader's weight blend, validated by the existing rest-vs-bent GPU tests) deforms each draw's vertices, which are drawn through the proven-clean no-bone `ModelRenderer` pipeline via a transient deformed-vertex stream + per-draw instance stream. The public skinned API (`Scene3D.LoadSkinnedMesh`/`DrawSkinned`) is unchanged and presentation-only as before; per ~192-draw frame the CPU cost is sub-millisecond. `SkinnedModelRenderer` (the GPU bone-palette path) is retained dormant and is correct headless. Also new public: `Render3DPreview.ReadbackRgba()`, a CPU readback of the preview target for diagnostics/snapshot use. Shipping from the same investigation: a GPU use-after-free fix - grown-out instance/bone buffers are now RETIRED and freed only at renderer disposal, never disposed inline while a prior frame's command list may still read them on the GPU (`ModelRenderer`/`SkinnedModelRenderer`); and `GpuFrameCapture.ArmNext(path)`, a one-shot Xcode Metal GPU capture (`.gputrace`) of one whole frame bracketed between swapchain presents (needs `MTL_CAPTURE_ENABLED=1`; no-op off Metal). Headless GPU regression tests added for many skinned draws, slot isolation, MatchViewport, and reflected (det=-1) bones. A manual windowed repro (`SlathRepro`, deliberately kept out of the solution) guards the fix, since the bug cannot be reproduced headless. No breaking API changes.

## 7.17.0

Additive (`KhaozEngine.App` + `KhaozEngine.Persistence`): a generic local install/update stamp so any game can show when the current app version first ran on a machine. New serializable record `AppInstallStamp { string Version; DateTime FirstInstalledAtUtc; DateTime UpdatedAtUtc; }` and the pure resolver `AppInstallStampResult AppInstallStamp.Resolve(AppInstallStamp? previous, string currentVersion, DateTime utcNow)` (with `readonly record struct AppInstallStampResult(AppInstallStamp Stamp, bool Changed)`). The resolver is storage-free and deterministic - `utcNow` is injected, there is no hidden `DateTime.UtcNow`, so headless / snapshot replay stays stable. First run (previous null) sets both dates to `utcNow` and reports changed; a same-version re-run returns the previous stamp by reference and reports not changed; a different version (upgrade OR downgrade - ordinal string inequality only, no semver ordering) preserves `FirstInstalledAtUtc` while bumping `Version` + `UpdatedAtUtc` and reports changed. A null `currentVersion` throws `ArgumentNullException`. Optional thin convenience in `KhaozEngine.Persistence` (which already depends on `App`): the extension `AppInstallStampResult SettingsManager<T>.StampInstall(Func<T,AppInstallStamp?> read, Action<T,AppInstallStamp> write, string currentVersion, DateTime utcNow)` resolves against the manager's live settings and, only if changed, writes the stamp back via the setter and calls `Save()` (a no-op run does not save). Recommended pattern: store an `AppInstallStamp` field on the game's existing settings/save DTO, call `Resolve` (or `StampInstall`) once at boot, persist if changed; the engine does not impose a separate file. `BuildDate` (the build's release date) stays a per-game build property surfaced via `BuildMetadata` and is deliberately out of scope here - this feature is only the local first-ran/updated stamp. Headless tests cover first run, no-op re-run, upgrade, downgrade, null-version, and the persistence convenience (writes+saves on change, no save on no-op). No behaviour change to existing members.

## 7.16.0

Additive (`KhaozEngine.Audio`): `AudioSystem` gains a loaded-check and first-available SFX play. `bool IsSfxLoaded(string name)` reports whether a name resolved to a loaded buffer (registered-but-file-missing returns false), so a game can ask "is this loaded?" without tripping the unknown-name warn-once. New first-available overloads `bool PlaySfx(IReadOnlyList<string> candidateKeys, float volume = 1f, float pitch = 1f)` and `bool PlaySfx3D(IReadOnlyList<string> candidateKeys, Vector3 position, float volume = 1f, float pitch = 1f)` take candidate keys in priority order and play the first loaded one (reusing the existing gain math + never-disables-music guard), returning true; a null/empty list is a no-op returning false, and an all-unloaded list warns once (deduped on the joined list, not per call) and returns false. Lets consumer games (Hardpoint first) do per-entity sound variants with a shared fallback ("play `towers/railgun/fire` if loaded, else `towers/default/fire`") while the fallback CONVENTION stays game-side: the engine provides the primitive, the game builds the candidate list. The single-key `PlaySfx`/`PlaySfx3D` overloads are unchanged (`PlaySfx("x")` still binds to the string overload; no overload ambiguity), and behaviour of every existing member is unchanged.

## 7.15.0

Additive: new `GuiSurface.HoverCaptured` property (`KhaozEngine.Gui`). True when the pointer's CURRENT position
is inside any widget rect reserved this frame (the same per-frame `_blocked` set `PointerCaptured` tests), but
against the live position rather than the press origin, and with no press-in-progress guard. `PointerCaptured`
gates on the press origin and early-returns unless a press is down or just released, so it answers "did the
user press on the UI"; there was no equivalent for "is the cursor merely over the UI right now". Games need
that to suppress world HOVER affordances (tooltips, hover reticles, hover highlights) while the cursor sits
over a panel without clicking. `IsHovering`/`HoveredRect` don't cover it: `_hoveredRect` is only set by
interactive widgets (Button/IconButton/Slider), whereas `_blocked` is also populated by `Panel`, so
`HoverCaptured` covers panel backgrounds too. No behaviour change to existing members.

## 7.14.0

Additive: new `KhaozEngine.Sfx.Tool` package, the `ke-sfxbake` dotnet tool (`PackAsTool`, rides the shared
version line like `ke-updater`). A manifest-driven bulk SFX generation + bake pipeline usable by every game.
It reads a per-game `sfx.manifest.jsonc` (routed through the engine `Jsonc` read policy: comments + trailing
commas), generates each effect via the ElevenLabs text-to-sound-effects REST API directly (not the auditioning
MCP, so it batches and runs unattended; key from `ELEVENLABS_API_KEY`), normalizes/encodes with ffmpeg or
oggenc, and writes into that repo's asset tree. It is author-time tooling, not a runtime package: no game
references it via `<PackageReference>` and it is in no umbrella metapackage.

Per-entry manifest schema: `key`, `prompt`, optional `durationSeconds` (0.5-30), optional `promptInfluence`
(0..1), `format` (`ogg` default | `wav`), `channels` (`mono` default | `stereo`), and `out` (resolved relative
to the manifest file). Format policy baked in as defaults: OGG Vorbis mono at ~q5 (8-9x smaller than WAV at
scale); mono is the default because OpenAL only spatializes mono sources; `wav` output is forced to 16-bit PCM
44.1 kHz (the only WAV `KhaozEngine.Audio`'s `WavDecoder` accepts); the API source is requested at high fidelity
(`mp3_44100_192` by default, or `pcm_44100` via `--source-format`) for a single lossy step before encode.

Idempotency + cost control: each output gets a `.sfxmeta` sidecar holding a hash of (prompt + duration +
influence + format + channels + model + source format); a re-run skips entries whose output exists and whose
hash is unchanged, regenerates only changed/new ones, and `--force` regenerates all. `--dry-run` prints the
generate/skip plan and an estimated credit cost (ElevenLabs API list rate, approximate) and spends nothing.
Preflight detects a usable Vorbis encoder (an ffmpeg built with libvorbis, or `oggenc` from vorbis-tools) and
fails with a clear remediation message rather than emitting bad/stereo-only OGG, since stock Homebrew ffmpeg has
no libvorbis. The network and encoder sit behind `IElevenLabsSfxClient` / `IAudioEncoder` (plus an
`ISfxFileSystem` / `IProcessRunner` seam) so the whole pipeline is headless-tested with no network, API, or
audio device. Authoring each game's manifest and wiring play keys is separate per-game work.

## 7.13.0

Additive: JSONC (JSON with `//` and `/* */` comments and trailing commas) is now the documented engine standard
for hand-authored config, content manifests, settings, and saves. New `KhaozEngine.Serialization.Jsonc` class is
the single canonical read policy every engine JSON load routes through, with one accessor per System.Text.Json
reader (`Jsonc.Options` for `JsonSerializer`, `Jsonc.DocumentOptions` for `JsonDocument`, `Jsonc.NodeOptions` for
`JsonNode`) plus convenience helpers (`Deserialize<T>`, `DeserializeFile<T>`, `ParseDocument`, `ParseNode`).
`JsonDefaults.TolerantRead` now returns the same instance as `Jsonc.Options` (was an equivalent but separate
options object; behaviour unchanged, still case-insensitive + comments-skipped + trailing-commas). Routed the two
call sites that still parsed JSON with their own or no options through the shared policy: `JsonSchemaValidator`
(dropped its private `DocOptions`) and `WorldSerializer.Load` (its bare `JsonNode.Parse` now accepts comments and
trailing commas, so hand-edited saves load). `ConfigLoader` and `KhaozEngine.Persistence` already used
`JsonDefaults.TolerantRead`, so they inherit the canonical instance with no change.

Write side is unchanged and stays plain JSON by design: System.Text.Json cannot emit comments, so generated files
(settings, saves) are written with `JsonDefaults.IndentedWrite` and signed/wire formats (the `KhaozEngine.Updates`
manifest, the AOT apply-update config) keep their own strict options. JSONC is a read-time convenience; authored
files keep their comments because the engine only reads them, never rewrites them in place. No public API removed.

## 7.12.0

Fix + additive: multi-instance skinned meshes now render correctly. Drawing more than one skinned mesh in a
frame, each with its own bone palette, previously rendered every skinned draw past the first as invisible or
full-screen garbage (SpaceGame's many-tentacle creature hit this; the 7.11.0 GPU test only ever drew one skinned
mesh per frame). Root cause: indexing a single shared bone buffer by a per-instance offset mis-fetched on the
Metal/Veldrid backend for every draw after the first. The bone palette is now a DYNAMIC-OFFSET uniform buffer:
each skinned draw's bones occupy a per-draw slot, and the draw rebinds the bone buffer with that slot's byte
offset so the vertex shader reads `bones[0..N]` for its own mesh (no per-instance index). Each skinned
`DrawSkinned` is now its own `instanceCount=1` draw (skinned meshes are no longer GPU-instanced); one skinned
mesh has at most 128 bones (`MaxBonesPerDraw`), and a mesh over the cap throws. The public skinned API
(`Scene3D.LoadSkinnedMesh` / `DrawSkinned` / `UnloadSkinnedMesh`) is unchanged; bone matrices, the lit colour
path, and determinism semantics are unchanged. Additive in `KhaozEngine.Gpu`: dynamic-offset resource bindings
(`GpuResourceLayoutElement(... dynamic: true)`, the `GpuBufferRange` bindable, and
`IGpuCommandList.SetGraphicsResourceSet(slot, set, dynamicOffset)`) plus an offset overload of
`SetVertexBuffer`.

## 7.11.0

Runtime skinned / deformable mesh support in Render3D. New `Scene3D.LoadSkinnedMesh` /
`DrawSkinned` / `UnloadSkinnedMesh` add GPU bone-palette skinning: a smooth mesh bends under
pure code control (tentacles, limbs, cables, soft-body), one skinned draw replacing many rigid
segments. `SkinnedMeshBuilder.BuildTube` generates a procedural tube weighted to a bone chain;
`GltfLoader.LoadSkinned` reads authored glb rigs (JOINTS_0/WEIGHTS_0 + inverse-bind, embedded
images still ignored); `PolylineFrames.Build` turns a chain of points into joint transforms.
`DrawSkinned` takes per-frame joint world transforms (the mesh's `RestPose` = no deform) and
composes them with the skin's inverse-bind. Every skinned draw's bones share one growable
structured buffer, indexed per-instance, so instances of one mesh draw in a single instanced
call. Skinning rewrites position + normal only; the lit colour path
(`albedo = vColor * vTint * texRgb`), tint, and texture semantics are unchanged. Bones are
independent joints (no implicit parent hierarchy): the caller supplies each bone's world
transform, so forward kinematics for a chain is the caller's responsibility (PolylineFrames or a
consumer's per-segment layout). New types: `SkinnedVertex`, `SkinnedGltfMesh`,
`SkinnedMeshHandle`, `Axis`, `SkinningMath` (pure, headless-testable). Presentation only: must not
touch sim/RNG/netcode.

## 7.10.0

Additive (non-breaking): new `KhaozEngine.Determinism` package with `DeterministicFpScope`, cross-platform control
of the CPU floating-point environment for fixed-tick / lockstep sims. A fixed-seed, fixed-input host sim can drift
across threads, machines, and even process runs because the per-thread FP control register (ARM64 `FPCR`, x86
`MXCSR`) is uncontrolled: its rounding mode and flush-to-zero / denormals-are-zero flags are not guaranteed to
match, and a native library on the thread can change them. Different flags give different low bits, which compound
over thousands of ticks (the SpaceGame determinism tripwire produced two different final states from one seed on
one machine). `DeterministicFpScope.Enter()` (RAII, allocation-free) saves the current FP environment, installs the
IEEE default (round-to-nearest-even, FTZ/DAZ off, FP traps masked), and restores on dispose; `DeterministicFp.
SetCanonical()`/`Restore()` are the set-once-per-sim-thread form, and `DeterministicFp.IsSupported` reports whether
the platform is wired up (unsupported -> safe no-op, never corrupts FP state).

Implemented over the platform C library's `<fenv.h>` via pure-managed P/Invoke (a per-OS `DllImportResolver`; no
native build asset, packs through the existing pipeline). Canonical state is a zeroed `fenv_t` on macOS/Linux arm64
(a zero write sets FPCR=0, which is round-to-nearest + FTZ-off + traps-masked by the ARM architecture itself),
`FE_DFL_ENV` on x64 (glibc/musl sentinel `(fenv_t*)-1`; macOS/Windows resolve the
`_FE_DFL_ENV` symbol, else fall back to the captured startup environment). Works on arm64 and x64 across macOS,
Linux, and Windows. The package is wired into `KhaozEngine.Foundation`, so the `Game2D`/`Game3D`/`Server` umbrellas
expose it transitively.

Scope: this controls the FP *register* only. It does NOT fix non-determinism from JIT *codegen* (FMA contraction
via `MathF.FusedMultiplyAdd`, auto-vectorization / reduction order). See the new "Deterministic floating point"
section in `docs/USING-KHAOZENGINE.md` for the guidance (fix operation order, avoid fused/vectorized forms for
state you hash or send over the wire). Ships with a headless repro harness in `KhaozEngine.Tests` that corrupts the
thread's rounding mode and asserts the mini-sim is byte-identical across repeated runs and across main / thread-pool
/ dedicated threads with the scope active; a CI step runs the determinism tests under `DOTNET_TieredCompilation` 0
and 1.

## 7.9.0

Fix + additive (non-breaking): `KhaozEngine.Gui` hover glow now reads as a natural soft bloom instead of a hard
amber/blue rim hugging the widget edge. The old `GuiDraw.HoverGlow` expanded the glow quad by only half the
softness, so the SDF soft falloff was truncated at ~50% coverage on the quad's own flat edge and looked like a
tacked-in outline. The glow (and the `FillStyled` drop shadow) now route through a shared `GuiDraw.SoftRoundedQuad`
helper that keeps the SDF box body-sized (so coverage peaks on the body outline) while expanding the quad well past
it, letting the falloff fade smoothly to zero before the quad edge; the body is drawn on top, hiding the steep
inner half so only the soft outer halo shows. `GuiStyle.Modern`'s glow/shadow alphas were retuned (`GlowColor` a
`0.35 -> 0.5`, `GlowSize` `10 -> 11`, `ShadowColor` a `0.40 -> 0.55`) for the new look. Applies to every widget
honouring `GlowColor`/`GlowSize` (`Button`, `GuiSurface`, `Slider`, `Toggle`, `Dropdown`, `TextInput`).

To support this, `SpriteBatch.DrawRounded` gains an optional `inset` parameter (default `0`, byte-identical to
today): it shrinks the SDF box by that many draw units on every side WITHOUT shrinking the quad, giving the
rasterised quad fragments beyond the shape's `d=0` edge for a soft falloff to fade across. The flat `GuiStyle`
fast path (glow off) and all non-`inset` `DrawRounded` callers stay byte-identical; existing goldens are unmoved.
New gated golden `gui_button_glow` (Metal baked; D3D11/Vulkan grids bake in CI).

## 7.8.0

Additive (non-breaking): the remaining retained `KhaozEngine.Gui` widgets gain a `GuiStyle Style` field (mirroring
the retained `Button`) so they inherit the 7.7.0 modern look (rounded corners, soft shadow, vertical gradient,
hover/active glow) when a modern style is set, and stay byte-identical on the flat default. The gated 2D goldens do
not move.

- **Retained widgets adopt `GuiStyle`.** `Slider`, `Toggle`, `Panel`, `Dropdown`, `TextInput`, `PopupPanel`, and
  `ScrollablePanel` each gain a public `GuiStyle Style = GuiStyle.Default;` field and route their body/handle/thumb
  fills through `GuiDraw.FillStyled` (and `GuiDraw.HoverGlow` where an active state exists). Each widget keeps its
  own per-element colour fields (e.g. `Slider.TrackColor`/`ThumbColor`, `Toggle.OnColor`/`OffColor`,
  `Panel.Color`) - those are passed as the `bodyColor`/`borderColor` arguments; only the modern affordance knobs
  (corner radius, shadow, gradient, glow) come from `Style`. Set `widget.Style = GuiStyle.Modern` to opt the whole
  retained set into the modern look.
- **Per-widget rounding decisions** (mirrors the immediate-mode `GuiDraw.DrawSlider` reference, where the thin
  track stays flat and only the knob rounds):
  - `Slider`: track + accent fill stay flat; only the thumb takes `Style` (glow behind it while dragging).
  - `Toggle`: track rounds into a pill and the thumb rounds (glow behind the track while on + enabled).
  - `Panel`: the body rounds; the panel keeps its own `BorderThickness` field (the style's is overridden by it).
  - `Dropdown`: the trigger and the open list container round (glow behind the trigger while open); the
    option-row highlights stay flat.
  - `TextInput`: the field box rounds (focus glow behind it while focused); the caret stays a flat sliver.
  - `PopupPanel`: the body rounds and the footer buttons inherit the modern affordances (their palette now derives
    from `Style`); the full-screen scrim and the title bar stay flat.
  - `ScrollablePanel`: the background container rounds (the row clip stays a rectangular scissor).
- **Byte-identical default.** Every routed fill passes `Style with { BorderThickness = <element's existing value> }`
  so the flat default (`GuiStyle.Default`, which `IsFlat`) collapses back to the exact prior `Fill` + `Border`
  calls (borderless thumbs use `BorderThickness = 0`); `HoverGlow` is a no-op when `GlowSize == 0`. No public colour
  fields were removed.

## 7.7.0

Additive (non-breaking): modern UI primitives + a procedural icon system in `KhaozEngine.Gui`, all opt-in and
defaulted off so existing screens render byte-identically (the gated 2D goldens for the plain path do not move).

- **Gui modern primitives.** New `GuiStyle` fields, all defaulted to today's flat look: `CornerRadius`,
  `ShadowSize`/`ShadowColor`/`ShadowOffset`, `FillMode` (`GuiFill.Solid`|`VerticalGradient`) with
  `GradientTopScale`/`GradientBottomScale`, and `GlowColor`/`GlowSize` hover glow. New `GuiStyle.Modern` preset
  wires rounded corners + soft shadow + gradient + glow onto the default palette, and `GuiStyle.IsFlat` /
  `GuiStyle.ScaleRgb` support the draw path. Centralized in `GuiDraw` (`FillStyled`/`HoverGlow`), so the
  immediate-mode `GuiSurface` widgets (`Panel`/`Button`/`Slider`) and the retained `Button` (which already carries
  a `GuiStyle`) inherit it. The other retained widgets (`Slider`/`Toggle`/`Panel`/`Dropdown`) still carry their own
  flat colour fields and keep today's look until a follow-up migrates them onto `GuiStyle`. New
  `GuiSurface.Panel(rect, style)` overload.
- **SDF SpriteBatch path (Render2D).** The shared sprite vertex widened from 32B to 64B with rounded-rect SDF
  attributes; the fragment shader branches on a per-vertex mode flag (flag 0 = the prior `texture * vColor`,
  byte-identical for every existing draw; flag 1 = alpha shaped by an Inigo-Quilez rounded-box SDF with `fwidth`
  AA, computed in uniform control flow, used for corners/shadow/glow/border-ring). New public
  `SpriteBatch.DrawRounded(...)` and a vertical-gradient `Draw(tex, dest, top, bottom)` overload (the latter using
  the already-interpolated per-vertex colour).
- **Icon system (Gui).** `IconAtlas` CPU-bakes a core outline icon set (coin, heart, skull, crosshair, gear, play,
  pause, close, check, plus, minus, chevron-l/r/u/d) into one tintable alpha-mask atlas (no shipped asset, the
  `VfxTextures` pattern), exposed via a string-keyed registry. Games register their own icons
  (`IconAtlas.Register`). Draw with `GuiSurface.Icon(rect, id, tint)`. New composed widgets
  `GuiSurface.IconButton` and `GuiSurface.StatChip`.

## 7.6.0

Additive (non-breaking): a reusable "attention" pulse VFX in the 2D VFX module, so any game can flag a point of
interest (pickups, quest markers, objectives) with expanding sonar-ping rings and twinkling glints instead of a
bespoke per-game aura. Stateless and time-driven, mirroring `EnergyBeam`.

- `KhaozEngine.Render2D.Vfx.AttentionBeacon`: new static
  `Draw(SpriteBatch batch, Texture2D? ring, Texture2D? glow, Vector2 center, in AttentionBeaconParams p, float timeSeconds)`.
  Draws additively (the batch's blend mode is saved and restored): `RingCount` soft sonar rings expanding from
  `InnerRadius` to `MaxRadius` over `RingPeriod`, evenly phase-staggered and fading to zero at the rim, plus
  `GlintCount` glints placed at deterministic golden-angle offsets (no per-frame RNG, no allocation), each
  twinkling on its own phase at `TwinkleRate`. A null `ring` skips the rings; a null `glow` skips the glints;
  `RingCount = 0` and `GlintCount = 0` draw nothing.
- `KhaozEngine.Render2D.Vfx.AttentionBeaconParams`: new immutable `record struct` of tunables (`Color`,
  `Intensity`, `RingCount`/`RingPeriod`/`InnerRadius`/`MaxRadius`/`RingThickness`, `GlintCount`/`GlintRadius`/
  `GlintSize`/`TwinkleRate`/`GlintStyle`) with a `Default` preset. `RingThickness` is a relative band-thickness
  multiplier (1 = the ring texture's native band). New `GlintStyle` enum: `Disc` (soft dot) or `Star` (a tiny
  4-point sparkle from two crossed soft quads; the default).
- `KhaozEngine.Render2D.Vfx.VfxRenderer`: new
  `DrawAttentionBeacon(SpriteBatch batch, Vector2 center, in AttentionBeaconParams p, float timeSeconds)`, which
  forwards to `AttentionBeacon.Draw` with the owned ring (rings) and glow (glints) textures, mirroring `DrawBeam`.

Existing render paths and goldens are unaffected (new draw only).

## 7.5.0

Additive (non-breaking): a generic translucent filled-fan primitive on the Render3D debug overlay, so a consumer
can fill an arbitrary star-shaped polygon (e.g. a turret's line-of-sight area) instead of only quads and discs.

- `KhaozEngine.Render3D.DebugFillShapes`: new
  `FilledFan(List<Vector3> tris, Vector3 center, IReadOnlyList<Vector3> rim, bool closed)`. For each adjacent rim
  pair it appends the triangle `(center, rim[i], rim[i+1])`; when `closed` it also appends the wrap triangle
  `(center, rim[last], rim[0])` to seal the loop. Same CCW winding convention as `FilledCircle` (wind the rim CCW
  about the desired facing normal). Degenerate input (fewer than 2 rim points) appends nothing.
- `KhaozEngine.Render3D.Scene3D`: new
  `DebugFilledFan(Vector3 center, IReadOnlyList<Vector3> rim, Color color, bool closed = true)` - queues the fan
  through the existing fill overlay (cleared in `Begin`, drawn under the debug lines), the arbitrary-polygon
  counterpart to `DebugFilledQuad`/`DebugFilledCircle`. The fill vertex buffer stays internal; this exposes the
  builder so games no longer have to reach into it.

## 7.4.0

Additive (non-breaking): point/segment geometry in `KhaozEngine.Collision`, the primitive swept (look-ahead)
collision needs so a fast mover cannot tunnel through a thin target between two frames.

- `KhaozEngine.Collision`: new `Segment2D` static class with
  `DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)` - the shortest distance from a point to the
  segment `[a, b]` (the clamped closest point, not the infinite line). `t` is the projection parameter of that
  closest point along `a -> b`, clamped to `[0, 1]` (`t ~ 0` near `a`, `t ~ 1` near `b`), so callers can order
  hits by position along a swept path. A degenerate segment (`a == b`) returns `|p - a|` with `t = 0`. The math
  uses explicit component arithmetic (no `Vector2.Dot`/`Length` helpers), bit-stable for lockstep sims, matching
  `CircleCollision`. Companion to the existing `CircleCollision` (circle/circle) and `GridRay` (grid raycast).

## 7.3.0

Additive (non-breaking): the auto-updater's reusable last-mile glue, so games adopt the updater with thin
per-game config only (feed URL, embedded public key, a one-line shim, a themed overlay).

- `KhaozEngine.Updates`: new read-only `IUpdateStatus` (implemented by `UpdateService`) so UI can present
  update state without the concrete service. `UpdaterShim.Main(args)` - a game's external updater exe is now
  one line (`return KhaozEngine.Updates.UpdaterShim.Main(args);`). `UpdateOverlayActions.Trigger(service)` +
  `ResolveAction(state)` - the default state-to-action wiring (`OverlayAction` enum). `ManifestToolCommands` -
  the command logic behind the new CLI.
- `KhaozEngine.Gui`: `UpdateOverlayView` (a headless-testable presenter over `IUpdateStatus` that raises
  `OnTrigger`/`Triggered` on a bound key/gamepad button) and `UpdateOverlayScreen` (a drop-in `Screen`
  wrapper, modal only while a panel is shown), themed via `UpdateOverlayTheme`. `KhaozEngine.Gui` now
  depends on `KhaozEngine.Updates` (pure .NET, acyclic).
- New `KhaozEngine.Updates.Tool` package: the `ke-updater` dotnet tool - `manifest`, `genkey`, `sign`,
  `verify` for RSA-2048 signed manifests. Wires the `--genkey`/`--sign` deferred in 7.0.0.
- `KhaozEngine.Updates` now bundles `templates/publish-update.sh` (a parameterized publish template) and a
  README "Adopting the updater" section. No change to the security model (signing stays mandatory; HTTPS +
  same-host; size/disk caps; fail-closed apply).

## 7.2.0

Additive (non-breaking): round (cylindrical) end-caps for `EnergyBeam` (`KhaozEngine.Render2D.Vfx`), so a beam
can read as a capsule/cylinder instead of a hard rectangle. The default keeps the original square ends, so every
existing caller and every committed golden is byte-identical.

- `BeamParams.Caps` (`BeamCap` enum, `None` | `Round`, default `None`): with `BeamCap.Round`, a soft disc cap of
  radius half the band's pulse-adjusted width is drawn at each endpoint of both the glow band and the core, so the
  caps sit flush with the band, scale with the pulse, and the wider glow band gets a larger cap than the thin core
  automatically. The glow cap is drawn under the core cap, matching the band draw order; both use the same
  per-band colour/alpha as the band itself.
- The round caps are independent of the endpoint flare: a beam with `FlareRadius = 0` and `BeamCap.Round` still
  has rounded ends. The disc is sampled from the same radial glow texture passed to `EnergyBeam.Draw` (the
  `glow` argument); with no glow texture the ends stay square. A degenerate (zero-length) beam draws nothing.
- New pure helper `EnergyBeam.RoundCaps(a, b, BeamCap, bandWidth, pulse)` returning an internal `BeamCaps` value
  computes the per-band cap geometry headlessly (covered by `EnergyBeamTests`); the private flare disc helper is
  now `DrawDisc`, shared by flares and caps.

## 7.1.0

Additive (non-breaking): textured, depth-interleaved billboards in `Scene3D` (`KhaozEngine.Render3D`), so
sprite-sheet frames can be drawn as depth-sorted quads inside the lit 3D scene alongside meshes. Unblocks
SpaceGame's mesh-rendering pivot (WS1.5): enemies/projectiles/effects render as textured billboards in the same
depth-buffered scene as the player/drone meshes until they become meshes in WS3. The colour-only billboard
overlay and every existing 2D/3D golden are byte-identical (no Hardpoint regression).

- `Scene3D.DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Vector4 sourceUv, Color tint,
  BillboardBlend blend = Alpha)`: queue a camera-facing quad sampling a sub-rect `sourceUv` (`(u0,v0,u1,v1)`,
  bottom-left to top-right) of a texture loaded with `Scene3D.LoadTexture`, multiplied by `tint`, with `Alpha`
  or `Additive` blend. A convenience overload without `sourceUv` samples the whole texture (`(0,0,1,1)`). An
  invalid/`default` texture handle draws nothing (no throw), mirroring the untextured-mesh fallback. Cleared each
  `Begin()` like the other per-frame queues.
- Depth-interleaved with meshes: unlike the colour-only `DrawBillboard` (an overlay drawn after the post chain
  with depth disabled), textured billboards draw INTO the model MRT alongside the lit meshes with the depth test
  on (less-or-equal) and depth write OFF. A nearer mesh occludes a quad behind it and a quad in front draws over
  a mesh behind it; depth write is off so overlapping quads blend in submission order (submit back-to-front for
  correct transparency). The whole MRT (meshes + textured billboards) then goes through the post chain together;
  the outline pass still keys off the meshes' normal/depth (the textured pass preserves attachments 1 & 2).
- `BillboardGeometry.Triangles(center, size, right, up, Vector4 sourceUv, positions, uvs)`: pure helper mapping
  the quad's corners onto a source-UV sub-rect for sprite-sheet frame selection (the existing full-square
  overload is unchanged). Swap `v0`/`v1` (or `u0`/`u1`) to flip a frame.
- `GpuBlendAttachment.PreserveDestination` (keep dst: src*0 + dst*1) and
  `GpuDepthStencilState.DepthTestLessEqualNoWrite` (depth test, no write) added to `KhaozEngine.Gpu` for the new
  pass; both are generic pipeline-state presets.

Tests: headless coverage of the UV-rect mapping and the submission-order run coalescing; backend-agnostic GPU
readback tests for sub-rect sampling, tint, blend, and occlusion both ways; a new committed golden
(`scene3d_texbillboard`) of a textured billboard depth-interleaved with a mesh.

## 7.0.1

### KhaozEngine.Updates: cap the version-check probe response

- `HttpUpdateSource.CheckLatestVersionAsync` now reads the `/latest` probe response with a bounded buffer
  before deserializing, instead of handing an unbounded body to `GetFromJsonAsync`. A hostile or compromised
  update host could otherwise stream an arbitrarily large body into the JSON parser (memory-exhaustion DoS).
  The response is a small fixed-shape `LatestVersionInfo`, so the read is capped at a new
  `HttpUpdateSourceOptions.MaxLatestVersionBytes` (default 64 KiB); an over-cap response is aborted and the
  check returns null, mirroring the existing `MaxManifestBytes` / `maxBytes` caps on `DownloadBytesAsync` and
  `DownloadFileAsync`. Malformed JSON and transport/IO errors on the probe now also return null (offline-safe)
  rather than throwing. Closes the residual risk flagged in the 7.0.0 updater-hardening review.

The whole engine shares one version line, so all packages bump to 7.0.1; only KhaozEngine.Updates changed, and
the change is additive (a new option with a safe default) with no API break.

## 7.0.0

### KhaozEngine.Updates: security hardening (BREAKING)

- Mandatory manifest signing. Manifests are RSA-2048 / SHA-256 / PKCS#1 signed; the client verifies a detached `manifest.json.sig` over the raw manifest bytes before parsing and refuses anything unsigned or signed by an untrusted key. `UpdateServiceOptions.TrustedPublicKeys` is now REQUIRED (at least one key), constructing `UpdateService` without one throws. New `ManifestSigner` / `ManifestVerifier` / `ManifestKeyPair` (pure BCL, no new dependency).
- Signed fields only for security decisions. `Required` is now a signed manifest field; the downgrade gate and the recorded version run against the signed manifest. The unsigned `/latest` response is a hint only.
- Feed transport locked to https + same host. `HttpUpdateSource` refuses any manifest, `.sig`, or file URL that is not https or not on the configured `ServerBaseUrl` host. `IUpdateSource` now exposes `DownloadBytesAsync(url, maxBytes)` (replacing `DownloadManifestAsync`) and `DownloadFileAsync` takes a `maxBytes` cap. The manifest and signature fetches are size-capped (`MaxManifestBytes`, default 64 MiB).
- Apply-time guards. Path-traversal rejection on both copy and delete lists (new `ApplyOutcome.AbortedUnsafePath`), reparse-point guards on staged sources and destinations (a destination symlink is removed before copy, and a failed removal fails closed rather than writing through the link), and macOS `codesign --verify --deep --strict` before relaunch (fail closed, rolls back and relaunches the old version on failure). The new manifest is installed only after the signature check passes. `IUpdaterEnvironment` gains `IsReparsePoint` and `VerifyCodeSignature`.
- Size and disk caps. Per-file (`MaxFileBytes`, default 4 GiB) and total (`MaxTotalDownloadBytes`, default 16 GiB) download caps, streaming overrun abort, and a free-disk pre-check.

The whole engine shares one version line, so all packages bump to 7.0.0; only KhaozEngine.Updates changed. Consumers using the updater must generate keys, embed the public key, and publish a signed manifest (SpaceGame).

## 6.6.0

Additive (non-breaking): a generic 2D VFX module under `KhaozEngine.Render2D.Vfx` (glowing sprites, animated
energy beams, rich pooled particles), plus per-quad additive blending on `SpriteBatch`. Engine-first
centralization driven by Nullwake's mining-VFX upgrade; nothing here is Nullwake-specific. The alpha render path
is byte-identical to 6.5.0 (existing 2D/3D goldens unchanged).

- `KhaozEngine.Render2D.BlendMode` (`Alpha` | `Additive`) and `SpriteBatch.BlendMode { get; set; }`: choose the
  compositing mode for subsequent draws. It can change mid-batch (per quad) without a new `Begin`, and painter's
  order is preserved across blend modes. Each `Begin` resets it to `Alpha`. The default `Alpha` path is unchanged
  (the run key, pipeline, and vertex output for alpha draws are identical, so output is byte-for-byte the same);
  additive draws go through a second pipeline built from `GpuBlendAttachment.Additive`.
- `KhaozEngine.Render2D.Vfx.Particle2DSystem`: a fixed-size, zero-allocation, screen-space particle pool (ring
  buffer) driven by a deterministic seeded `XorRng` (not `System.Random`). Per particle: velocity, constant
  acceleration (gravity), **drag** (velocity damping), horizontal sway, **rotation + angular velocity**, size
  lerp and colour lerp over life, and a per-particle **blend mode**. `Emit(in Particle2DEmitterConfig, Vector2
  origin, int count)` and a tint overload; `Update(float dt)`; `Clear()`; `ActiveParticles()` snapshots; and
  `Draw(SpriteBatch, Texture2D)` (per-particle blend) / `Draw(SpriteBatch, Texture2D, BlendMode)` (forced blend)
  - pass a 1x1 white pixel for solid squares or a baked glow dot for soft sprites.
- `KhaozEngine.Render2D.Vfx.Particle2DEmitterConfig` (immutable `readonly record struct`, `with`-derivable),
  `Particle2DEmission` (`Radial` | `Directional`-with-cone), and `Particle2DView` (read-only snapshot for tests
  and custom rendering). Build configs from data so a consumer keeps presets in content.
- `KhaozEngine.Render2D.Vfx.EnergyBeam.Draw(SpriteBatch, Texture2D white, Texture2D? glow, Vector2 a, Vector2 b,
  in BeamParams, float timeSeconds)`: an animated additive A->B beam - soft glow band under a bright core, with
  flowing dashes, brightness/width pulse, sideways jitter, and endpoint flares. Time-driven and stateless (no
  hidden mutable state). `BeamParams` is an immutable record struct with a `Default` preset.
- `KhaozEngine.Render2D.Vfx.VfxTextures`: CPU-baked VFX textures, no shipped asset. `BakeGlowPixels`/
  `BakeRingPixels` return tightly-packed RGBA8 (pure / headless); `BakeGlow`/`BakeRing`/`White` upload to a
  sampleable `Texture2D` on a `Render2DSurface` or snapshot `Render2DContext`.
- `KhaozEngine.Render2D.Vfx.VfxRenderer` (`IDisposable`): convenience owner that bakes a glow, a ring, and a 1x1
  white texture **at construction** and offers ready-made additive draws - `DrawGlow` (halos/flares/bloom),
  `DrawRing` (impact/shockwave), and `DrawBeam` (forwards to `EnergyBeam` with the owned textures). Exposes
  `GlowTexture`/`RingTexture`/`WhitePixel` to feed a `Particle2DSystem`.
- Trauma-based screen shake is unchanged: the existing `KhaozEngine.Effects.ScreenShake` (`Add`/`Update`/
  `Offset`/`Angle`, camera-independent, deterministic seeded noise) already covers the VFX brief's shake item, so
  no duplicate type was added. It reaches 2D consumers via the `Game2D` umbrella (since 6.4.0).

## 6.5.0

Additive (non-breaking): dynamic point lights in the lit mesh pass, for 2.5D mesh-composited games (SpaceGame's
mesh-rendering pivot, WS1). Zero-light rendering is byte-identical to 6.4.0; existing goldens unchanged.

- `KhaozEngine.Render3D.Scene3D.AddLight(Vector3 worldPos, Color color, float radius, float intensity)`: queues a
  per-frame dynamic point/effect light (muzzle flashes, explosions, thrusters, key projectiles). Cleared each
  `Begin()` like the instance queue. The lit fragment shader accumulates point-light diffuse plus cheap
  Blinn-Phong specular on top of the existing key+fill+ambient term, back-face gated like the key term, with a
  smooth windowed distance attenuation (1 at the light, easing to 0 at `radius`, scaled by `intensity`). Cel
  banding applies to point lights too when `CelBands >= 1`.
- `Scene3D.MaxPointLights` (16): the per-frame GPU budget. `AddLight` accepts any number, but only the first
  `MaxPointLights` queued are uploaded - the host picks the N nearest to the action per frame (CPU-side cull) so
  a dense bullet-hell stays bounded. The renderer defensively clamps and zero-fills the unused tail so a previous
  frame's lights never leak. The frame UBO grew from 176 to 688 bytes (header + two `vec4[16]` light arrays);
  `Params.y` carries the active count. Zero active lights leaves the shader's light loop unentered, so the lit
  term is bit-identical to the prior key+fill+ambient-only path (no Hardpoint regression).
- Transparent-background compositing for the 3D-under-2D path was already in place
  (`PixelPostProcessSettings.TransparentBackground`, since 5.70.0; `Render3DPreview` defaults it on): the model
  pass clears the colour target to alpha 0 and the palette/edge/blit post passes preserve the per-pixel alpha, so
  a captured `Texture2D` overlays a 2D background with alpha 0 outside the silhouette. Re-verified by the preview
  GPU test for this pivot; no code change needed beyond the point-light shader edit keeping the alpha path intact.

## 6.4.0

Additive packaging fix (non-breaking). No code or behaviour change; rendering byte-identical.

- `KhaozEngine.Game2D` umbrella now pulls in `KhaozEngine.Effects` (the trauma-based `ScreenShake` /
  game-feel package), so it flows transitively into `KhaozEngine.Game3D` too. Closes the last omni-package
  gap: `Effects` was previously the only leaf package no metapackage referenced, forcing 2D/3D consumers to
  add it by hand. A game already on `Game2D`/`Game3D` gets `Effects` for free after this bump; a game that
  referenced `Effects` directly can drop the explicit `<PackageReference>`.

## 6.3.0

Additive helpers (non-breaking) that unblock Nullwake's 6.x adoption. Rendering byte-identical; golden snapshots unaffected.

- `KhaozEngine.Primitives.Color`: scalar multiply `operator *(Color, float)` and the symmetric `operator *(float, Color)`
  scale all four channels including alpha (unclamped), matching `Vector4 * float` / legacy MonoGame `Color * float`.
- `KhaozEngine.Primitives.Color.Lerp(a, b, t)`: component-wise, unclamped, byte-identical to
  `System.Numerics.Vector4.Lerp` (delegates to it - no clamp, no rounding through bytes).
- `KhaozEngine.Windowing.InputManager.GetScrollIn(Rect bounds)`: integer scroll-notch delta when the pointer is over
  `bounds`, else 0 (scopes wheel scrolling to a region via the bounds helpers, no raw position check).

## 6.2.0

Cross-platform clip-space correction (port hardening). No behavior change on Metal (golden-snapshot byte-identical).

- New `KhaozEngine.Gpu.GpuClip.Correct(viewProj, caps)`: adapts a world-to-clip view-projection to the live
  backend's clip-space-Y convention (identity on Metal/D3D, flips clip-Y on inverted-Y backends like Vulkan,
  per Veldrid's `IsClipSpaceYInverted`). Applied at the GPU view-projection upload sites (`SpriteBatch`,
  `ModelRenderer`, `OverlayRenderer`), never to CPU world/screen / picking matrices, so render and picking stay
  consistent. Replaces the baked Metal-only assumption (the old TODOs in `Camera2D` / `ModelRenderer`).
- NOTE: the inverted-Y (Vulkan/D3D11) path is correct-by-construction from `GpuCapabilities` but is not yet
  validated on non-Metal hardware (no green non-Metal CI). Depth range is not remapped: all supported backends
  (Metal/D3D11/Vulkan) use a [0,1] NDC range; only unsupported legacy OpenGL would differ.

## 6.1.0

Post-6.0.0 cleanup batch: performance, internal dedup, and consistency. No public color/API breaks; two small
Gui behavior refinements are noted below.

- Render3D: `Render3DPreview.Capture` no longer blocks the CPU with a per-frame `WaitForIdle`. Live previews
  rely on same-queue submission ordering (CPU readbacks still fence via `GpuReadback`), so N live previews no
  longer cost N pipeline stalls per frame.
- Render3D: the line/fill/billboard overlay renderers now share one generic `OverlayRenderer<TVertex>` (vertex
  structs and `Draw` signatures unchanged; rendering byte-identical).
- Audio: music streaming is zero-alloc in steady state (reuses a preallocated 1-element queue scratch instead of
  allocating per processed buffer per frame). The `AudioSystem` catch blocks that disable audio now log at Debug
  (with the exception) so a silent backend failure is diagnosable.
- Ecs: `World.ForEach` overloads pool their per-call `Query` (and its backing lists) via `KhaozEngine.Pooling`,
  so steady-state `ForEach` is allocation-free; nested `ForEach` is unaffected (falls back to a fresh `Query`).
  `Query`'s public API is unchanged.
- Updates: best-effort cleanup catch blocks (temp / rollback / staging dir deletes) now log at Debug instead of
  swallowing silently.
- Gui (behavior): `PopupPanel`'s buttons now route through the shared `GuiDraw.DrawButton` / `GuiStyle` (gaining a
  border, a press affordance, and state priority). `Slider` (retained and immediate) now grabs only when the
  press began inside the track, via the new `Pointer.IsDragStartIn(Rect)`.
- Gui (behavior, fail-loud): `PopupPanel.Viewport` and `Tooltip.Viewport` no longer default to 960x540; an unset
  (`Vector2.Zero`) viewport now throws on use, so a missed assignment surfaces immediately instead of silently
  mis-positioning. Set the design viewport explicitly (callers that already set it are unaffected).
- Docs: added the `[ComponentId]` save-format-stability policy to `docs/USING-KHAOZENGINE.md` (new components get
  a stable id from creation; annotating an already-shipped component needs a paired `RegisterMigration`).

## 6.0.0

BREAKING. First 6.x release. New shared primitives leaf + uniform public color type.

- New package `KhaozEngine.Primitives` (zero-dependency leaf, `System.Numerics` only): `Color` (now with
  `FromHex`/`ToHex`), `DeterministicRng` (moved from `Ecs`, `StableHash` now public), `XorRng` (value-type PRNG,
  promoted from `Particles`), `MathUtil` (`Clamp01`/`Lerp`/`InverseLerp`), `ViewportMath` (`Fit`/`Cover`),
  `Easing` (moved from `Render2D`).
- **BREAKING:** the public color API across `Gpu`, `Render2D`, `Render3D`, `Particles`, and `Content` now takes
  `KhaozEngine.Primitives.Color` instead of `Vector4`. `IGpuCommandList.ClearColorTarget` takes `Color`.
  `Content.ColorHex` removed (use `Color.FromHex` / `Color.ToHex`). Internal GPU layout structs (vertex formats,
  std140 UBOs) stay `Vector4`. Rendering output is byte-identical (verified by golden snapshots).
- **BREAKING:** `KhaozEngine.Ecs.DeterministicRng` moved to `KhaozEngine.Primitives` (update using directives).
- `Ecs` save format: `WorldSerializer` now reads `FormatVersion` (throws `UnsupportedSaveVersionException` on
  unknown future versions), has a migration-registration seam (`RegisterMigration`), and supports
  `[ComponentId("key")]` for rename-stable component keys (with a duplicate-key guard).
- **BREAKING (behavior):** `Audio` random-track selection now uses the deterministic `DeterministicRng` (via
  `SetRng`) instead of `System.Random`. `SetRng`'s parameter type changed to `DeterministicRng`. The default is a
  fixed seed, so without calling `SetRng` the track order is reproducible and invariant across launches (call
  `SetRng` with a varying seed, e.g. from the clock, for per-launch variety). The rotation-pool track-set
  semantics from 5.71.0 are unchanged.
- Fix: `FileSettingsStorage` reads with `JsonDefaults.TolerantRead` (comments, trailing commas, case-insensitive),
  matching its write and `GameStorage`.
- Internal: single image-decode path via `ImageRgba` (`Render3D` no longer references `StbImageSharp` directly);
  `EntityCommandBuffer` playback dictionary pooled via `KhaozEngine.Pooling`; viewport-fit math centralized in
  `ViewportMath`. `WavSynth.WriteNoise` now uses `XorRng` (its placeholder noise samples differ slightly from
  5.x because `XorRng.NextFloat` uses a 24-bit mantissa; output is still deterministic for a fixed seed).

## 5.71.0 (custom 5.x line)

Scoped random-rotation pool in `KhaozEngine.Audio.AudioSystem`. A game can register every track (so
`PlayTrack(name)` plays any of them on demand) while restricting which tracks the random picker is allowed to
surface - e.g. keep menu music on a menu subset instead of letting a gameplay or death track boot on the menu.

- **`KhaozEngine.Audio`:** new `AudioSystem.SetRotationPool(IEnumerable<string>? trackNames)`. `null` (the
  default / unset state) keeps random rotation over ALL registered tracks, byte-for-byte the previous behaviour.
  A non-null pool scopes `PlayRandomTrack()` (the deferred boot first-play, `MusicEnabled = true` resume, and
  end-of-track auto-advance under `PlayMode.RandomRotation`) to only the named tracks that are registered. Names
  not registered are ignored; names resolve lazily, so it is safe to call before or after `LoadContent` and
  before or after the tracks are registered.
- `PlayTrack(name)` / `PlayTrack(index)` are unaffected: any registered track still plays on demand regardless
  of the pool. The "don't repeat the same track twice in a row" rule operates within the pool. A pool of size 1
  plays that track every time. If the pool resolves to no registered tracks (e.g. names not yet loaded) rotation
  falls back to ALL tracks with a one-time warning, so a misconfigured pool never silences music.
- Additive, back-compatible: existing callers that never touch the pool are unchanged.

## 5.70.0 (custom 5.x line)

Live render-to-texture for 3D model previews in `KhaozEngine.Render3D`: render a rotating model into a
sampleable `Texture2D` on the live device and composite it into a 2D `SpriteBatch`/Gui panel (unit inspectors,
shop / character-select previews, item icons). Fills the gap between `Render3DSurface` (window framebuffer only)
and `Render3DSnapshot` (separate headless device + CPU readback, for goldens/tooling).

- **`KhaozEngine.Render3D`:** new `Render3DPreview` (`IDisposable`). Built once from the live `AppWindow`
  (`new Render3DPreview(window, width, height)`); owns a dedicated `Scene3D` (isolated from the board scene) and
  a single offscreen render target reused every frame, so a spinning preview allocates no GPU texture per frame.
  Load preview meshes and frame the camera ONCE via `preview.Scene` (no per-frame re-upload, unlike the
  snapshot path), then each frame call `Capture(Action<Scene3D> drawFrame)` (queue the instance(s) with the
  current world matrix) to re-render in place and get the sampleable `Texture` back. `Resize(w, h)` re-allocates
  the target; sizes are clamped into `[1, MaxDimension]` (4096) by the pure, headless-testable
  `Render3DPreview.ClampSize`. The result runs on the live Metal/Veldrid device through the same
  `Scene3D.RenderInternal` (full stylized post chain), so the preview matches the on-screen look.
- **`KhaozEngine.Render3D`:** new `PixelPostProcessSettings.TransparentBackground` (default `false`). When set,
  the final blit keeps the per-pixel alpha "background marker" (geometry stays opaque, the cleared background
  stays transparent) instead of forcing opaque, so a scene composites cleanly over a 2D panel. `Render3DPreview`
  enables it (with the starfield off) by default. Existing on-screen rendering is unchanged (default opaque); all
  committed goldens are byte-identical.
- **`KhaozEngine.Render2D`:** new `Texture2D.Wrap(IGpuTexture, width, height, ownsHandle = true)`. Wraps an
  engine GPU texture (e.g. another module's render target) as a `SpriteBatch`-drawable `Texture2D`. With
  `ownsHandle: false` the wrapper does not dispose the underlying texture, so a reused offscreen target can hand
  back a stable, non-owning `Texture2D` each frame. `Render3D` now references `Render2D` (already present in every
  `Game3D` consumer, which is a superset of `Game2D`).

## 5.69.0 (custom 5.x line)

Ground-aligned, alpha-blended FILLED overlay primitive on `Scene3D` (`KhaozEngine.Render3D`): flat world-space
translucent shapes painted on a plane (range/zone/coverage/AoE highlights, board tiles), the counterpart to the
existing translucent `Debug*` line outlines. New `DebugFilledQuad(center, normal, uAxis, halfExtents, color)` plus
two ground conveniences: `DebugFilledQuad(center, halfExtents, color)` (XZ plane, normal +Y, u axis +X) and
`DebugFilledQuad(center, halfSize, color)` (square tile). `DebugFilledCircle(center, normal, radius, color,
segments=32)` fills a disc as a triangle fan. Colour is an RGBA `Vector4`; alpha is respected and blended over the
post image. The fills draw in the overlay pass (the mesh pass is opaque, so a tinted plane mesh can't blend) on a
new triangle-list renderer (depth disabled, src-alpha/one-minus-src-alpha), and are drawn UNDER the debug lines so
an outline reads crisp on top of a fill. Cleared each frame in `Begin()`; per-frame alloc-free like the line and
billboard paths. Pure geometry builders live in the new `DebugFillShapes` (headless-testable: winding, extents,
fan vertex count). Additive; no API removed.

## 5.68.0 (custom 5.x line)

Cursor orbit gesture on `IsoCameraController` (`KhaozEngine.Render3D`), input-agnostic and headless-testable like
the existing pan/zoom. `BeginOrbit(cursorPx)` / `UpdateOrbit(cursorPx)` / `EndOrbit()` (plus `IsOrbiting`) swing the
camera's `Azimuth` by horizontal drag (`OrbitYawSpeed`, rad/px) and tilt `Elevation` by vertical drag
(`OrbitPitchSpeed`, rad/px; dragging up raises elevation), clamped to `[MinElevation, MaxElevation]` (defaults
`PI/12` ~15 deg and `PI*0.49` ~88 deg). The clamp keeps the camera strictly above the ground plane (never flat or
under the board) and strictly below the vertical (so `CreateLookAt` can't degenerate against the up vector). Orbit
leaves `Target` fixed, so the camera swings around the board centre with no re-pin. The game wires the gesture to
whichever button it likes.

## 5.67.0 (custom 5.x line)

Generic 2D grid line-of-sight / segment-raycast helper in `KhaozEngine.Collision` (shipped in the
`KhaozEngine.Foundation` umbrella). Subsumes the bespoke grid LOS test games were writing by hand (Hardpoint's
tower line-of-fire) and improves on it: an exact Amanatides&Woo grid traversal (4-connected supercover) instead
of fixed-step sampling, so a thin diagonal wall is never stepped over.

- **`KhaozEngine.Collision`:** new static `GridRay`. `IsClear(from, to, cellSize, blocks)` returns true when the
  segment crosses no cell for which the caller's `blocks(x, y)` predicate is true; the two endpoint cells (the
  cells containing `from`/`to`) are excluded by default so a shooter/target standing in a wall cell does not block
  its own line, with an opt-in `includeEndpointCells` to test them too. `Trace(from, to, cellSize, visit)`
  enumerates every touched cell in order (endpoints included; return false from `visit` to stop early). Fully
  decoupled from game types, deterministic, allocation-free on the hot path (the only delegate is the caller's
  predicate). Cell mapping is `(int)MathF.Floor(world / cellSize)`, matching `SpatialHashGrid`.

## 5.66.0 (custom 5.x line)

Optional viewport-tracking internal render target for `KhaozEngine.Render3D`, to kill upscale blur on large
windows. `Scene3D` renders the 3D world into a fixed offscreen target (default 1600x900) and blit-scales it to
the swapchain. On a window larger than that target (trivial on Retina) the smooth (non-`Pixelated`) blit
UPscaled the buffer bilinearly, so everything went soft; zooming the ortho camera out made it worse. New opt-in
mode sizes the target to the actual framebuffer instead, so the final blit is 1:1 (or a downscale at the cap).
Default is unchanged (`FixedInternal`), so the retro/`Pixelated` path and existing consumers are untouched.

- **`KhaozEngine.Render3D`:** new `RenderScale` enum (`FixedInternal` | `MatchViewport`) and three
  `PixelPostProcessSettings` fields: `RenderScale` (default `FixedInternal`), `MaxRenderWidth`/`MaxRenderHeight`
  (default 3840x2160, the cap when matching). `MatchViewport` sizes the internal `RenderResources` to the
  framebuffer each frame, clamped to the cap with aspect preserved (each axis >= 1). `Scene3D.EnsureSize` resizes
  only when the clamped target actually changes (stable at the cap, no per-frame thrash); `RenderWidth`/
  `RenderHeight` are ignored in `MatchViewport`. The viewport plumbed into the render is already physical
  framebuffer pixels (`AppWindow` sets `Frame.Width/Height` from `FramebufferSize`), so Retina output is sharp.
  Pure `Scene3D.ComputeTargetSize` carries the sizing math with a headless test; a `KE_GPU_TESTS` test asserts
  the real `RenderResources` resize for both modes.

## 5.65.0 (custom 5.x line)

Frame-rate-independent client prediction render. `ClientPrediction.RenderedState` now eases the predicted
position from the previous tick to the current one across the tick duration (time-based), instead of only the
per-tick stepped position. Above the tick rate (e.g. a 144Hz window over a 60Hz sim) the predicted local entity
- and anything that follows it, like the camera and orbiting drones - was snapping each tick and juddering; it
is now smooth at any frame rate. The reconciliation render offset is unchanged (still decays over real time);
the inter-tick interpolation collapses onto the rebased state on reconcile so the visible correction is carried
only by that offset.

- **`KhaozEngine.Netcode`:** `ClientPrediction.RenderedState` interpolates `previousPredictedPosition` ->
  `predictedState.Position` by `min(1, secondsSinceLastPredict / TickSeconds)`. `Reset`/`Predict`/`Reconcile`
  maintain the interpolation endpoints; `AdvancePresentation` advances the clock (clamped to one tick, so a
  stalled tick stream holds rather than overshoots). Headless test asserts the eased, clamped path.

## 5.64.0 (custom 5.x line)

Two additive `KhaozEngine.Render2D` APIs for HUD/card UIs and CPU pixel work (the SpaceGame fan-card screen
asked for both): a batch-level model transform so a whole card tilts as one, and a CPU image decode for
opaque-pixel collision masks.

- **Batch model transform:** `SpriteBatch.Begin(...)` gains overloads taking a `Matrix4x4 transform` applied to
  every draw before projection, so a composed group (panel + icon + text) rotates / scales / translates as one.
  `DrawString` has no rotation of its own, so a model transform here is how text tilts with its card. On all
  three spaces: `Begin(Matrix4x4)` (screen), `Begin(Camera2D, Matrix4x4)` (world), and
  `Begin(IDesignViewport, Matrix4x4)` (design - the card-tilt case). A `SetScissor` during a design-space
  transformed pass is mapped through the viewport but NOT the transform (the GPU scissor is axis-aligned in
  framebuffer space), so clip a rotated card by its un-rotated design bounds. A headless test pins the
  model-before-projection compose order; a GPU-gated test draws a translated rect and asserts the pass moved.
- **CPU image decode:** new `ImageRgba` struct - tightly-packed RGBA8 pixels + width/height, no GPU resource -
  with `AlphaAt` / `IsOpaqueAt(threshold)` for building opaque-pixel collision masks. Decode with
  `ImageRgba.Load(path)` / `ImageRgba.Decode(bytes)` or `Render2DSurface.LoadImageRgba(path)`; no GPU device and
  no GPU round-trip. Hand `img.Pixels` to `Render2DSurface.CreateTexture` to also draw it without re-decoding.

## 5.63.1 (custom 5.x line)

Internal cleanup, no public API change.

- **`KhaozEngine.Audio`:** the float-to-16-bit-PCM helper `ToShort` was duplicated in `Decoding.cs`
  (`PcmDecoders.ToShort`) and `WavSynth.cs`. Consolidated into a single internal `AudioConvert.ToShort`;
  both call sites now use it. Behaviour is identical (same clamp + round).

## 5.63.0 (custom 5.x line)

Three additions consumers asked for after the SpaceGame port: point sampling, CPU-side capture, and keyboard
slider control.

- **`KhaozEngine.Render2D` - point sampling:** `SpriteBatch.Begin(...)` now takes an optional
  `SamplerMode` (`Linear` default, `Point` for crisp pixel art - the 4.x `SamplerState.PointClamp` equivalent).
  Per-pass: pass it to the `Begin(Camera2D)` / `Begin(IDesignViewport)` / `Begin()` overload. Resource sets are
  keyed by (texture, sampler) so a texture drawn under both filters in one frame gets a set each. GPU-gated test
  upscales a checker and asserts the Point pass keeps hard edges where Linear blends.
- **`KhaozEngine.Render2D` - CPU capture:** `Render2DSurface.CaptureToRgba(w, h, clear, draw)` renders an
  offscreen pass on the live device (reusing its textures/fonts) and returns a tightly-packed RGBA8 byte buffer -
  the on-device equivalent of `Render2DSnapshot.Capture`, for pixels a game needs on the CPU (e.g. a clipboard
  image copy). Shared mechanism in `Render2DCore.RenderToRgba`; GPU-gated test.
- **`KhaozEngine.Gui` - slider keyboard control:** `Slider.Nudge(delta)` adjusts the value (clamped 0..1) for
  keyboard / gamepad, independent of pointer drag; returns whether it changed. Unit-tested.

## 5.62.0 (custom 5.x line)

Scaled text: `SpriteBatch.DrawString(font, text, position, color, scale)` (Color + Vector4 overloads) draws the
glyph run uniformly scaled by `scale` about its top-left. The whole layout - glyph size, offsets, advances and
the ascent baseline - scales together, so it matches a layout measured with `font.Measure(text) * scale` (the
caller measures at `scale` to position, draws at the same `scale`). Closes the gap that forced consumers to draw
all text at the base font size after the 4.x `DrawString(..., scale, ...)` overload went away; `scale = 1`
is the existing unscaled path.

- **`KhaozEngine.Render2D`:** new `SpriteBatch.DrawString(..., float scale)` overloads. GPU-gated test renders a
  glyph at scale 1 and scale 2 and asserts the lit-pixel extent grows ~2x in width and height.

## 5.61.0 (custom 5.x line)

On-device offscreen capture for the 2D surface: `Render2DSurface.CaptureToTexture(width, height, clear,
Action<SpriteBatch> draw)` renders a 2D pass into a fresh sampleable `Texture2D` on the surface's own live
GPU device and returns it (caller-owned, dispose when done). Unlike `Render2DSnapshot.Capture`, which spins
up a throwaway headless device and reads back to the CPU, this stays on the live device, so the draw callback
can reuse textures/fonts already loaded on the surface. The one-shot freeze-frame screenshot a game needs (a
death scene captured to a texture and shown behind the game-over UI) without rebuilding assets on a second
device.

- **`KhaozEngine.Render2D`:** new `Render2DSurface.CaptureToTexture`. Runs synchronously (submits + waits on
  the GPU) into an offscreen `RenderTarget | Sampled` target sized to `width`/`height` (clamped to >= 1). The
  callback gets its own batch and does the usual `Begin(camera)`/`Begin(viewport)` + `End()` passes inside it.
- Shared mechanism lives in `Render2DCore.RenderToTexture(IGpuDevice, ...)` so it is GPU-testable headlessly
  (gated `[GpuFact]`: renders a known quad, reads it back, asserts the captured pixels).

## 5.60.0 (custom 5.x line)

Menu-navigation input layer: a MonoGame-free `InputManager` in `KhaozEngine.Windowing`, the 5.x rebuild
of the legacy 4.x `KhaozEngine.Input.InputManager`. Closes the gap where the 5.x stack had raw input
primitives but no high-level menu/action layer, and `Gui` widgets were pointer-only.

- **`KhaozEngine.Windowing`:** new `InputManager` (poll once per frame via
  `Update(InputState, IDesignViewport?)`). Composes a `Pointer` for the press-origin click-through
  invariant and adds keyboard/gamepad menu navigation: `IsMenuUp`/`IsMenuDown` (arrow / D-pad /
  edge-detected left-stick / scroll-wheel), `IsMenuSelect` (Enter/Space/A/Start), `IsMenuCancel`
  (Escape/B/Back), `IsSelectNext`/`IsSelectPrevious` (Right/Left + D-pad), `IsPauseGame`
  (Escape/Back/Start or a tap in bounds). Plus `IsKeyDown`/`IsKeyJustPressed`,
  `IsNewKeyPress`/`IsNewButtonPress` (per-player with an `out PlayerIndex`, or any-player scan),
  `IsMouseWheelScrolledUp`/`Down`, and the full set of `Pointer` hit-test helpers delegated through.
- **`KhaozEngine.Windowing`:** new `PlayerIndex` enum (`One`..`Four`, 0-based), the MonoGame-free
  replacement for XNA `PlayerIndex`. Maps XNA `Keys`/`Buttons`/`PlayerIndex` to `Key`/`GamepadButton`/
  `PlayerIndex`, and `Rectangle` to `Rect`, so a 4.x consumer ports with a `using` swap.
- **`KhaozEngine.Windowing`:** `Pointer` gains `IsMiddleJustPressed` and `IsRightJustPressed` (symmetric
  with the existing `*JustReleased` edges), backing the `InputManager` middle-button surface.
- **`KhaozEngine.Gui`:** new `FocusNavigator` - keyboard/gamepad focus cursor over a list of N widgets
  (wrap/clamp index math; `Update(InputManager)` drives focus from vertical menu nav). The natural
  companion to the otherwise pointer-only Gui widgets.
- Left-stick "up" convention: `+Y` = stick pushed up (matching the engine's existing 4.x cursor
  convention), edge-detected past `InputManager.StickThreshold` (0.5) against the previous frame.
- Additive, no breaking changes. Reachable from the `KhaozEngine.Game2D` umbrella (pulls `Windowing`
  and `Gui`). 35 new headless tests. First consumer: SpaceGame's 5.x screen/menu port.

## 5.59.0 (custom 5.x line)

Cross-platform storage: `AppDataPaths` is now publisher-rooted and mobile-aware, plus a new
`GameStorage` facade in `KhaozEngine.Persistence`.

- **BREAKING (`KhaozEngine.App`):** `AppDataPaths` now takes `(string publisher, string appName)` and
  resolves `<os-base>/<publisher>/<appName>/`. The old single-arg `AppDataPaths(appFolderName)` is
  removed. Migrate call sites: `new AppDataPaths("MyGame")` becomes `new AppDataPaths("APKiwi", "MyGame")`
  (or switch to `GameStorage`). No on-disk migration is performed; data under the old single-folder
  layout is orphaned.
- New Android/iOS branches resolve the app sandbox (`SpecialFolder.LocalApplicationData`) and are
  checked before the desktop branches. `IAppDataEnvironment` gains `IsAndroid`/`IsIOS`. BCL-only.
- New `GameStorage` / `GameStorageOptions` (`KhaozEngine.Persistence`): one object assembling the
  publisher-rooted `AppDataPaths`, a shared `PersistenceQueue`, a `FileSettingsStorage`, and an optional
  `SaveEncoder`. Generic typed `Save<T>`/`Load<T>` (plaintext or transparently encoded), `Exists`/`Delete`,
  `CreateSettingsManager<T>`, and `Flush`/`Dispose` (flushes the queue). `Load<T>` returns a new instance
  for an absent file, tolerates comments/trailing commas, and auto-decodes encoded saves.

## 5.58.0 (custom 5.x line)

`GuiSurface.Slider` - an immediate-mode horizontal drag slider, the one widget the immediate surface lacked
for a settings screen. Same idiom as `Button`/`Panel`/`Swatch`: one call per frame, styled by `GuiStyle`,
headless-testable via `Begin(null, pointer)`.

- `float Slider(Rect rect, float value, GuiStyle style, bool enabled = true)` and the default-style overload
  `float Slider(Rect rect, float value)`. Value domain is normalized `[0,1]`; the caller maps to its own range
  (volumes are already 0..1) and owns persistence.
- Drag uses the press-origin invariant (via `Pointer.IsDraggingIn`): the value only tracks the pointer when the
  press began inside the track, and keeps tracking even if the cursor strays off the track while held. The
  handle half-width is inset so the ends reach exactly 0 and 1.
- An enabled slider reserves its rect for the `PointerCaptured` click-through gate (so the board/camera behind
  a settings panel does not also react); a disabled slider returns its value unchanged, reserves nothing, and
  draws muted.
- Visuals (`GuiDraw.DrawSlider`, reusing `GuiStyle`): thin track bar, accent fill left of the handle, and a knob
  that lights `Press` while dragging / `Hover` while hovered. The value label is the caller's job.

## 5.57.0 (custom 5.x line)

Parallax background layers - the final camera feel-layer slice. With this, the roadmap camera backlog
(follow, look-ahead, pixel snap, multi-target framing, eased blends, room cameras, screen shake, parallax)
is complete.

- **`ParallaxLayer`** (`KhaozEngine.Render2D`) - a per-axis scroll `Factor` (0 = static backdrop, 1 = locked
  to the world, 0.5 = half speed / farther) with `ViewPosition(cameraPosition) = cameraPosition * Factor`.
  The game derives a layer `Camera2D` from it and draws the layer's sprites; parallax is translation-only
  (zoom/rotation are shared with the main camera).
- **`Parallax.Wrap(value, size)`** (`KhaozEngine.Render2D`) - a positive modulo (`[0, size)`) for seamlessly
  tiling a repeating background; the game draws copies starting at `-Wrap(layerViewX, tileWidth)`. Returns 0
  for non-positive size.

## 5.56.0 (custom 5.x line)

Screen shake on the 5.x engine, and `KhaozEngine.Effects` graduates off MonoGame onto the 5.x line - the
last camera feel-layer slice (parallax aside).

- **`KhaozEngine.Effects` graduated to the 5.x line.** The old 4.x rect-particle system (MonoGame + Graphics)
  was retired - it was superseded by the MonoGame-free `KhaozEngine.Particles` and had no game consumer. The
  package now targets System.Numerics + BCL only and versions with `<KhaozEngine5xVersion>`. The 4.x line is
  down to `Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`.
- **`ScreenShake`** (`KhaozEngine.Effects`) - a trauma-based, deterministic shake offset generator. `Add(amount)`
  bumps trauma on impacts; the magnitude falls off as `trauma^2` and `Update(dt)` drains it. It exposes a
  positional `Offset` and rotational `Angle` (seeded smooth noise, no `System.Random`/wall-clock) that the
  game composes onto its render camera - it never mutates a camera itself. `MaxOffset`/`MaxAngle`/
  `DecayPerSecond`/`Frequency` tune the feel; `MaxAngle = 0` gives positional-only shake.

## 5.55.0 (custom 5.x line)

Room / region cameras on the 5.x engine - Metroidvania-style per-area cameras, the next slice of the camera
feel layer (composing the follow + blend pieces already shipped).

- **`RoomCamera`** (`KhaozEngine.Render2D`) - a turnkey controller: `Update(target, velocity, dt, vw, vh)`
  follows the target confined to the region it is in (an internal `CameraFollow`, exposed via `Follow` for
  feel tuning) and eases (an internal `CameraBlend`) to reframe when the target crosses into a new region,
  then resumes following. `BlendDuration` / `BlendEasing` shape the hand-off; `ActiveRoomIndex` /
  `IsTransitioning` expose state; `Warp(target, vw, vh)` snaps to the target's room instantly.
- **`CameraRoom`** (`KhaozEngine.Render2D`) - a region: a world rect (both the trigger area and the camera
  confinement) plus an optional per-room zoom override (`null` keeps the current zoom). Overlaps resolve by
  list order; a target in no room holds the current room.
- **`Camera2D.ClampPosition`** gains an overload taking an explicit zoom, so a hand-off can clamp the framing
  at the next room's zoom before the camera has eased there. The existing overload delegates with `Zoom`
  (behaviour unchanged).

## 5.54.0 (custom 5.x line)

Eased camera blends on the 5.x engine - a reusable one-shot camera transition primitive, the next slice of
the camera feel layer (and the building block the room/region camera slice will consume).

- **`CameraBlend`** (`KhaozEngine.Render2D`) - transitions a `Camera2D` from its current framing to a target
  over a duration: `To(target, duration, easing)` captures the start, `Update(dt)` advances it, and the
  camera lands exactly on the target at the end. `duration <= 0` snaps instantly; `IsBlending` / `Progress`
  expose state; `Stop()` cancels in place; calling `To` mid-blend cleanly re-targets from the current frame.
- **`CameraState`** (`KhaozEngine.Render2D`) - an immutable framing snapshot (position + zoom + rotation) with
  `From(camera)` / `ApplyTo(camera)` / `Lerp(a, b, t)`. The blend endpoint type, and a reusable camera "setup"
  value.
- **`Easing`** (`KhaozEngine.Render2D`) - pure preset curves (`Linear`, `SmoothStep`, `EaseIn`, `EaseOut`,
  `EaseInOut`), each clamping `t` to `[0,1]`. `CameraBlend` defaults to `SmoothStep`; callers can pass any
  `Func<float,float>`.

## 5.53.0 (custom 5.x line)

Multi-target (co-op / shared-screen) camera framing on the 5.x engine, the next slice of the camera feel
layer.

- **`GroupCamera`** (`KhaozEngine.Render2D`) - drives a `Camera2D` to keep N targets framed: each frame it
  takes the targets' padded bounding box and eases position and zoom toward the contain-fit framing
  (frame-rate-independent, separate `Stiffness` / `ZoomStiffness`), then clamps to world bounds. `PaddingFraction`
  sets the margin; `MinViewSize` floors the framed extent so a clustered or single target does not zoom to the
  max. `Warp(targets, ...)` snaps instantly; an empty target list holds the view.
- **`CameraFraming`** (`KhaozEngine.Render2D`) - the pure framing math underneath: `Bounds(targets,
  paddingFraction, minViewSize)` for the padded AABB and `Solve(bounds, vw, vh, minZoom, maxZoom)` for the
  position + contain-fit zoom. Headless, no easing - usable standalone.

## 5.52.0 (custom 5.x line)

Camera feel layer for 2D / platformer games arrives on the 5.x engine: `CameraFollow` (previously 4.x-only,
MonoGame-bound) is ported to `KhaozEngine.Render2D` (System.Numerics, headless) and enriched for side-scroller
feel.

- **`CameraFollow`** (`KhaozEngine.Render2D`) - drives a `Camera2D` to follow a target with per-axis,
  frame-rate-independent smoothing (`1 - exp(-Stiffness.axis * dt)`), an optional absolute screen-space
  `Deadzone` (`Rect?`), a world-bounds clamp, and `Warp(position)` for instant respawn / scene-load placement.
  `Stiffness` is a per-axis `Vector2` (a component `<= 0` snaps that axis); `SetStiffness(float)` sets both.
- **Look-ahead** - `CameraFollow.LookAhead` (`LookAheadSettings`) leads the camera ahead of the target along a
  caller-supplied velocity: `clamp(velocity * LeadTime, +/-MaxDistance)` per axis, eased by its own
  `Stiffness` so a direction reversal does not snap. Per-axis `LeadTime` allows horizontal-only lead.
- **Pixel snap** - `CameraFollow.Snap` (`PixelSnap`, also usable standalone) snaps the rendered
  `Camera.Position` to an art-pixel grid (`WorldUnitsPerPixel`) while smoothing keeps the sub-pixel truth, so
  there is no drift. Snaps camera translation only; integer zoom + a fixed-resolution render target remain the
  game's responsibility.

## 5.51.0 (custom 5.x line)

`GameApp` (the loop facade) can now host a game with a custom window or viewport, so a real game can adopt it
instead of hand-writing the `AppWindow.Run` loop.

- **`GameAppOptions.WindowFactory`** (`Func<GameAppOptions, AppWindow>?`) and **`ViewportFactory`**
  (`Func<GameAppOptions, IDesignViewport>?`) - optional builders. When null (the default) `GameApp` builds the
  plain `new AppWindow(Title, Width, Height)` + `new DesignViewport(...)` as before; set them to use e.g.
  `AppWindow.Scaled` (display-fitted) + `AdaptiveViewport` (responsive, no letterbox). `GameApp.Viewport` is now
  typed `IDesignViewport`.
- **`IDesignViewport` gains `Update(int windowWidth, int windowHeight)`** - the per-frame recompute the facade
  drives. Both `DesignViewport` and `AdaptiveViewport` already had this method; it's now on the seam. Minor
  breaking change for a custom `IDesignViewport` implementation (add the one-line `Update`).
- No behaviour change with default options.

## 5.50.0 (custom 5.x line)

Two structural follow-ups to the umbrella metapackages: the game-loop framework no longer drags in the 3D
renderer, and there's a foundation-only bundle.

- **`KhaozEngine.Game` is now Render3D-free.** Its `GameApp`/`GameScene`/`SceneManager` baked in optional 3D
  hooks, forcing a compile-time `Render3D` (and SharpGLTF) dependency on every game that used the loop facade -
  even a 2D one. The 3D integration moved to a new bridge package **`KhaozEngine.Game.Render3D`**:
  - **`GameApp3D : GameApp`** - builds the `Render3DSurface` and drives the 3D pass in `GameApp`'s new
    `OnRenderWorld(Frame)` seam (which runs before the 2D batch).
  - **`IGameScene3D`** - a `GameScene` implements this to submit a 3D world pass.
  - **`SceneManager.Draw3D(scene)`** extension - draws the visible `IGameScene3D` scenes (same visible set as
    `Draw2D`).
  - **Breaking** (shipped as a 5.x minor; the only consumer, Hardpoint, is migrated alongside): removed
    `GameApp.OnDraw3D`/`Scene`/`Surface3D`, `GameScene.OnDraw3D`, `SceneManager.Draw3D`, and
    `GameAppOptions.Enable3D` from `KhaozEngine.Game`. A 3D game now derives `GameApp3D` (or implements
    `IGameScene3D` on its scene + calls the `Draw3D` extension) from `KhaozEngine.Game.Render3D`. `SceneManager.
    FirstVisibleIndex` is now public. **`KhaozEngine.Game2D` now includes `KhaozEngine.Game`** (it pulls no 3D),
    so a 2D game gets the loop facade with no 3D renderer.
- **New `KhaozEngine.Foundation` metapackage** - the MonoGame-free, GPU-free foundation (App/Content/Diagnostics/
  Ecs/Localization/Persistence/Serialization/Pooling/Collision/Platform/Updates) in one reference, for a
  gameplay-logic library or any non-rendering project. `Game2D` and `Server` now compose it instead of listing
  the foundation packages individually (same closure, deduplicated).
- No runtime/render change: the GPU goldens are pixel-identical and the 3D sample runs on `GameApp3D`.

## 5.49.0 (custom 5.x line)

**Umbrella metapackages so a game references the engine in one line instead of a dozen.** Three new code-free
packages, each a curated dependency group over the existing granular packages (which stay, unchanged - the
split still serves servers, tools, and trimmed builds):

- **`KhaozEngine.Game2D`** - desktop 2D game: `Windowing`, `Render2D`, `Gui`, `Audio`, `Particles` + the
  MonoGame-free foundation (`App`/`Content`/`Diagnostics`/`Ecs`/`Localization`/`Persistence`/`Serialization`/
  `Pooling`/`Collision`/`Platform`/`Updates`). No 3D, no netcode.
- **`KhaozEngine.Game3D`** - desktop 3D game: a strict superset of `Game2D` plus `Render3D` and `Game` (the
  `GameApp`/`SceneManager` loop facade).
- **`KhaozEngine.Server`** - headless / server: the GPU-free foundation plus the networking layer
  (`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`). No graphics, windowing, audio, or GPU.

Each metapackage ships no assembly (`IncludeBuildOutput=false`), just a NuGet dependency group at the shared
5.x version. You can still reference granular packages directly, and mix a bundle with extras (e.g.
`Game2D` + `Netcode.LiteNetLib` for a 2D multiplayer game). No code or behaviour change to any existing package.

> Note: the `GameApp` facade (`KhaozEngine.Game`) depends on `Render3D`, so it lives in `Game3D` only; a 2D game
> on `Game2D` drives the `AppWindow.Run` loop directly. Decoupling `GameApp` from `Render3D` (so it could join
> `Game2D`) is a possible follow-up.

## 5.48.0 (custom 5.x line)

Engine-audit cleanups: a real mesh-loader bug fix plus three quality
items. No behavioural change to existing scenes - the GPU goldens are pixel-identical.

- **`Render3D.GltfLoader` no longer silently corrupts meshes (5.x engine audit).** It welded vertices by *position
  only* (merging away hard edges and UV seams) and cast indices to `ushort` with no bound check (silently
  truncating any mesh past 65535 vertices). It now honours the glTF `NORMAL` attribute, welds on
  (position, normal, uv) so hard edges and UV seams survive, and **throws** past the ushort index ceiling
  instead of truncating (matching `MeshBuilder`). The welding/normal/overflow logic moved to a new internal
  `MeshAssembler` that's unit-tested without needing a glTF file on disk. (No golden uses `GltfLoader`, so
  procedural-mesh output is unchanged.)
- **Typed `Color` and `Rect` overloads on `SpriteBatch.Draw`/`DrawString` (5.x engine audit).** Destination rect and
  color were both a bare `Vector4` and could be swapped at a call site. New `Render2D.Color` struct (RGBA float,
  implicit to `Vector4`, `FromBytes`/`WithAlpha`/`White`/...) and typed overloads; the rect overloads reuse the
  existing `Windowing.Rect`. The untyped `Vector4` overloads stay, so nothing breaks.
- **Retained widgets reserve their rect, like `Button` (5.x engine audit, click-through).** `Toggle`, `Slider`,
  `Dropdown`, and `TextInput` now call `Pointer.BlockRegion` during `Update` (the open `Dropdown` reserves its
  whole expanded list), so a layer beneath can't be clicked through them. `GuiSurface`'s docs now spell out when
  to use the immediate vs retained paradigm. (`Button` already did this; the audit's specific Button complaint
  was resolved earlier.)
- **Shared `Gpu.GpuReadback.ToRgba` (5.x engine audit).** The headless texture-readback (staging blit + map +
  row-pitch de-stride) was duplicated verbatim in the Render2D and Render3D snapshot helpers; both now call the
  one helper in `KhaozEngine.Gpu`. Pure refactor - goldens verified pixel-identical on Metal.

## 5.47.0 (custom 5.x line)

`Windowing.AdaptiveViewport` - a responsive `IDesignViewport` that fills a resizable window at any aspect (promoted
from Nullwake, where it replaced the pillarboxed fixed-aspect viewport on desktop).

- The design **height** is fixed (vertical layout/anchors stay constant) while the design **width** tracks the
  window's aspect ratio, with a uniform height-fit scale and **no letterbox**. Layout expressed relative to
  `Width`/`Height` (full-width bars, Width-relative grids, centered content) fills the window edge-to-edge instead of
  being pillarboxed. `Width` is floored at the reference width so a narrower-than-design window keeps the design's
  minimum rather than squishing. Contrast `DesignViewport`, which preserves a fixed reference size by letterboxing.
- Drop-in for the existing seam: drive with `Update(windowW, windowH)`, pass to `SpriteBatch.Begin(IDesignViewport)` /
  `Pointer.Update(InputState, IDesignViewport)`. Pure math, no window/GPU dependency. 5 headless tests.

## 5.46.0 (custom 5.x line)

**The MonoGame-free foundation packages graduated from the 4.x line onto the 5.x line, so a 5.x game pins only
5.x packages** (5.x engine audit). 14 packages move from `<Version>` (`4.12.0`) to `<KhaozEngine5xVersion>` (`5.46.0`):
`Ecs`, `Serialization`, `Content`, `Diagnostics`, `App`, `Localization`, `Persistence`, `Pooling`, `Platform`,
`Updates`, `Collision`, `Netcode`, `Netcode.Abstractions`, `Netcode.LiteNetLib`. The 5.x line is now the 8
custom-stack packages + these 14 = 22 packages, all at `5.46.0`.

- **Non-breaking re-version.** Same assemblies, namespaces, and public API — only the package version changes. A
  consumer adopts by swapping `Version="4.12.0"` to `Version="5.46.0"` on those `<PackageReference>`s; no code
  change. The old `4.12.0` foundation nupkgs remain in the feed (cumulative pack), so a consumer that hasn't
  bumped (Hardpoint, SpaceGame) keeps resolving its pin unchanged.
- **The 4.x line is now legacy-only**, carrying just the genuinely-MonoGame packages
  (`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`), consumed by the still-4.x SpaceGame until it
  migrates. `<Version>` stays `4.12.0` (frozen-ish); these packages and the 4.x line are deleted with MonoGame
  once SpaceGame is off them.
- **`scripts/check-doc-versions.sh` now checks the 5.x line** (`<KhaozEngine5xVersion>`). The 4.x `<Version>` is
  no longer the "current engine version" and is exempt like a consumer pin.
- No functional/runtime change; existing tests unaffected.

## 4.12.0 (4.x line)

`KhaozEngine.Collision` and `KhaozEngine.Netcode` are now **MonoGame-free** - they drop the
`MonoGame.Framework.DesktopGL` reference and their public `Vector2` moves from XNA (`Microsoft.Xna.Framework`) to
`System.Numerics`. This makes them foundation-grade (a 5.x MonoGame-free game can consume them) and shrinks the
remaining legacy-MonoGame surface.

- **Breaking** (shipped as a 4.x minor; the version-number jump to 5.x is reserved for the custom stack): the
  `Vector2` in `CircleCollision`, `SpatialHashGrid`, `ICircleCollider`, `IPreciseCircleCollisionTarget`,
  `UnitAxisQuantizer`(none), `ClientPrediction`, and `IPredictedState` is now `System.Numerics.Vector2`. A
  consumer swaps `using Microsoft.Xna.Framework;` to `using System.Numerics;` at those call sites; the
  field-compatible struct means no other change.
- **Determinism preserved** (these are lockstep-hash-gated for SpaceGame): `CircleCollision.Intersects` now uses
  explicit `dx*dx + dy*dy` rather than a `Vector2` library helper, so the result is bit-stable regardless of the
  vector library; `UnitAxisQuantizer` uses `System.Math.Clamp` (a comparison clamp, bit-identical to the old
  `MathHelper.Clamp`). `ClientPrediction`'s `Length`/`Lerp` are client render-smoothing only (not in the sim
  hash). The byte-identical sim math keeps SpaceGame's `17709480852979803671` gate stable when it adopts.
- All other 4.x packages are unchanged; they share this version bump as usual.

## 5.45.1 (custom 5.x line)

Fix: `Render2D.SpriteBatch` corrupted any frame that flushed more than once. The batch reused a single persistent
vertex buffer starting at byte 0 on every flush, but a frame flushes repeatedly whenever `SetScissor`/`ClearScissor`
is used (e.g. scrollable panels): a later flush overwrote the vertices an earlier, already-recorded `Draw` still
referenced, so the GPU read the wrong geometry. Symptoms were garbled/misplaced/clipped content in scissor-clipped
UI (the single-flush main scene was unaffected, which is why goldens missed it).

- Each flush within a frame now uses its own vertex buffer from a small pool (`_flushIndex` resets per `NewFrame`);
  buffers persist across frames and only grow, so no extra per-frame allocation. No public API change.
- New GPU regression test: a draw before `SetScissor` must not corrupt, and the scissor must clip the asked band.

## 5.45.0 (custom 5.x line)

Sixth engine-first gap-fill for porting 2D games (Nullwake) to the 5.x stack: open a fixed-design window large
enough to read on a desktop monitor. A small-tall (mobile-portrait) design opened at its design size renders the
whole UI at life-size, so its intentionally-small text is tiny on a desktop display.

- **`AppWindow.Scaled(title, designWidth, designHeight, screenFraction = 0.9f, maxScale = 2f)`** - a factory that
  opens the window sized up to fill the display: the largest multiple of the design that preserves its aspect and
  fits within `screenFraction` of the primary monitor's work area, clamped to `[1, maxScale]`. Pair with a
  `DesignViewport` (Fit) so the whole UI (and text) scales uniformly; with `Render2D` 5.44.0 font oversampling the
  upscaled text stays crisp. Never opens smaller than the design size.
- **`AppWindow.FitToScreen(designW, designH, screenW, screenH, screenFraction, maxScale)`** - the pure sizing policy
  (no monitor/GPU access), returned as a `(Width, Height)`. Falls back to the design size when the screen size is
  unknown (`<= 0`). 5 headless tests (grow-to-fill + aspect preserved, never-shrink on a height-constrained screen,
  maxScale cap, unknown-screen fallback, landscape width-bound).
- **`AppWindow.PrimaryScreenSize()`** - the primary monitor size in window coordinates (or `(0, 0)` headless), via
  the Silk monitor API.

## 5.44.0 (custom 5.x line)

Fifth engine-first gap-fill for porting 2D games (Nullwake) to the 5.x stack: crisp supersampled text. Small UI
fonts baked at their design pixel size go soft when a `DesignViewport` upscales them to a higher-resolution
framebuffer; `SpriteFont` can now rasterize the atlas at a higher texel density while keeping the on-screen layout
identical.

- **`Render2DSurface.LoadFont(ttfPath, pixelHeight, oversample = 1)`** (and the matching `Render2DContext.LoadFont`
  used by snapshots) gains an `oversample` factor. The glyph atlas is baked at `pixelHeight * oversample`, but
  every layout metric (`Measure`, `LineHeight`, glyph advances) is reported at the logical `pixelHeight`, so text
  occupies the exact same space at any oversample - only the texel density changes. Pass 2-3 for HiDPI / upscaled
  design viewports. With linear sampling the denser atlas stays sharp through the upscale.
- **Non-breaking / pixel-identical default**: `oversample == 1` is the original bake. The atlas width stays 512 and
  its height only grows past the 256 floor when a larger raster needs the room, so the default produces the exact
  same atlas (the `scene2d` GPU text golden is unchanged on metal). The fixed-256 atlas also gained an adaptive
  height, removing a latent overflow for large fonts/oversamples.
- Internally `SpriteFont.Build` now splits into a device-free `BakeCpu` (rasterization + packing + metrics) plus a
  thin GPU upload; `SpriteBatch.DrawString` scales each glyph quad by the new `SpriteFont.RenderScale` (1/oversample).
- 5 headless tests (default 512x256 lock, atlas grows with oversample, logical metrics/advances invariant across
  oversample, glyph quad logical size, non-empty coverage); verified visually that oversample 3 stays crisp under a
  3x upscale where oversample 1 blurs.

## 5.43.0 (custom 5.x line)

Tooling: a dependency-free PNG encoder so game snapshot tools stop re-implementing the image write.

- **`Render2D.Png`** - a minimal BCL-only PNG encoder for 8-bit RGBA buffers (`Encode(rgba, w, h) -> byte[]`,
  `Write(path, rgba, w, h)`). Uses `System.IO.Compression.ZLibStream` for the IDAT stream + a CRC-32 table, so
  Render2D gains NO image-library dependency. Tooling/test helper (no palette/interlace).
- **`Render2DSnapshot.CaptureToPng(path, w, h, clear, draw)`** - one-call headless render -> PNG (over the
  existing `Capture` + `Png`), the path a game's offscreen screen-capture tool needs. Returns the raw RGBA too.
- 3 headless tests (PNG signature, byte-identical round-trip through the StbImageSharp decoder, length guard).

## 5.42.0 (custom 5.x line)

Fourth engine-first gap-fill for porting 2D games (Nullwake) to the 5.x stack: `Windowing.TimeSkip`, the
offline / fast-forward catch-up primitive that sits next to `GameClock`.

- **`Windowing.TimeSkip`** (+ `TimeSkipResult`) - advances a simulation by a span of sim-time in one shot with
  optional cap / multiplier / min-threshold policy, then invokes the consumer's analytical catch-up callback
  (O(events), not O(ticks)). Drives on-demand fast-forward ("skip +2h") and offline catch-up ("away 3h"); a
  `Completed` event fires on every `Advance` (including no-ops), and `ElapsedSimSeconds(lastSave, now, timeScale)`
  is the pure wall-time helper (clamped >= 0). The MonoGame-free 5.x port of the 4.x `Time.TimeSkip` (pure BCL,
  headless-tested) - it could not be referenced from a 5.x game because the 4.x `KhaozEngine.Time` package still
  drags in MonoGame via its `GameClock`.
- 7 new headless tests (cap/multiplier/min/no-op/Completed/ElapsedSimSeconds).

## 5.41.0 (custom 5.x line)

Third engine-first gap-fill for porting 2D games (Nullwake) to the 5.x stack: `Render2D.TextHelper`, the
point-anchored text-drawing convenience the screens lean on.

- **`Render2D.TextHelper`** - point-anchored text over a `SpriteFont`: `Draw` (top-left at a point), `DrawCentered`
  (centered on a point), `DrawRight` (right edge on a point), `DrawCenteredInRect`, and `DrawWrappedCentered`, each
  with an optional `alpha` fade overload. Positions are pixel-snapped (floored) to avoid sub-pixel blur; colors are
  RGBA `Vector4`. Complements `TextLayout` (which aligns/wraps within a width-region) with the screen-author point
  API; the positioning math (`CenteredX`/`RightX`/`CenteredInRect`/`MeasureWrappedHeight`) is pure + headless-tested
  over `ITextMeasurer`. The MonoGame-free 5.x port of the 4.x `UI.TextHelper`.
- 5 new headless tests (centered/right anchoring, centered-in-rect, wrapped-height, empty-string).

## 5.40.0 (custom 5.x line)

Second engine-first gap-fill for porting 2D games (Nullwake) to the 5.x stack: a `Gui.PannableCanvas`, the
`Render2D.Camera2D` math it needs, and `Gui.ScreenStack.Services`.

- **`Render2D.Camera2D`** gains `CenterOn`, `PanByScreenDelta`, `Focus(Rect, vw, vh, padding, min, max)` (contain-
  fit + centre), and `ClampPosition(desired, worldBounds, vw, vh)` (keep the view inside bounds; centre when the
  bounds are smaller than the viewport). Pure math, headless-tested.
- **`Gui.PannableCanvas`** - a generic pannable viewport over world-space content larger than a viewport: drag +
  wheel pan over a `Windowing.Pointer` (+ `InputState.ScrollDelta`), clamps to caller content bounds + padding,
  `WorldToScreen`/`ScreenToWorld` (viewport-offset aware), `CenterOn`/`Focus`/`CenterContent`, a click-through-safe
  `TryGetTap`, and a scissor-clipped `Draw(batch, drawWorld)` via `Begin(camera)`. Pan-only this release (pinch
  zoom is a follow-up - the 5.x Pointer exposes no pinch yet). Ported from the 4.x MonoGame `UI.PannableCanvas`.
- **`Gui.ScreenStack.Services`** (`IServiceProvider?`) + `Screen.Services` (reads via `Manager`) - so a screen can
  resolve services through its manager (matches the 4.x ScreenManager).
- 21 new headless tests (Camera2D math, PannableCanvas transforms/clamp/tap/pan, ScreenStack.Services).

## 5.39.0 (custom 5.x line)

`KhaozEngine.Render2D` gains a 2D `PrimitiveRenderer` and SpriteBatch rotation - the first engine-first gap-fill
for porting 2D games (Nullwake) off the legacy MonoGame `Graphics.PrimitiveRenderer`.

- **SpriteBatch rotation:** new `Draw(tex, position, size, originNormalized, rotation, srcUV, color)` overload
  emits a rotated quad through the same clip + winding path as the axis-aligned draws (z-order + scissor
  preserved). At rotation 0 / origin (0,0) it is identical to the axis-aligned `Draw`.
- **`PrimitiveRenderer`** (over a 1x1 white pixel + the SpriteBatch): `DrawFilledRect`, `DrawRect` (outline),
  `DrawLine` (sub-pixel, centered on its thickness), `DrawCircle`, `DrawRing` (+ adaptive `RingSegments`),
  `DrawFilledCircle`, `DrawVerticalGradient`, `DrawProgressBar` (+ a pure `ComputeProgressBarLayout`). Uses 5.x
  types (`Rect`, `Vector4` colours, `System.Numerics.Vector2`).
- Headless tests (progress-bar layout, ring segments, rotated-corner geometry) + a new `scene2d_primitives`
  golden. Existing goldens unchanged.

## 5.38.0 (custom 5.x line)

Game scene/state stack in `KhaozEngine.Game`, so games stop hand-rolling an `AppState` enum + giant `switch`.
A `GameScene` is a full game state - it owns its own update + 3D submission + 2D HUD draw + lifecycle - and a
`SceneManager` runs a stack of them.

- `GameScene` (abstract): `OnEnter`/`OnExit`/`OnUpdate(dt)`/`OnDraw3D(Scene3D)`/`OnDraw2D(SpriteBatch)`/`OnResize`,
  plus `DrawBelow` (this scene is a transparent overlay - draw the one below too) and `UpdateBelow` (let the one
  below keep updating; default false, so an overlay freezes what it covers). Reads shared per-frame context
  (Input/Pointer/Viewport/FrameWidth/FrameHeight) via its `Manager`.
- `SceneManager`: `Push`/`Pop`/`Replace` (swap top)/`SwitchTo` (clear + push)/`Clear`, and `Update`/`Draw3D`/
  `Draw2D`/`Resize`. Overlay-aware: updates run top-down and stop at the first scene that doesn't pass them
  through; draws run from the lowest visible scene up. Transitions requested from inside `Update`/`OnUpdate` are
  deferred and applied at the end of the pass, so the stack is never mutated mid-iteration. `OnEnter`/`OnExit`
  fire on add/remove.
- Distinct from and composable with the Gui `ScreenStack` (a 2D-UI-only screen stack): a scene may use a
  `GuiSurface`/`ScreenStack` internally for its menus. Drives cleanly from a `GameApp` subclass (forward
  `OnUpdate`/`OnDraw3D`/`OnDraw2D`/`OnResize` to the manager) or a raw `AppWindow` loop.
- New `SceneSample` (Menu -> SwitchTo Play -> Push Pause overlay -> Pop). Headless-tested (lifecycle ordering,
  both gating modes, deferred transitions, edge cases).

## 5.37.0 (custom 5.x line)

New `IsoCameraController` in `KhaozEngine.Render3D`: cursor-anchored zoom + pan for an `IsoCamera3D`. It is
input-agnostic (pure System.Numerics, no GPU, no input types) so a game wires its own input policy to it and the
math stays headless-testable.

- `Zoom(wheelDelta, cursorPx, vw, vh)` scales `Camera.Zoom` by `ZoomStep^wheelDelta` (clamped to
  `[MinZoom, MaxZoom]`) and shifts `Target` so the ground point under the cursor stays fixed (zoom-to-cursor).
- `BeginPan` / `UpdatePan` / `EndPan` are a grab-pan: the world point grabbed at the start of the drag stays
  under the cursor, so the ground follows the hand. `IsPanning` reports state.
- Optional `PanMin` / `PanMax` clamp `Target` X/Z; `GroundY` sets the pick plane.

Headless-tested (anchor preservation for zoom + pan, clamps, no-op guards). Adopt by feeding scroll delta to
`Zoom` and a drag to `BeginPan`/`UpdatePan`.

## 5.36.0 (custom 5.x line)

`GuiSurface` (immediate-mode UI) now exposes hover state, so a game can drive hover sounds / highlights without
reconstructing rect hit-tests itself. The surface tracks the enabled button under the pointer each frame and
compares against last frame:

- `IsHovering` - true when the pointer is over an enabled button this frame.
- `HoverEntered` - true only on the frame the pointer moves ONTO a (different) button (a hover-enter, or sliding
  straight from one button onto another). False while staying on the same button and false on hover-exit. Wire
  this to a UI hover tick.
- `HoveredRect` - the rect of the hovered button, or null.

Read them after issuing the frame's widgets (before the next `Begin`). Disabled buttons do not register hover
(no affordance). Click behavior (the `Button` bool return) is unchanged. Headless-tested.

## 5.35.1 (custom 5.x line)

Cleanup: the textured-mesh model pass (5.35.0) now samples through the device built-in linear sampler
(`IGpuDevice.LinearSampler`, wrap-addressed) instead of a custom-created one - the same sampler Render2D uses, so
there is one shared sampler and nothing custom to own/dispose. Metal output is unchanged (all goldens still
pixel-identical).

Note: after 5.35.0 the cross-platform CI flagged `scene3d` on Direct3D11/WARP. Investigation showed it was NOT a
code regression: the committed `scene3d.direct3d11.txt` golden was a stale/divergent bake (its green box + red
sphere were rendered noticeably brighter than Metal - a pre-existing D3D11-vs-Metal divergence that "passed"
because each backend verifies its own golden). The current D3D11 output now MATCHES the Metal golden cell-for-cell
on exactly those cells, so the D3D11 golden was re-baked to the correct (Metal-matching) output. The sampler
change above is a no-op on the D3D11 pixels (verified: identical before/after).

## 5.35.0 (custom 5.x line)

Per-mesh albedo textures in `KhaozEngine.Render3D`. Meshes were colour-baked only (lit `vColor * vTint`); the
model pass can now sample a bound texture and fold it into the albedo (`texRgb * vColor * vTint`). The UV
plumbing was already in place (vertex UVs, the shaders threaded `vUv`, glTF `TEXCOORD_0`, and every
`MeshPrimitives` / `MeshBuilder` shape generates real UVs), so this wires the sampling end to end.

- **Texture API on `Scene3D`**: `LoadTexture(string pngPath)` (PNG/JPG via StbImageSharp) and
  `LoadTexture(byte[] rgba, int width, int height)` (raw RGBA, for procedural textures) return an opaque
  `Scene3D.TextureHandle`. New `LoadMesh(GltfMesh mesh, TextureHandle texture)` overload textures a mesh; the
  existing `LoadMesh(GltfMesh)` stays untextured. Textures are owned by the scene (shareable across meshes,
  freed in `Dispose`); an invalid/`default` handle falls back to untextured without throwing.
- **Model pass**: the resource layout gains an albedo `texture2D` (binding 1) + `sampler` (binding 2); each mesh
  carries its own material resource set, bound per mesh. The texture is **per mesh** (shared by its instances);
  per-instance tint still varies colour. A shared linear/wrap sampler is used.
- **Untextured stays pixel-identical**: an untextured mesh samples a 1x1 white default, so
  `white * vColor * vTint == vColor * vTint`. The committed `scene3d` / `scene2d` goldens are unchanged
  (verified, not re-baked). A new `scene3d_textured` golden (a checkerboard on a plane) covers the texturing
  path; `Render3DSample` draws a checkerboard-textured floor.
- Out of scope (follow-ups): texture alpha / transparency, mipmaps, per-instance textures, normal/roughness maps.

## 5.34.0 (custom 5.x line)

SFX in `KhaozEngine.Audio`: one-shot sound effects with optional 3D positional audio, alongside the existing
OpenAL streaming music. Games can now play fire / hit / death sounds, not just background music.

- **Shared OpenAL context.** OpenAL has one current context per process, so music and SFX now share a single
  internal `OpenAlContext` (device + context), owned by `AudioSystem`. `OpenAlMusicBackend` was refactored to
  borrow it; its public `OpenAlMusicBackend(ILogger?)` ctor still works (creates + owns its own context for
  back-compat). Streaming behavior is unchanged.
- **SFX seam.** New public `ISfxBackend` (+ headless-safe `NullSfxBackend`) mirroring `IMusicBackend`. The
  internal `OpenAlSfxBackend` whole-file decodes short sounds into single buffers and plays them on a fixed
  16-voice source pool (prefers an idle voice, falls back to round-robin stealing), with per-sound gain / pitch
  and optional 3D position.
- **`AudioSystem` SFX API.** `RegisterSfx(name)` / `RegisterSfxes(...)` (loaded from the same content dir as
  music, `name` + `.wav` / `.ogg` / `.mp3`), `PlaySfx(name, volume, pitch)` (non-positional), `PlaySfx3D(name,
  position, volume, pitch)`, `SetListener(position, forward, up)`, and a `SfxVolume` property (effective gain =
  Master x Sfx x call). An SFX failure is logged and swallowed so it can never disable music. No device =>
  silent Null backends (unchanged fallback). Added an `AudioSystem(IMusicBackend, ISfxBackend, ...)` ctor for
  tests; existing ctors are unchanged.
- **`WavSynth`** (public): writes mono 16-bit PCM WAV placeholder SFX (`WriteTone` with sine / square / saw +
  attack-release envelope, `WriteNoise` with a deterministic xorshift seed), so a game / sample can generate
  audible placeholders with no external assets.
- `WindowingSample` synthesizes a couple of sounds and plays them (Z = 2D, X = positional) as a live smoke.
- Headless tests cover the voice-pool policy, the SFX volume / routing / listener math (via a fake backend), and
  the WAV synth's RIFF/WAVE output. Verified: full suite green + the OpenAL SFX path runs cleanly in the sample.

## 5.33.0 (custom 5.x line)

Windowing platform swapped from `Veldrid.Sdl2` to **Silk.NET** (audit milestone 3, desktop distribution). The GPU
stays Veldrid behind the `KhaozEngine.Gpu` seam; only the window / input / loop changed. This removes the
`brew install sdl2` runtime requirement on macOS: `Silk.NET.Windowing.Glfw` bundles its GLFW natives per-RID, so a
clean checkout (and shipped game) runs without a system SDL2. `AppWindow`'s public API is unchanged, so consumers
(surfaces, `GameApp`, Hardpoint) need only a version bump.

- **`KhaozEngine.Windowing`**: `AppWindow` rewritten on `Silk.NET.Windowing` + `Silk.NET.Input` (GLFW). It creates
  the window with `GraphicsAPI.None` (the GPU is driven by Veldrid), reads the native handle, and builds the device
  from it. Keyboard / mouse / scroll go through the same edge-tracking `InputState` model as before; gamepads move
  from the old SDL poller to a `SilkGamepadReader` over `IInputContext.Gamepads`. Dropped `Veldrid` /
  `Veldrid.StartupUtilities`; deleted `SdlGamepadPoller`.
- **HiDPI**: the cursor (logical points from GLFW) is now scaled into framebuffer-pixel space so input and the
  render viewport share one coordinate system. Identity on a 1:1 display; keeps `Pointer` hit-testing correct on
  any display where the framebuffer size diverges from the logical window size.
- **`KhaozEngine.Gpu`**: new `GpuWindowHandle` (+ `GpuWindowKind` Cocoa/Win32/X11/Wayland) and
  `GpuDeviceContext.CreateForWindow(handle, w, h)` build a Veldrid swapchain from a native window handle (the GPU
  package takes no windowing dependency, just an `IntPtr` + kind). The old SDL2 `CreateWindow` is gone; headless
  `CreateHeadless` (goldens) is unchanged. Dropped `Veldrid.StartupUtilities`.
- **Samples**: removed the per-sample `CopySdl2` MSBuild targets (natives now come transitively from
  `Silk.NET.Windowing.Glfw`).
- Verified: `KE_GPU_TESTS=1` goldens pixel-identical (headless path untouched), full suite green, and a
  multi-frame windowed smoke (`KE_MAX_FRAMES`) runs `GuiSample` / `Render2DSample` / `Render3DSample` through the
  real Silk window + Veldrid Metal device and exits cleanly.

## 5.32.0 (custom 5.x line)

Cross-platform desktop bring-up — verification infrastructure (audit milestone 4, desktop scope). No renderer
change; the `KhaozEngine.Gpu` seam already abstracts the backend.

- **Backend-aware golden net**: the golden-snapshot references are now per-backend (`goldens/<name>.<backend>.txt`,
  resolved from `GpuBackendSelector.Select()`), so the same scene can be verified independently on Metal / Vulkan
  / D3D11 (software-rasterizer output differs from Metal pixel-for-pixel). The existing Metal references were
  renamed to `*.metal.txt`.
- **Cross-platform GPU CI** (`.github/workflows/cross-platform-gpu.yml`): a matrix that runs the golden tests on
  **macOS (Metal)**, **Windows (Direct3D11, WARP fallback)**, and **Linux (Vulkan via Mesa lavapipe)**. macOS
  verifies the committed Metal goldens immediately; Windows/Linux are baked via a manual `workflow_dispatch
  bake=true` run (uploads the per-backend goldens as artifacts to commit), then verified on push. The fast
  `ci.yml` (build/test/pack/publish) is unchanged.
- `KE_GRAPHICS_BACKEND` now also accepts `direct3d11`/`opengl` aliases (matching the enum names + the CI matrix
  values).
- New `docs/CROSS-PLATFORM.md` documents the matrix, software rasterizers, the per-backend golden flow, and the
  remaining productization gaps (per-RID SDL2/libveldrid-spirv bundling for shipped windowed apps; OpenGL +
  runtime clip-Y derivation; mobile as a separate project).

## 5.31.0 (custom 5.x line — drops the `-experimental` tag)

- **The 5.x custom stack graduates from experimental.** No code change — the `KhaozEngine.Gpu`/`Windowing`/
  `Render2D`/`Render3D`/`Gui`/`Audio`/`Particles`/`Game` packages drop the `-experimental` version suffix
  (`5.30.0-experimental` → `5.31.0`) and their descriptions drop the `EXPERIMENTAL` prefix. After the audit-
  driven P0 (correctness net, instancing, the full graphics-backend seam) and P1 work (GameApp facade, Gui +
  mesh fixes), the stack is the engine: a self-contained, MonoGame-free, single-GPU-abstraction game framework
  that Hardpoint ships on. The 5.x tag is now plain `vX.Y.Z`.
- **Foundation-line clarification resolved (documented).** The 4.x line is clarified to carry BOTH the legacy MonoGame-based packages
  AND the permanent MonoGame-free foundation packages the 5.x stack depends on (`Ecs`/`Serialization`/`Content`/
  `Diagnostics`/...); those graduate to the unified line when MonoGame is finally dropped, rather than churning
  consumers now. (CLAUDE.md governance + `Directory.Build.props` comments updated.)

## 5.30.0-experimental (custom 5.x line)

P1 batch 2 (5.x engine audit): the game-loop framework + POC cleanup.

### KhaozEngine.Game (NEW package, 5.x engine audit)

- **`GameApp` loop facade.** A subclass base that owns the window, clock, design viewport, pointer, and the 2D/
  (optional) 3D surfaces, and drives the per-frame composition in the correct order — so a game overrides
  `OnLoad`/`OnUpdate(dt)`/`OnDraw3D(scene)`/`OnDraw2D(batch)`/`OnResize` and **cannot get the frame ordering
  wrong** (clock → viewport → pointer → update → 3D submit+render → 2D begin/draw/end). `GameAppOptions` carries
  title/size/design-size/clear-colour/`Enable3D`. The raw `AppWindow.Run` path stays for special needs. Both
  the 3D (`Render3DSample`) and 2D (`GuiSample`) samples now run on it.

### KhaozEngine.Render3D (POC debt removed, 5.x engine audit)

- **Deleted `Render3DHost`** and its private `Key`/`FrameInfo` (a second window/loop + a second `Key` enum that
  duplicated Windowing). The standalone 3D demo path is now `GameApp`; the only public key enum is
  `KhaozEngine.Windowing.Key`. (`Render3DSnapshot` — the headless capture path — is unchanged.)

## 5.29.0-experimental (custom 5.x line)

P1 batch 1 (5.x engine audit): two contained fixes.

### KhaozEngine.Gui (styling unification, 5.x engine audit)

- The retained `Button` now uses `GuiStyle` (its hardcoded `Color`/`HoverColor`/`PressColor`/`TextColor` fields
  are replaced by a single `Style` + `Enabled`/`Selected`), and both the retained `Button` and the immediate
  `GuiSurface.Button` draw through one shared `GuiDraw.DrawButton` — so there's a single source of truth for
  button visuals (no more hand-duplicated colours that drift). The retained `Button.Update` now reserves its
  rect (`Pointer.BlockRegion`), closing the click-through gap in the retained path, and a disabled button never
  fires.

### KhaozEngine.Render3D (mesh lifecycle, 5.x engine audit)

- `Scene3D.UnloadMesh(MeshHandle)` frees a mesh's GPU buffers and recycles its slot, so a game that streams or
  swaps content no longer leaks. `MeshHandle` gains a generation; handles are validated on draw, so a stale
  handle (its mesh unloaded, or its slot since reused) is skipped rather than drawing freed/wrong geometry.
  Backed by a pure, headless-tested slot map. (Was: append-only index, no unload — a GPU-memory leak for any
  dynamic content lifecycle.)

## 5.28.0-experimental (custom 5.x line)

P0 hardening, stage 3 — graphics-backend seam, **phase 3d of 4 (final)**. Lockdown: the consumer-facing
renderer/windowing/Gui packages are confirmed Veldrid-free. The **graphics-backend seam is complete** -
Veldrid is contained to `KhaozEngine.Gpu` (all GPU) + Windowing's internal SDL2 window/input, and a future
Silk.NET backend is a new `IGpuDevice` impl, not a consumer-visible change.

### KhaozEngine.Gpu / KhaozEngine.Tests

- `GpuDeviceContext.Device` (the raw Veldrid `GraphicsDevice`) is now **internal** — no renderer consumes it;
  consumers use the engine-owned `GpuDevice`.
- **Veldrid lockdown test**: a reflection test asserts the public API of Render2D / Render3D / Windowing / Gui
  exposes no `Veldrid.*` type (it would fail the build on any future leak). The samples build unchanged against
  the migrated API.

## 5.27.0-experimental (custom 5.x line)

P0 hardening, stage 3 — graphics-backend seam, **phase 3c of 4**. **Render3D is now fully off Veldrid**, and
`Frame.Commands` is the engine command list — so both renderer packages run entirely on `KhaozEngine.Gpu`.
Behaviour unchanged (both goldens pixel-identical, 3D scene visually confirmed).

### KhaozEngine.Render3D (migrated; Veldrid dropped)

- **No longer references Veldrid.** `Scene3D`, `ModelRenderer` (instanced model pass + MRT), `PixelPostProcess`,
  `RenderResources`, `LineRenderer`, `BillboardRenderer`, `Render3DSurface`, `Render3DSnapshot`, and the standalone
  `Render3DHost` are all rewritten against the `KhaozEngine.Gpu` interface; the `Veldrid`/`Veldrid.SPIRV`/
  `Veldrid.StartupUtilities` (and the now-unneeded Newtonsoft) package references are removed. The full pipeline
  state (MRT, instancing, depth/raster/blend, the post-process chain, the debug-line + billboard overlays) is
  preserved. `Render3DHost` now delegates its window/loop to `AppWindow`.

### KhaozEngine.Windowing / KhaozEngine.Gpu

- `Frame.Commands` is now an `IGpuCommandList` (the transitional `Frame.GpuCommands` + the `GpuCommandLists.Wrap`
  bridge are removed). `AppWindow` no longer exposes the Veldrid `GraphicsDevice`/`Swapchain` — its public GPU
  surface is `GpuDevice`/`Backend`/`Capabilities`, and it drives the loop through the engine command list. The
  SDL2 window + input pump remain on `Veldrid.Sdl2` (the windowing/input platform layer — abstracting SDL2 is a
  separate future item). `KhaozEngine.Gpu`'s public device factories no longer expose Veldrid's
  `GraphicsDeviceOptions` (kept internal), so creating a device touches no Veldrid type.

## 5.26.0-experimental (custom 5.x line)

P0 hardening, stage 3 — graphics-backend seam, **phase 3b of 4**. The full engine-owned GPU abstraction lands
and **Render2D is migrated onto it (Veldrid dropped from Render2D entirely)**. Behaviour unchanged (both
goldens pixel-identical).

### KhaozEngine.Gpu

- **Full GPU interface + Veldrid implementation**: `IGpuDevice`/`IGpuResourceFactory`/`IGpuCommandList` + the
  resource handles (`IGpuBuffer`/`Texture`/`Sampler`/`Framebuffer`/`Pipeline`/`ResourceLayout`/`ResourceSet`/
  `ShaderSet`), engine-owned descriptions + 16 `Gpu*` enums, all mapped 1:1 to Veldrid inside `Internal/`. Veldrid
  is now hidden behind this interface (a future Silk.NET backend becomes a new `IGpuDevice` impl). Covers the
  full surface both renderers use, so phase 3c migrates Render3D against the same interface. A gated `[GpuFact]`
  smoke test exercises buffer+texture+pipeline+draw+readback on the device. Plus `GpuCommandLists.Wrap(...)` — a
  transitional bridge presenting a window's frame command list as an `IGpuCommandList` until phase 3c retypes
  `Frame.Commands`.

### KhaozEngine.Render2D (migrated; one fewer dependency)

- **No longer references Veldrid.** `SpriteBatch`/`Render2DCore`/`Render2DSurface`/`Render2DSnapshot`/`Texture2D`/
  `SpriteFont` are rewritten against the `KhaozEngine.Gpu` interface; the `Veldrid`/`Veldrid.SPIRV` package
  references are removed. `AppWindow` now also exposes `GpuDevice` + `Frame.GpuCommands` for 2D consumers
  (Render3D still uses the Veldrid path until 3c). Submission order, scissor, and the persistent vertex buffer
  are preserved; the 2D golden passes pixel-identical.

## 5.25.0-experimental (custom 5.x line)

P0 hardening, stage 3 of 3 — the graphics-backend seam, **phase 3a of 4** (foundation). See
`docs/superpowers/specs/2026-06-16-gpu-backend-seam-design.md`. Behaviour
on Metal is unchanged (both goldens pass pixel-identical).

### KhaozEngine.Gpu (NEW package)

- **Backend-seam foundation**: `GpuBackendKind`, `GpuCapabilities` (clip-Y / depth-range, read from the device),
  `GpuBackendSelector.Select()` (probe `RuntimeInformation` → Metal on macOS / Direct3D11 on Windows / Vulkan on
  Linux, with a `KE_GRAPHICS_BACKEND` env override; the core logic is a pure `Select(env, os)` overload that is
  headless-tested), and `GpuDeviceContext` factories (`CreateWindow`/`CreateHeadless`) that own device creation
  behind the selector. This is the first of four phases: it centralizes the previously hard-coded
  `GraphicsBackend.Metal` (removed from `AppWindow`, `Render3DHost`, and both snapshot helpers), and plumbs the
  device capabilities — without yet wrapping the GPU resource types (phases 3b/3c rewrite the renderers against
  engine-owned GPU interfaces so Veldrid stops appearing on any public API; 3d migrates consumers).

### Windowing / Render2D / Render3D

- Device creation now routes through `KhaozEngine.Gpu`; `AppWindow` exposes `Backend`/`Capabilities`. The
  clip-Y/depth derivation from `GpuCapabilities` is marked for phase 3c (behaviour identical on Metal for now).

## 5.24.0-experimental (custom 5.x line)

P0 hardening, stage 2 of 3 (5.x engine audit): submission performance. Internal
rewrite; `Scene3D.Draw`/`SpriteBatch.Draw` public APIs unchanged. Guarded by the stage-1 golden-snapshot net
(both 3D + 2D goldens pass pixel-equivalent).

### KhaozEngine.Render3D (perf)

- **GPU instancing.** The model pass no longer uploads a UBO + issues a draw per instance. Per-frame uniforms
  (view-projection, lights, camera) live in a 176-byte UBO uploaded once per frame; per-instance data (model
  matrix, tint, emissive, specular) moves to an instanced vertex stream uploaded once per frame; each UNIQUE
  mesh draws once with `instanceCount`. A 200-object board goes from ~200 UBO uploads + 200 draws to 1 UBO
  upload + ~(unique-mesh) instanced draws. (The previous per-instance ceiling was ~150-300 objects.)

### KhaozEngine.Render2D (perf)

- **Persistent SpriteBatch vertex buffer.** `SpriteBatch.Flush` no longer creates+disposes a GPU buffer and
  allocates a managed array per texture-run every frame; it uploads sub-ranges into one persistent growable
  buffer (uploaded directly from the run's backing storage, no `ToArray`). Removes the worst per-frame
  allocation/driver-churn hot spot in 2D.

## 5.23.0-experimental (custom 5.x line)

P0 hardening, stage 1 of 3 (5.x engine audit): correctness net + low-risk fixes. No
public API change.

### KhaozEngine.Render3D / Render2D / Gui (fixes + perf)

- **Fixed** a `ResourceLayout` GPU-resource leak in `ModelRenderer` (now stored + disposed like the other
  renderers).
- **Perf**: hoisted the invariant `SetPipeline`/`SetGraphicsResourceSet` binds out of the per-instance model
  loop (one bind per pass, not per instance); cached the post-process palette scratch array (was a 260-float
  allocation every frame); `ScreenStack.Update` reuses a scratch list instead of allocating a `Screen[]` every
  frame. (The per-instance UBO upload — the real 3D scaling ceiling — is stage 2.)
- **Mesh winding made consistent**: a new winding-vs-normal test net (applied to every `MeshPrimitives` shape)
  found that Cylinder/Cone/Pyramid-base/Sphere wound their triangles opposite their own outward normals (two
  conflicting conventions). Flipped those generators so winding is uniformly CCW-outward. Render-neutral today
  (`FaceCullMode.None`; positions + normals unchanged) but unblocks enabling back-face culling later.

### KhaozEngine.Tests

- **Golden-snapshot GPU regression net**: gated `[GpuFact]` tests render fixed 3D + 2D scenes through the
  offscreen snapshot path and compare a downsampled colour grid to committed references with tolerance —
  catching shader/blend/UBO/winding/orientation regressions that headless tests and `FaceCullMode.None`
  cannot see. Skipped by default (and on GPU-less CI); run with `KE_GPU_TESTS=1`, re-bake with
  `KE_UPDATE_GOLDENS=1`.

## 5.22.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive; one internal format change)

- **More mesh primitives**: `MeshPrimitives.Torus`, `Capsule`, `RoundedBox`, `Plane` (subdividable flat grid)
  join Box/Tile/Cylinder/Cone/Pyramid/Wedge/Sphere — smooth normals on curved surfaces, degenerate-arg
  clamping, CCW-outward winding.
- **UV texture coordinates**: `ModelVertex` gains a `Vector2 Uv` (vertex now 48 bytes) and every primitive
  generates sensible UVs (per-face for flats, cylindrical for cylinder/cone, lat/long for sphere, etc.);
  `MeshBuilder` carries UVs through, `GltfLoader` reads `TEXCOORD_0`. The model shader passes the UV through
  (it is not yet sampled — textured-mesh *rendering* is a later step; this makes the geometry data ready so
  primitives don't need re-touching then). Existing meshes are unaffected (the 3-arg `ModelVertex` ctor
  defaults UV to zero). Render verified unchanged after the vertex-format change.
- **`MeshOps`**: `WithSmoothNormals(mesh, epsilon)` welds vertices by position and averages normals (smooth a
  faceted mesh); `RecomputeFlatNormals(mesh)` for per-triangle face normals. Both return copies.

## 5.21.0-experimental (custom 5.x line)

### KhaozEngine.Particles (NEW package)

- **Particle simulation** (pure, MonoGame/Veldrid-free — System.Numerics + BCL only): `ParticleSystem`
  (capacity-bounded pool, swap-remove compaction, contiguous `Active` span), `EmitterConfig` (lifetime/speed
  ranges, cone `Direction`+`SpreadDegrees`, gravity, drag, start/end size + colour, `Spark`/`Puff` presets),
  `Particle`, and a `RateAccumulator` for continuous emission. Fully **deterministic** — an internal xorshift32
  RNG seeded per system, no `System.Random`/`DateTime`/wall-clock — so two systems with the same seed + calls
  produce identical particles (headless-testable). Render-agnostic: a game splats `system.Active` to any
  renderer.

### KhaozEngine.Render3D (additive)

- **Camera-facing billboards** for displaying particles (and any sprite-in-3D): `Scene3D.DrawBillboard(worldPos,
  size, color, BillboardBlend.Alpha|Additive)` draws a soft round disc (smoothstep falloff in the shader, no
  texture) facing the camera, composited over the post image like the debug lines. Alpha for smoke/puffs,
  additive for glowing sparks/flashes (pairs with the 5.19 emissive look). The camera basis is computed once
  per frame. Render3D deliberately does NOT depend on KhaozEngine.Particles — the game loops `Active` and calls
  `DrawBillboard` per particle. Snapshot-verified (additive spark burst + alpha puff over a lit scene). From
  the Hardpoint testbed.

## 5.20.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Debug line/wireframe overlay**: immediate-mode `Scene3D.DebugLine/DebugRay/DebugBox/DebugGrid/DebugAxes/
  DebugCircle` draw coloured lines on top of the post-processed image with the camera's view-projection (depth
  disabled, alpha-blended overlay). For dev viz and in-game cues — tower range rings (`DebugCircle` on the
  ground), flow-field arrows, board grids, bounds, RGB axis gizmos. Segments accumulate per frame and clear in
  `Begin()` (same lifecycle as instances). The geometry builders live in a pure, headless-tested
  `DebugShapes` (Box/Grid/Circle/Axes). Backed by an internal `LineRenderer` (LineList pipeline). Snapshot-
  verified (grid + box wireframe + ground ring + axes + line over a lit scene). From the Hardpoint testbed.

## 5.19.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Lighting + materials**: the model pass gains a second **fill light** (`PixelPostProcessSettings.
  FillLightDirection`/`FillLightColor`, a dim cool default that softens shadowed sides) on top of the existing
  key light, **Blinn-Phong specular** highlights, and **emissive** self-illumination — driven by a new
  per-instance `Material` (`Emissive`, `Specular` strength, `Shininess`) with `Material.None` (matte,
  the prior look), `Material.Glowing(color)`, and `Material.Shiny(strength, shininess)`. (The glow factory is
  `Glowing` rather than `Emissive` because that name is taken by the property.) `Scene3D.Draw(mesh, world,
  tint, material)` and a `MeshInstance.Material` field (additive, default matte) carry it; the
  `Scene3DBinder` scene overload applies it. The shader now also receives the camera eye for the specular
  view vector. Default look (matte materials, dim fill) is unchanged. Snapshot-verified (matte vs shiny vs
  emissive spheres + fill on form). From the Hardpoint testbed.

## 5.18.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Procedural mesh primitives.** `MeshPrimitives` gains `Cylinder`, `Cone`, `Pyramid`, `Wedge`, and `Sphere`
  alongside the existing `Box`/`Tile`, all returning a `GltfMesh` with white vertex colour and CCW outward
  winding. `Cylinder(radius, height, segments, capped)` and `Cone(...)` seat their base at y=0 along +Y with
  smooth radial side normals and flat center-fan caps (`capped:false` drops the caps). `Pyramid(baseSize,
  height)` and `Wedge(size, height)` are flat-shaded solids (square-based pyramid; right-triangular prism ramp
  rising -Z->+Z). `Sphere(radius, rings, segments)` is a UV sphere centered at the origin with smooth radial
  normals. Degenerate args clamp (`segments>=3`, `rings>=2`).
- **`MeshBuilder`** composes transformed, optionally re-coloured `GltfMesh` parts into a single mesh, so a game
  can build a multi-part, multi-colour silhouette in code and draw it as one tinted instance. `Add(part,
  transform)` keeps the part's colours; `Add(part, transform, color)` bakes a colour onto the appended verts.
  Positions transform by `Vector3.Transform`; normals by the inverse-transpose of the linear 3x3 (correct under
  non-uniform scale, falling back to the raw linear part if non-invertible) then re-normalized; indices offset
  by the running vertex count. `Build()` throws if the total exceeds the `ushort` vertex ceiling (65535).
  Fluent. `VertexCount`/`IndexCount` expose the running totals.

## 5.17.0-experimental (custom 5.x line)

### KhaozEngine.Gui (additive)

- **Immediate-mode UI surface**: `GuiSurface` lets a game running a single `window.Run(frame => ...)` loop
  author a HUD-over-3D and full-screen menus with one call site per widget instead of hand-rolling
  `SpriteBatch` fills + per-widget `Pointer.BlockRegion` bookkeeping. `Begin(batch, pointer)` (the `batch` may
  be `null` for headless tests) then `Panel`/`Swatch`/`Label` (positioned or box-aligned via `GuiAlign`) and
  `Button(...) -> bool` (hover/press/disabled/selected visuals, fires on the press-origin `IsTapIn` invariant).
  `PointerCaptured` reports whether the pointer's press-origin landed on any widget this frame, centralizing the
  click-through gate that keeps a tap on a button from leaking to the world. `GuiStyle` carries the default
  palette (matching the retained `Button`) and is overridable per call or on the surface. Draws through the
  existing `GuiDraw` primitives and reuses the caller's begun batch so it composes with the design viewport for
  free. Headless-tested (interaction + capture, no GPU); demoed in `GuiSample`'s Immediate screen. From the
  Hardpoint testbed (the flagged immediate-mode-Gui engine-first candidate).

## 5.16.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **ECS->Scene3D binding**: render-component types `Transform3D` (position/scale/rotation; zero scale/rotation
  treated as identity) and `MeshInstance` (`MeshHandle` + `Vector4` tint; zero tint = white), plus
  `Scene3DBinder.Submit(world, scene)` which draws every entity carrying both. Replaces the per-game
  "query entities -> compute matrix -> Draw" loop with one call. The pure core
  `Submit(world, Action<MeshHandle,Matrix4x4,Vector4>)` is headless-tested with a real `World` + a recording
  delegate. Render3D now references the MonoGame-free `KhaozEngine.Ecs`. From the Hardpoint testbed.

## 4.11.0 (MonoGame 4.x line)

- **`KhaozEngine.Content.ColorHex`**: `FromHex(string) -> Vector4` (RGBA 0..1; accepts `#RRGGBB` / `RRGGBB` /
  `#RRGGBBAA`, leading `#` optional, missing alpha = opaque) and `ToHex(Vector4) -> #RRGGBBAA`. A
  MonoGame-free, Veldrid-free home for parsing config colour strings, usable by both the pure domain and the
  render stack (it lives in Content because games already reference it for config and it has no GPU deps).
  Headless-tested. (Centralizes a hex-colour helper games were hand-rolling; from the Hardpoint testbed.)
  Shared 4.x version bumped 4.10.0 -> 4.11.0.

## 5.15.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **`IsoCamera3D.Frame(center, size, margin = 1.1f)`**: aim the camera at a bounds center and size `OrthoSize`
  so an axis-aligned bounds fits the viewport (projects the 8 corners into view space, fits both axes against
  the current `AspectRatio`/`Zoom`). Replaces the per-game "OrthoSize = max(w,h)*spacing*k" guesswork with a
  correct fit. Pure math, headless-tested (tight fit at margin 1, slack with margin > 1, wide-aspect). From
  the Hardpoint testbed (board framing).

## 5.14.0-experimental (custom 5.x line)

Engine maturity from the Hardpoint 3D testbed: per-instance tint + code-built mesh primitives, so one mesh
draws in many colors and games stop hand-rolling primitive geometry.

### KhaozEngine.Render3D (additive)

- **Per-instance tint**: `Scene3D.Draw(MeshHandle, Matrix4x4 world, Vector4 tint)` (the existing
  `Draw(mesh, world)` defaults to white = no tint). The tint multiplies the lit colour in the model shader
  (a `vec4 Tint` added to the model UBO; `SceneInstances.Instance` carries it). Lets a single white mesh be
  drawn in many colours instead of one mesh per colour.
- **`MeshPrimitives`**: `Box(size)` and `Tile(size, thickness)` build `GltfMesh` cubes/flat-tiles in code
  (24 verts / 36 indices, per-face normals, white vertex colour for tinting), no asset files. Headless-tested
  (vertex/index counts, corner positions, tile base at y=0). Verified visually (one box drawn in three tints).

## 5.13.1-experimental (custom 5.x line)

### KhaozEngine.Render3D (fix)

- **3D scenes rendered vertically upside-down.** `ModelRenderer` multiplied the camera view-projection by a
  clip-Y flip (`M22 = -1`), which inverted the image (world-up landed at the bottom) AND disagreed with
  `IsoCamera3D.ScreenToGround` picking, which uses the *unflipped* matrix. The flip was invisible on symmetric
  content (a sphere, a starfield, a symmetric instance grid) and only showed up on the first asymmetric scene
  (an iso game board). Removed the flip: the view-projection is uploaded as-is, so the render is right-side up
  and consistent with picking. Verified with an asymmetric two-sphere snapshot (world-up now maps to
  screen-up). No API change.

## 4.10.0 (MonoGame 4.x line)

- **`KhaozEngine.Ecs` is now MonoGame-free**: dropped the unused `MonoGame.Framework.DesktopGL` package
  reference (the ECS source uses no Xna types; its only dependency, `KhaozEngine.Serialization`, is pure BCL).
  This lets the custom MonoGame-free 5.x stack reuse the same ECS. No API or behaviour change; existing 4.x
  consumers are unaffected (they carry their own MonoGame reference). Shared 4.x version bumped 4.9.0 -> 4.10.0.

## 5.13.0-experimental (custom 5.x line)

Render3D grows from a single-model demo into a scene a game can use (Phase A of the Hardpoint 3D vertical
slice): many instances per frame, screen->ground picking, and composition into an `AppWindow` alongside a
Render2D HUD.

### KhaozEngine.Render3D

- **Multi-instance `Scene3D`** (breaking vs the demo API): `LoadMesh(GltfMesh) -> MeshHandle` (load several
  meshes), then per frame `Begin()` + `Draw(MeshHandle, Matrix4x4 world)` to queue instances. The old
  single-model `LoadModel`/`Spin` is removed; instances are drawn through the iso camera + `PixelPostProcess`
  in one pass. `SceneInstances` (the instance queue) is headless-tested.
- **`IsoCamera3D` picking**: `ScreenToGround(screenPixel, viewportW, viewportH, groundY = 0)` and
  `ScreenToRay(...)` (returns the new `Ray` struct) unproject a screen pixel into the world. Pure /
  headless-tested (round-trips a known ground point; screen-centre maps to the camera target).
- **`Render3DSurface`**: binds a `Scene3D` to a `KhaozEngine.Windowing.AppWindow` and renders into the
  window's per-frame command list, so a 3D scene composes into the same window as a `Render2D` HUD (3D fills
  the frame, the HUD draws on top). Mirrors `Render2DSurface`; adds a Render3D->Windowing reference.
- `Scene3D.RenderInternal` now records into a caller-supplied `CommandList` + target `Framebuffer` (the
  caller owns Begin/End/Submit); `Render3DHost` and `Render3DSnapshot` drive that path. `ModelRenderer.Draw`
  split into `BeginModelPass` (clear once) + `DrawInstance` (per instance). `Render3DSnapshot` gains a
  multi-instance `Capture(width, height, setup, drawFrame, frames)` overload (verified a 3x3 instance grid).
- `Render3DSample` now submits a grid of instances instead of spinning one model.

## 5.12.0-experimental (custom 5.x line)

Native packaging (milestone 3), part 1: bundle openal-soft so audio no longer depends on the deprecated
macOS system OpenAL.

### KhaozEngine.Audio

- Reference `Silk.NET.OpenAL.Soft.Native` (1.23.1) and create the API with `AL.GetApi(true)` /
  `ALContext.GetApi(true)` (the openal-soft library-name container). The native ships RID-specific
  (linux-arm/arm64/x64, osx-arm64/x64, win-arm64/x64/x86) and flows to the consuming app's `runtimes/<rid>/
  native/` via the runtime graph. Verified on osx-arm64: the process now loads the bundled
  `libopenal.dylib`, not `/System/Library/Frameworks/OpenAL.framework` (deprecated), with the audio device
  opening cleanly. Deviceless CI still falls back to `NullMusicBackend` as before.

### Known gap (SDL2)

- SDL2 is still sourced on macOS by a per-sample `CopySdl2` MSBuild target that copies a Homebrew-installed
  `libSDL2.dylib` (Veldrid.SDL2 4.9.0 bundles only osx-x64, no osx-arm64/linux), so a clean macOS checkout
  still needs `brew install sdl2`. A proper bundled SDL2 across RIDs is folded into the cross-platform-backends
  milestone (it shares the Windows/Linux native-coverage work and can't be run-verified on the Apple-Silicon
  dev box).

## 5.11.0-experimental (custom 5.x line)

Input breadth, part 2: gamepad + touch state on `InputState`. Additive and non-breaking. The state model is
headless-tested; a *live* gamepad smoke needs a physical controller (the SDL polling is best-effort and
compile-verified, defensive on every call) and touch is mobile (the type + any mapping stay testable).

### KhaozEngine.Windowing (additive)

- `GamepadState` + `GamepadButton`: immutable per-frame pad snapshot (button down/pressed/released sets, two
  analog sticks raw, two triggers), with `IsDown`/`WasPressed`/`WasReleased` and radial-deadzone stick
  helpers. `GamepadState.Disconnected` is the not-connected sentinel.
- `Deadzone.Radial(stick, deadzone)`: shared magnitude-based deadzone (rejects small diagonal drift as a
  whole, rescales the remainder so the edge maps to 0 and full tilt to 1).
- `TouchPoint` + `TouchPhase`: a touch point (stable id, position, phase). Empty on desktop; mobile fills it.
- `InputState` gains `Gamepads` / `Touches` (default empty) plus `Gamepad(index)` (returns
  `GamepadState.Disconnected` when absent) and `PrimaryGamepad`. The existing 10-arg constructor is unchanged
  (the new parameters are optional), so all current call sites keep compiling.
- `AppWindow` polls SDL2 game controllers each frame via a defensive `SdlGamepadPoller` (every SDL call
  guarded; degrades to no pads on any failure, never affecting the window loop). `WindowingSample` shows a pad
  readout and lets the left stick nudge the box / A reset it.

## 5.10.0-experimental (custom 5.x line)

Input breadth, part 1 (milestone 2 of engine maturity): pause/time-scale and a gesture seam. All additive in
`KhaozEngine.Windowing`, MonoGame-free, and headless-tested. Gamepad + touch state land next (part 2).

### KhaozEngine.Windowing (additive)

- `GameClock`: 5.x-native clock separating real delta from a scaled simulation delta, driven by a raw
  `float` dt (`AppWindow.Frame.Dt`). `TimeScale` (slow-mo / normal / fast-forward), `Pause`/`Resume`
  (orthogonal to scale), `RealDeltaSeconds`/`ScaledDeltaSeconds`, `ElapsedRealSeconds`/`ElapsedScaledSeconds`
  accumulators, and `Paused`/`Resumed` edge events. The custom-stack analogue of the 4.x
  `KhaozEngine.Time.GameClock` (which is MonoGame-coupled via `GameTime`).
- `GestureRecognizer`: single-pointer tap / long-press / drag from raw (isDown, position, dt) frames or a
  `Pointer`. Per-frame flags (`Tapped`, `LongPressed`, `DragStarted`/`DragEnded`) plus `IsDragging`,
  `DragDelta`/`DragTotal`/`DragStart`; tunable `MoveThreshold`/`TapMaxDuration`/`LongPressDuration`. Feed it
  the design-space `Pointer.Position` so gestures match scaled/letterboxed draws; use real (unscaled) dt.
- `PinchRecognizer`: two-point pinch -> relative `Scale`, per-frame `ScaleDelta`, midpoint `PanDelta` +
  `Center`. Headless-testable; live it needs two touch points (mobile).
- `WindowingSample` now demos drag/tap/long-press and a `GameClock` (Space pauses, 1/2/3 set speed) on a
  `DesignViewport`.

## 5.9.1-experimental (custom 5.x line)

### KhaozEngine.Render2D (fix)

- `SpriteBatch` scissor clipping now composes with a design viewport. Two bugs: the clip rect passed to
  `SetScissor` was treated as window points even under `Begin(IDesignViewport)` (so it ignored the design
  scale + letterbox offset), and the clip helpers re-`Begin()`ed in screen space to resume after the scissor
  (throwing away the design transform, so clipped content drew unscaled at raw design coordinates). Now
  `SetScissor`/`ClearScissor` **flush internally and preserve the active transform** (no surrounding `Begin`
  needed), and a clip rect is mapped through the active viewport. New pure overload
  `ComputeScissor(rect, IDesignViewport?, ...)` is headless-tested. Visible symptom: on the resized `GuiSample`
  Widgets screen the scrollable list drew unscaled and escaped its panel.

### KhaozEngine.Gui (fix)

- `ScrollablePanel.BeginClip`/`EndClip` are now one-liners over `SetScissor`/`ClearScissor` (they no longer
  `End()`+`Begin()` around the scissor, which was the source of the lost-transform bug). Clipped content under
  a `DesignViewport` now scales and clips correctly.

## 5.9.0-experimental (custom 5.x line)

Resolution independence + layout (milestone 1 of engine maturity). The window already resized the
framebuffer; this adds the missing design layer so content scales, centers, and letterboxes instead of
sitting at hard pixel coordinates. Additive across Windowing/Render2D/Gui.

### KhaozEngine.Windowing (additive)

- `DesignViewport` + `IDesignViewport`: a fixed design space (e.g. 960x540) mapped onto the current window
  with a `ScaleMode` (`Fit` = letterbox/pillarbox centered, `Fill` = cover/crop centered, `Stretch` =
  distort). Exposes `ScaleX/Y`, `OffsetX/Y`, `ContentBounds`, `DesignBounds`, `ScreenToDesign`/`DesignToScreen`,
  and `GetClipProjection(viewportW, viewportH)` for the batch. Pure math, headless-tested.
- `Pointer.Update(InputState, IDesignViewport)`: maps the cursor into design space so all bounds helpers
  hit-test in the same coordinates draws use (press-origin click-through invariant preserved). The existing
  `Update(InputState)` is unchanged (identity). In-window guard still uses the raw window position.

### KhaozEngine.Render2D (additive)

- `SpriteBatch.Begin(IDesignViewport)`: draw in design coordinates; scaling, centering, and letterbox happen
  for free. Mirrors `Begin(Camera2D)`. Existing `Begin()` / `Begin(Camera2D)` unchanged.

### KhaozEngine.Gui (additive)

- `Layout.Resolve(parent, Anchor, width, height, marginX, marginY)`: pure anchor-based rect placement
  (`TopLeft`..`BottomRight`, `Center`, `Stretch`) against the design viewport or a container, so widgets stop
  hard-coding absolute pixels. Headless-tested.
- `ScreenStack.Update(dt, InputState, IDesignViewport)`: routes the pointer through the design viewport.
- `Screen.BackgroundColor` + `Screen.DrawBackground(batch, white, viewport)`: opaque full-screen fill
  convention for non-modal screens (fixes screens showing the one below through their gaps).
- `GuiSample` now drives a `DesignViewport(960, 540, Fit)`: resize the window and the UI scales, centers, and
  letterboxes, with hit-testing aligned and opaque backgrounds on the full screens.

## 5.8.1-experimental (custom 5.x line)

### KhaozEngine.Render2D (fix)

- `SpriteBatch` now preserves **submission order across textures**. It previously grouped all quads globally
  per texture and flushed those groups in first-seen order, so a draw issued later could paint *under* or
  *over* the wrong layer whenever textures interleaved (text vs. solid-fill rectangles). Visible symptom: a
  menu's text bled through a modal panel drawn on top of it, and in-screen overlays (dropdown popup, tooltip)
  could land beneath later fills. Quads are now coalesced into submission-ordered *runs* — only consecutive
  same-texture draws merge — so painter's order is correct. Pure run-coalescing logic is headless-tested
  (`QuadRunBuilder`); no API change.

## 5.8.0-experimental (custom 5.x line)

The heavy `KhaozEngine.UI` widgets ported onto the custom stack: `Dropdown`, `TextInput`, `Tooltip`,
`PopupPanel`, `ScrollablePanel` in `KhaozEngine.Gui`, plus a scissor-clip capability in `KhaozEngine.Render2D`
and a headless `TextEntry` helper. Game-specific coupling from the 4.x versions (VirtualResolution,
LayoutConstants, nav/top-bar assumptions) was dropped — these are clean generic widgets.

### KhaozEngine.Render2D (additive)

- `SpriteBatch` gains **scissor clipping**: `SetScissor(Rect)` / `ClearScissor()` (call between an `End` and the
  next `Begin`) clip subsequent draws to a viewport-space rect. `ComputeScissor(...)` is a pure, unit-tested
  helper that scales viewport points to framebuffer pixels (DPI / Retina aware) and clamps to the framebuffer.
  The pipeline now enables the scissor test (default = full framebuffer, so unclipped draws are unaffected).

### KhaozEngine.Gui (additive)

- `TextEntry` — headless text-entry helper: maps a frame's `InputState` key presses (+ shift, US layout) to
  typed characters and applies them to a string (append printable, Backspace deletes), with max-length and a
  char filter. No SDL text-input plumbing, so it is fully unit-testable. (No IME/locale/dead-keys.)
- `TextInput` — single-line field: tap to focus / tap-out to blur; while focused, typed keys edit the text
  (via `TextEntry`); bordered field with placeholder + blinking caret. Ported from the 4.x `UI.TextInput`
  (which hooked SDL's TextInput event).
- `Dropdown` — selector with a trigger + an option list that opens below; tap to open/select, release-outside
  dismisses. Two-phase draw (`Draw` trigger inside any clip, `DrawOverlay` the open list last/unclipped).
- `Tooltip` — auto-sized floating bubble; `ComputeBounds(...)` is a pure layout function (sizes to content,
  sits above the anchor, flips below when it would cross the top margin, clamps into the viewport) testable
  with a fake `ITextMeasurer`. `Show`/`Hide`/`Draw` instance API.
- `PopupPanel` — modal dialog: scrim, centered auto-sized panel (clamped between a min height and a viewport
  fraction), title bar, label/value content rows (`PopupRow` Header/Stat/Spacer), and a footer dismiss button
  (+ optional primary action). `Update` blocks the pointer over the panel. (No internal scroll — that is
  `ScrollablePanel`.)
- `ScrollablePanel` — vertically-scrolling fixed-height list: wheel (while hovering) + drag scroll, clamped to
  range; the owner draws rows positioned via `ItemBounds` between `BeginClip`/`EndClip` (which set/clear the
  SpriteBatch scissor); `TappedItemIndex` hit-tests a row (gaps return -1). Ported from the 4.x
  `UI.ScrollablePanel` (clipping now via the engine scissor instead of MonoGame's).
- Headless tests cover all of the above logic (`TextEntryTests`, `TextInputTests`, `DropdownTests`,
  `TooltipTests`, `PopupPanelTests`, `ScrollablePanelTests`, plus `SpriteBatchScissorTests` for the DPI scissor
  math) — 40 new, 752 green. `GuiSample` gains a "Widgets" screen driving the dropdown, text field, scrollable
  list, hover tooltip, and a modal popup. NOTE: this stack is Metal-only and was built without a display, so the
  GPU scissor clip itself is not yet visually verified (the scroll logic + pixel math are).

## 5.7.0-experimental (custom 5.x line)

Core `KhaozEngine.UI` widgets ported onto the custom stack: `Label`, `Panel`, `Slider`, `Toggle` in
`KhaozEngine.Gui`, plus a device-free text-layout helper in `KhaozEngine.Render2D`.

### KhaozEngine.Render2D (additive)

- `ITextMeasurer` — a text-measurement seam (`LineHeight` + `Measure(string)`) implemented by `SpriteFont`.
  Lets the layout math be unit-tested headlessly with a fake measurer (no GPU device / real font).
- `TextLayout` — pure word-wrap + alignment helpers over `ITextMeasurer` (`AlignedX`, `Wrap`,
  `MeasureWrappedHeight`), plus pixel-snapped draw overloads taking a `SpriteBatch` + `SpriteFont`
  (`DrawAligned`, `DrawWrapped`) and a `TextAlign` enum. Ported from the 4.x MonoGame-bound `UI.TextHelper`.

### KhaozEngine.Gui (additive)

- `Label` — non-interactive text widget: aligned (left/center/right) and optionally word-wrapped within its
  bounds, vertical-centered for single lines. Pure presentation over the (tested) `TextLayout`.
- `Panel` — filled, optionally-bordered container/backdrop; `BlocksPointer` reserves its region on the
  `Pointer` (via `BlockRegion`) so a layer beneath can skip hit-testing under it (modal scrims/popups).
- `Slider` — horizontal slider over `Pointer`; the bounds are the track. A press that begins inside starts a
  drag and jumps the value to the pointer (clamped 0..1), tracking until release; a press that began elsewhere
  is ignored (press-origin invariant). `Update` returns whether the value changed.
- `Toggle` — two-state switch; a valid tap (press + release both inside, the click-through invariant) flips
  `IsOn` and fires `OnChanged`. Drawn as a track with a thumb that slides to the on/off side.
- Internal `GuiDraw` fill/border helpers (1x1-white-texture rects) shared by the widgets.
- Headless tests cover the layout math (`TextLayoutTests`), the slider drag/clamp/press-origin behaviour
  (`SliderTests`), the toggle flip + click-through (`ToggleTests`), and panel pointer-blocking (`PanelTests`).
  `GuiSample`'s settings screen now drives a `Panel`, `Label`s, a volume `Slider` (with live readout), and a
  fullscreen `Toggle`. The heavier widgets (ScrollablePanel/Dropdown/TextInput/PopupPanel) are a follow-up batch.

## 5.6.0-experimental (custom 5.x line)

New `KhaozEngine.Gui` package — the screen-stack + first widget on the custom stack.

### KhaozEngine.Gui (new)

- `ScreenStack` — owns a stack of `Screen`s and routes input top-to-bottom: the first visible,
  non-passthrough screen that reports consuming input blocks the screens below it; a modal
  (`PassUpdateThrough == false`) screen also stops them updating; `AlwaysReceivesInput` opts back in. Draws
  bottom-to-top and drives transitions. Exposes a shared `Pointer` + `InputState`. Ported faithfully from the
  MonoGame `ScreenManager` (uses `dt` instead of `GameTime`; the click-through layering model is intact).
- `Screen` — base UI surface: `Update(dt, receivesInput)` (return whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder`/`PassUpdateThrough`/`AlwaysReceivesInput`/transitions/`ExitScreen`.
- `Button` — bounds-aware widget over `Pointer.IsTapIn` (press-origin click-through invariant), hover/press
  visuals. Built on `KhaozEngine.Windowing` + `KhaozEngine.Render2D`.
- Headless `ScreenStackTests` cover the routing core (consume-blocks-lower, modal-stops-lower,
  AlwaysReceivesInput, transition-on, animated exit). `GuiSample` shows a menu that pushes a modal settings
  screen. Pause/timescale, per-player scoping, touch gestures, and the wider widget set
  (Slider/Dropdown/ScrollablePanel/...) are follow-ups.

## 5.5.0-experimental (custom 5.x line)

Bounds-aware pointer input (the click-through core) in `KhaozEngine.Windowing`, and the renderer windowing
consolidates onto `AppWindow`.

### KhaozEngine.Windowing

- `Pointer` — a bounds-aware pointer over the mouse with the **press-origin click-through invariant**, ported
  from the MonoGame `InputManager` core. `Update(InputState)` per frame, then hit-test with `IsTapIn`,
  `IsPressingIn`, `IsHoveringIn`, `IsPointerIn`, `IsReleasedOutside`, `IsDraggingIn`/`GetDragDelta`,
  `IsTapFromTo`, plus region blocking (`BlockRegion`/`IsBlocked`) for overlay click-through. New `Rect`
  type for hit-testing. Headless `PointerTests` cover the invariant (press-outside-release-inside is not a
  tap). Touch/gamepad/pinch/menu-nav and virtual-resolution transforms are still follow-ups.

### KhaozEngine.Render2D (cleanup)

- Removed the standalone `Render2DHost` + its own `Key`/`FrameInfo` (superseded by `AppWindow` from
  Windowing). Draw into a window via `Render2DSurface(AppWindow)`; `Render2DSnapshot` (headless) is
  unchanged. `Render2DSample` now uses `AppWindow`. Render2D dropped its direct `Veldrid.StartupUtilities`
  reference (windowing comes from `KhaozEngine.Windowing`). The `WindowingSample` gained a clickable button
  demonstrating `IsTapIn` + region-blocking.

## 5.4.0-experimental (custom 5.x line)

New `KhaozEngine.Windowing` package — the shared windowing + input foundation — and Render2D integrates with it.

### KhaozEngine.Windowing (new)

- `AppWindow` — owns the SDL2/Metal window, Veldrid device + swapchain, and the frame loop. `Run(onFrame)`
  clears + presents around the callback; each `Frame` exposes `Dt`, an engine-native `InputState`, and the
  GPU command list to draw into. `Device`/`MainSwapchain` are the advanced GPU boundary (the only Veldrid in
  the API).
- `InputState` — per-frame keyboard + mouse snapshot: keys down/pressed/released, mouse position/delta,
  mouse buttons, scroll, window size; `IsDown`/`WasPressed` helpers over engine-native `Key`/`MouseButton`
  enums. No MonoGame. (Headless `InputStateTests`.) Gamepad/touch and the rich gesture/`InputManager` layer
  are follow-ups.
- **Render2D integration:** `Render2DSurface(AppWindow)` builds a `SpriteBatch` + texture/font loaders on the
  window's device, so a consumer draws a 2D scene into `AppWindow` frames (see `WindowingSample`). Render2D
  now references Windowing. `Render2DCore` gained an `ownsDevice` flag so a borrowed (window-owned) device
  isn't double-disposed.

Follow-up noted: Render2D still ships its own standalone `Key`/`FrameInfo`/`Render2DHost`; these will fold
into the Windowing path so there's one window/input layer (the `WindowingSample` aliases around the overlap).

## 5.3.1-experimental (custom 5.x line)

- **Fix (`KhaozEngine.Audio`):** `AudioSystem`'s default constructor no longer throws when no OpenAL
  implementation / audio device is available (headless CI, servers, machines without sound) - it falls back
  to a silent `NullMusicBackend` and logs a warning. A real device still gets the OpenAL backend. (This was
  red on the Linux CI runner, which has no OpenAL.)

## 5.3.0-experimental (custom 5.x line)

`KhaozEngine.Audio` **graduates from the 4.x MonoGame line to the 5.x custom stack** (the first existing
package to graduate; the 4.x MonoGame Audio is frozen at its last 4.x version, still pinnable by current
consumers). `Render3D` and `Render2D` roll to 5.3.0 with no functional change.

### KhaozEngine.Audio (now MonoGame-free)

- Backend swapped to a cross-platform **OpenAL streaming backend** (`OpenAlMusicBackend`, Silk.NET.OpenAL):
  decodes **WAV / OGG (NVorbis) / MP3 (NLayer)** and streams via queued buffers, pumped from
  `AudioSystem.Update()`. The MonoGame and macOS-AVAudioPlayer backends are removed; no `Microsoft.Xna`
  reference remains.
- **Breaking API (intended for the 5.x graduation):** `IMusicBackend.TryLoadTrack` drops its
  `ContentManager` parameter (`TryLoadTrack(contentDirectory, trackName)`) and gains `Update()`;
  `AudioSystem.LoadContent(ContentManager)` becomes `LoadContent(string contentDirectory)` (the folder
  holding the audio files; track names are file names without extension). The rest of `AudioSystem`
  (volume, enable, rotation, `PlayMode`, `TrackChanged`) is unchanged.
- `AudioSystem` logic stays covered by the headless `AudioSystemTests` (fake backend); real OpenAL
  streaming is eyeball/spike-verified (can't run on the CI audio-less runner). Needs an OpenAL impl at
  runtime (macOS ships one; bundle openal-soft for production). Music-only; SFX is a future layer.

## 5.2.0-experimental (custom 5.x line)

New `KhaozEngine.Render2D` package, and the 5.x line becomes a **shared** version (was per-package for the
first two Render3D releases).

### KhaozEngine.Render2D (new)

- New package: 2D rendering on the custom MonoGame-free foundation (Veldrid + SPIR-V, `System.Numerics`).
  `SpriteBatch` (batched textured quads, alpha blend + tint, per-texture batching; quads transformed to clip
  space on the CPU so there is no per-batch uniform), `Camera2D` (position/zoom/rotation, headless +
  unit-tested), `Texture2D` (PNG load via `StbImageSharp`), `SpriteFont` (runtime TrueType text - glyph atlas
  via `stb_truetype` - with `DrawString`/`Measure`). `Render2DHost` owns the SDL2/Metal window + frame loop +
  input; `Render2DSnapshot` captures headless. Veldrid stays internal; deps
  (Veldrid/Veldrid.SPIRV/StbTrueTypeSharp/StbImageSharp) confined to the package. Proves 2D + text on the
  custom stack (de-risks the 2D game migration). Metal-only for now.

### 5.x line now shared

- The 5.x custom-stack packages now share `Directory.Build.props` `<KhaozEngine5xVersion>` (both
  `Render3D` and `Render2D` reference it). They release together under one `vX.Y.Z-experimental` tag, ending
  the per-package tag collisions. `Render3D` rolls `5.1.0 -> 5.2.0-experimental` with no functional change.

## KhaozEngine.Render3D 5.1.0-experimental

Polish pass on the experimental renderer (additive; default look unchanged).

- **Procedural starfield** behind the model (`PixelPostProcessSettings.Starfield`, default on), composited in
  the final pass. Background is flagged by the color target's alpha (model writes alpha 1, the clear sets 0)
  and preserved through the palette/edge passes - keeps the blit to a safe binding count (a depth texture in
  the blit tripped a Veldrid/Metal multi-resource binding bug).
- **Second test model**: a lumpy low-poly `asteroid.glb` (noise-perturbed icosphere) alongside the planet,
  proving the loader handles arbitrary glTF geometry. The sample switches models (Space), zooms (W/S), and
  toggles the starfield (A).
- **`Newtonsoft.Json` pinned to 13.0.3** in the package to override the vulnerable transitive 9.0.1 from
  `Veldrid.SPIRV` (clears NU1903).
- **Sample runs without env vars**: `Render3DSample` auto-copies the system SDL2 (Homebrew) into its output as
  `libsdl2.dylib`, so `dotnet run --project Render3DSample` works without `DYLD_FALLBACK_LIBRARY_PATH`.

## KhaozEngine.Render3D 5.0.0-experimental (new, independent 5.x line)

First package of the post-MonoGame custom engine (see `docs/ROADMAP.md`). EXPERIMENTAL. Versions
independently of the shared 4.x line via its own csproj `<Version>`; ships nothing that changes existing
packages. Proven on Apple Silicon (Metal) at net10.0.

### KhaozEngine.Render3D (new)

- New package: real-time stylized 3D on a **custom MonoGame-free renderer** - `Veldrid` (GPU) +
  `Veldrid.SPIRV` (GLSL -> SPIR-V -> MSL/HLSL/GLSL at load, compiled natively, no Wine) + `SharpGLTF`
  (runtime glTF load). Math is `System.Numerics`. All three deps are confined to this package; no
  `Microsoft.Xna.Framework` reference.
- `IsoCamera3D` / `IIsoCamera3D`: orthographic isometric camera (no perspective). Configurable
  `Azimuth` (default 45 deg), `Elevation` (default `atan(0.5)` ~= 26.57 deg, the 2:1 iso look), `Target`,
  `OrthoSize`, `Zoom`, near/far. Exposes `View` / `Projection` / `ViewProjection` (`Matrix4x4`). Headless,
  unit-tested.
- `GltfLoader` / `GltfMesh`: load a `.glb`/`.gltf` at runtime (SharpGLTF) into a welded-normal mesh.
- `Scene3D`: a camera + one model + a `PixelPostProcessSettings`. Renders the lit model into an internal
  render target, runs the post chain, presents. `Render3DHost` owns the SDL2/Metal window + frame loop +
  engine-native input (`Key`/`FrameInfo`); Veldrid stays fully internal. `Render3DSnapshot` captures a
  scene offscreen to a CPU RGBA buffer (headless, for tooling/tests).
- Directional "sun" lighting with smooth diffuse or **cel** shading. `PixelPostProcess` chain, every stage
  independently toggleable: palette quantization (swappable `Palette`/`Palettes`), 4x4 Bayer dither,
  depth/normal-edge silhouette outline, point-or-linear upscale, configurable internal resolution and
  background. Default settings target a smooth, stylized space look; flipping the toggles gives the
  chunky retro/pixel look.
- Known limitations (POC): Metal backend only (clip-Y flip + MRT-clear handling are Metal-specific, gated
  for a future per-backend pass); `Render3DHost` needs SDL2 on the loader path (`brew install sdl2`);
  `Veldrid.SPIRV` pulls a transitive `Newtonsoft.Json` flagged `NU1903` (build-time). GPU rendering is
  eyeball-verified (the sample / `Render3DSnapshot`); only the camera math has CI unit tests.

## Tools

Repo utilities under `tools/`. Not packages: never versioned, packed, or tagged.

### PixelLabSheetAssembler

- New offline tool (`tools/PixelLabSheetAssembler`, `IsPackable=false`). Assembles a PixelLab
  character export (zip or dir) plus an animation name into one `Direction8` grid sheet PNG for
  `PixelLabSpriteLoader.FromGridSheet`: 8 rows in `Direction8` order, N frame columns, uniform cell
  size, feet-on-baseline anchoring (opaque-bbox bottom), and hold-previous (or hold-next for a
  leading gap) missing-frame tolerance with warnings. Prints the `frameCount` and suggested `fps`.
  Uses SixLabors.ImageSharp 2.1.13 (Apache-2.0); no MonoGame/GraphicsDevice. See its README.

## KhaozEngine 4.9.0

Additive. New zero-dependency package; no source change for existing consumers. The `4.8.0` move put
the channel-split contract in `KhaozEngine.Netcode`, but that package depends on
`MonoGame.Framework.DesktopGL` (its `UnitAxisQuantizer`/`IPredictedState` use `Vector2`/`MathHelper`),
so a MonoGame-free, web-server-shared DTO project still could not implement the contract without
dragging MonoGame + native SDL in. This release extracts the contract into a package with no
dependencies at all.

### KhaozEngine.Netcode.Abstractions (new)

- New package, **zero NuGet dependencies** (BCL only: no MonoGame, no LiteNetLib, no UDP transport).
  `IChannelSplittable<TSelf>` and the `NetChannelReliability` enum now physically live here. A batch
  DTO in a MonoGame-free, transport-agnostic project (e.g. a contracts assembly referenced by an
  ASP.NET leaderboard server) references **only** this package to implement the contract.
- **Namespace stays `KhaozEngine.Netcode`** (assembly name `KhaozEngine.Netcode.Abstractions` differs
  deliberately), so no consumer needs a `using` change.

### KhaozEngine.Netcode (changed, non-breaking)

- Takes a package dependency on `KhaozEngine.Netcode.Abstractions` and adds assembly-level
  `[TypeForwardedTo(typeof(IChannelSplittable<>))]` + `[TypeForwardedTo(typeof(NetChannelReliability))]`.
  Type-forwards **work here**: the full type name is unchanged and only the assembly moved (unlike the
  4.8.0 namespace move, which forwards could not bridge). Anyone referencing `KhaozEngine.Netcode`
  keeps compiling and binding both types with no change.
- `KhaozEngine.Netcode.LiteNetLib`'s `ChannelSplitter` references the contract transitively; its
  `Send<T>`/`ToDeliveryMethod` still use `LiteNetLib.DeliveryMethod` and stay put.

Guards: a new test project `KhaozEngine.Netcode.Abstractions.DecouplingTests` references **only**
`KhaozEngine.Netcode.Abstractions` and implements the contract on a dummy struct (compiling proves the
contract needs no MonoGame and no transport; a reflection test asserts the declaring assembly
references neither `MonoGame.Framework` nor `LiteNetLib`). The existing
`KhaozEngine.Netcode.DecouplingTests` stays green and now also asserts the types resolve through the
type-forwards to the Abstractions assembly. No shipped consumer references these types yet (SpaceGame
is the intended first adopter via `EntityUpdateBatchDto`).

## KhaozEngine 4.8.0

Breaking change shipped as a minor bump: the `5.x` line is reserved for the experimental branch, so
this breaking namespace move ships as `4.8.0` rather than `5.0.0`. Pin deliberately if you implement
the moved contract.

- **`IChannelSplittable<TSelf>` and the `NetChannelReliability` enum moved from
  `KhaozEngine.Netcode.LiteNetLib` to `KhaozEngine.Netcode`** (namespace
  `KhaozEngine.Netcode.LiteNetLib` -> `KhaozEngine.Netcode`). Both are pure: the interface is just
  the `Has*/Extract*` members, the enum is two values, and neither names a LiteNetLib type. Moving
  them lets a batch DTO that lives in a transport-agnostic project (e.g. one shared with a web
  server) implement the split contract without pulling a UDP transport into that project.
- **`ChannelSplitter` stays in `KhaozEngine.Netcode.LiteNetLib`** (its `Send<T>` orchestration and
  `ToDeliveryMethod` genuinely use `LiteNetLib.DeliveryMethod`). `KhaozEngine.Netcode.LiteNetLib`
  now has a package dependency on `KhaozEngine.Netcode` for the moved types.
- **`KhaozEngine.Netcode` still has no LiteNetLib dependency** (only MonoGame). A dedicated test
  project (`KhaozEngine.Netcode.DecouplingTests`) references only the core package and implements
  `IChannelSplittable<T>` on a dummy struct; it compiling is the standing guard that the contract
  stays transport-free.

No type-forwards: `[TypeForwardedTo]` redirects the *assembly* for an unchanged full type name, so
it cannot bridge a *namespace* change. No shipped consumer references these types yet (all consumers
on 4.0.0; netcode unadopted), so nothing breaks in practice. Migration for any code that used them
is a one-line `using` swap:

```csharp
// before
using KhaozEngine.Netcode.LiteNetLib;   // IChannelSplittable<T>, NetChannelReliability, ChannelSplitter
// after
using KhaozEngine.Netcode;              // IChannelSplittable<T>, NetChannelReliability
using KhaozEngine.Netcode.LiteNetLib;   // ChannelSplitter (keep only if you call Send/ToDeliveryMethod)
```

## KhaozEngine 4.7.0

Additive. Two new packages extracting SpaceGame's reusable netcode. No change to existing packages.

### KhaozEngine.Netcode (new)

- New package: game-agnostic, transport-free netcode primitives (refs MonoGame for `Vector2`/`MathHelper`).
- `UnitAxisQuantizer`: 8-bit quantization of a unit-range `[-1,1]` axis to a signed byte and back
  (`Quantize` clamps then rounds `*127` away-from-zero; `Dequantize` is `v/127f`). The game keeps its
  own command record + packed field layout. Determinism: this rounding is sim-hash-relevant for any game
  that dequantizes commands before its host-authoritative deterministic sim, so the scheme is fixed.
- `ClientPrediction<TState,TCommand>`: client-side prediction + authoritative reconciliation. Seq-keyed
  pending-command buffer with oldest-drop bound, ack-prune, rebase to an authoritative basis + replay of
  unacknowledged commands, and decaying render-offset error smoothing with hard-snap and dead-zone. Game
  supplies `IPredictedState<TSelf>` (Position + WithPosition) and `ITickSimulator<TState,TCommand>`
  (one deterministic step); tunables via `PredictionSettings` (`PredictionSettings.Default` = 60 Hz,
  256-command buffer, 100u snap, rate 8, 1.5u dead-zone). Returns `ReconciliationResult`. State type is
  `struct`-constrained.
- `RemoteCommandQueue<TCommand>`: host-side per-slot, seq-ordered command queue. Dedups duplicate
  `(slot,seq)` and negative seqs, returns a caller-supplied neutral command for an empty slot, tracks
  the last-acknowledged seq per slot. Determinism-neutral (orders/dedups only).

### KhaozEngine.Netcode.LiteNetLib (new)

- New package: LiteNetLib channel-split kernel (refs `LiteNetLib 2.1.2`).
- `IChannelSplittable<TSelf>` + `ChannelSplitter.Send`: split a batch into its unreliable
  (position/transient, latest-wins) and reliable (spawns/destroys/events) parts and send each non-empty
  part on its own channel (Sequenced vs ReliableOrdered) so reliable events never head-of-line-block
  position updates. `NetChannelReliability` + `ChannelSplitter.ToDeliveryMethod` expose the mapping. The
  game keeps its own batch DTO and field layout.

## KhaozEngine 4.6.0

Additive. New package `KhaozEngine.Updates`. No change to existing packages.

### KhaozEngine.Updates (new)

- New package centralizing a game-agnostic **delta auto-update pipeline** (promoted from SpaceGame so
  Hardpoint/Nullwake can reuse it). Determinism-neutral (never touches sim/RNG). Pure .NET
  (+ `KhaozEngine.Diagnostics`), no MonoGame dependency.
- `UpdateManifest` - SHA256 file manifest (`path`/`sha256`/`size`, ordinal-sorted, stable camelCase
  JSON wire format). `GenerateFromDirectory(dir, version, platform)` builds one from an install dir
  (also usable by an offline publish-side manifest generator); `ComputeDiff(local, remote)` returns
  `FilesToDownload` + `FilesToDelete` + `TotalDownloadBytes`.
- `IUpdateSource` - host-agnostic transport. `HttpUpdateSource` is the default (HTTP against a
  configurable `ServerBaseUrl` + `LatestVersionPath` template; files resolved as siblings of the
  manifest - SpaceGame's Azure Blob layout, but a game points it elsewhere via config or implements
  the interface for any backend). `LatestVersionInfo` carries version/build/manifest-url/required.
- `UpdateService` - the check -> download -> apply state machine (`UpdateState`), with resumable
  staging (already-staged files with a matching SHA256 are skipped; corrupt downloads retry up to
  `MaxDownloadRetries`), boot hygiene (stale-staging cleanup, interrupted-apply detection), and
  offline-safe checks (failures fall back to `Idle`). Shim launch and process exit are injectable via
  `UpdateServiceOptions`, so the whole lifecycle is headless-testable. `Platform`/`InstallDir` default
  to the current OS runtime id / `AppContext.BaseDirectory`.
- `UpdateApplier` + `IUpdaterEnvironment` - the cross-platform **staged-apply core** for an external
  updater shim: wait for the game to exit, back up each install file before overwriting, copy with
  retries for locked files, roll every overwrite back on any failure (install never left half-new),
  abort before touching the install if a staged source is missing, delete removed files, install the
  new manifest, clear the macOS quarantine attribute, relaunch. All side effects go through
  `IUpdaterEnvironment` (`SystemUpdaterEnvironment` is the real impl); a game's shim is just
  `UpdateApplier.Run(args, new SystemUpdaterEnvironment(log))`.
- `ApplyUpdateConfig` is the `apply-update.json` handoff contract; it (de)serializes through a
  source-generated `UpdatesJsonContext`, so the shim needs no reflection and stays trim/AOT safe.
- 46 headless tests (manifest diffing, resume skip/retry, download verification, apply / rollback /
  abort).

## KhaozEngine 4.5.0

Additive. Two new packages of game-agnostic 2D primitives, ported verbatim from SpaceGame.

### KhaozEngine.Collision (new package)

- New package: deterministic 2D collision + broadphase primitives. Refs `MonoGame.Framework.DesktopGL`
  for `Vector2`. Float math and iteration order are bit-identical to the SpaceGame originals
  (`CircleCollision`, `EnemySpatialIndex`) so it can be adopted in a lockstep sim without moving the hash.
- `CircleCollision` (static): `Intersects(Vector2, float, Vector2, float)` and `Intersects(ICircleCollider,
  ICircleCollider)` broad overlap (`DistanceSquared <= combined^2`, touching counts), plus three
  `DoCollidersCollide` overloads (collider/collider, bare-circle/collider, collider/bare-circle) that apply
  per-pixel precise refinement when a side implements `IPreciseCircleCollisionTarget`.
- `ICircleCollider` (`Position`, `Radius`) and `IPreciseCircleCollisionTarget` (`IntersectsCircle`).
- `SpatialHashGrid`: uniform spatial hash for broadphase. Generic rebuild via `BeginRebuild(capacity)` +
  `Add(index, position, radius)` per item (replaces the snapshot-coupled `Rebuild`), then
  `QueryCandidates(center, radius)` / `GetQueryIndex(i)` / `SortQueryIndicesAscending(count)`. Cell coord =
  `(int)MathF.Floor(world / cellSize)`, queries walk Y-outer/X-inner, cell chains are LIFO (head insertion).
  Renamed off "Enemy"; stores caller-supplied indices into whatever collection the caller owns.

### KhaozEngine.Pooling (new package)

- New package: `ObjectPool<T>` where `T : class, IPoolable`, a fixed-capacity free-list pool genericized
  from SpaceGame's `XpFlyerPool` (XpFlyer specialization + `Update`/`Draw` dropped). Zero dependencies.
- O(1) `Rent()` (null when exhausted) / `Return(item)` (resets, ignores foreign items), `Clear()`,
  `ActiveCount`/`FreeCount`, and `GetActive(slot)` over a swap-removal-compacted active set. `IPoolable`
  exposes `PoolIndex` (pool-owned) + `Reset()`.

## KhaozEngine 4.4.0

Additive. New package `KhaozEngine.Platform` for native platform interop. No change to existing packages.

### KhaozEngine.Platform (new)

- New package: game-agnostic native platform interop, pure BCL P/Invoke, no MonoGame dependency.
- `Clipboard`: cross-platform system-clipboard facade. `TryGetClipboardText()` / `TrySetClipboardText(string)`
  dispatch SDL2 first, then a macOS `NSPasteboard` fallback, then an optional Android/iOS bridge.
  `TrySetClipboardImagePng(byte[])` covers macOS + mobile; `TrySetClipboardImageRgba32(w, h, rgba)` writes a
  bottom-up `CF_DIB` on Windows. Every call is best-effort and never throws (a missing/failing backend
  yields `""` / `false`).
- `Clipboard.MobileBridgeTypeName`: fully-qualified type name of the consumer's mobile clipboard bridge,
  resolved by reflection across loaded assemblies (static `TryGetClipboardText(out string)` /
  `TrySetClipboardText(string)` / `TrySetClipboardImagePng(byte[])`). Defaults to `null` (mobile fallback
  skipped); reassigning clears the resolution cache. This replaces the hard-coded bridge type name in the
  promoted-from source, so consumers register their own bridge.
- Ported verbatim from SpaceGame's `ClipboardInterop` (the SDL2 / Windows GDI / macOS Objective-C / mobile
  marshaling is unchanged); the dispatch/fallback ordering and the `CF_DIB` packing are extracted into pure
  helpers and covered by headless tests. The native bridges themselves can't run headless.

## KhaozEngine 4.3.1

Bugfix. No API change.

### KhaozEngine.Audio

- `MacOsMusicBackend.TryLoadTrack` now locates the built track file by probing the formats the
  content pipeline actually emits (`.ogg`, `.mp3`, `.m4a`, `.wav`, `.aiff`, `.caf`), preferring
  `.ogg`. It previously looked only for a raw `.mp3` on disk, but the DesktopGL pipeline transcodes
  music to `.ogg` (the `.xnb` is just a header that references it), so every track failed to load and
  no music played. AVAudioPlayer decodes the built `.ogg` directly.
- The native AVAudioPlayer bridge is now created lazily on first playback instead of in the
  constructor, so track loading is headless-testable on non-macOS CI.

## KhaozEngine 4.3.0

Additive. Completes the isometric toolkit's picking + extensibility seams from 4.2.0. No behaviour
change for existing 4.2.0 calls.

### KhaozEngine.Graphics

- `IsometricProjection.ScreenToWorld(screen, z)`: inverts the projection on the horizontal plane at
  height `z` (not just the ground). `ScreenToWorld(screen, 0)` equals `ScreenToGround`. This is the
  building block for picking over varying terrain - a consumer that owns the heightmap tests candidate
  heights front-to-back; the toolkit supplies the per-plane inverse.
- `IIsometricProjection` interface, implemented by `IsometricProjection`. Consumers can depend on the
  seam and substitute a fake/stub projection in headless tests (mirrors `Input.IDesignViewport`).
- `IsoDepth.DepthKey` gains an optional `zWeight` (default 1): scales how strongly height pushes a
  drawable toward the front, so a tall stack can be made to sort in front of a taller-but-nearer
  neighbour, or `zWeight: 0` drops height from ordering. Existing 4-argument calls are unchanged.

## KhaozEngine 4.2.0

Additive. A render-only isometric toolkit in `KhaozEngine.Graphics`, plus an opt-in footprint
anchor on the directional sprite draw path. No gameplay/grid/pathfinding concepts: consumers keep
their own world model and project at draw time. Orthographic consumers are unaffected (the only
signature change is a trailing optional parameter).

### KhaozEngine.Graphics

- `IsometricProjection`: configurable 2:1-style tile footprint (default 64x32) and `heightScale`
  (defaults to tile height). `WorldToScreen(wx, wy, z = 0)` maps world to screen
  (`sx = (wx - wy) * TileWidth/2`, `sy = (wx + wy) * TileHeight/2 - z * HeightScale`);
  `ScreenToGround(screen)` inverts on the ground plane (`z = 0`), returning a continuous world
  point for picking. `z` is a real input now (v1 callers pass 0) - the seam for terrain height.
- `IsoDepth.DepthKey(wx, wy, z = 0, layer = 0)` returns a comparable `IsoDepthKey` for Y-sorting a
  draw list: primary order `wx + wy + z`, integer `layer` as tiebreak. The consumer sorts its own list.
- `PrimitiveRenderer.DrawIsoDiamond` (filled 2:1 tile), `DrawIsoBlock` (top + two shaded side faces
  for a given height), `DrawIsoEllipse` (filled 2:1, for shadows) and `DrawIsoEllipseOutline`
  (stroked 2:1, for range rings). Match the existing pixel-quad rendering style.
- `ColorHelper.Scale(color, factor)`: per-channel RGB multiply (alpha kept), clamped - used for the
  default block face shading.

### KhaozEngine.Sprites

- `SpriteAnchor` enum and a new optional `anchor` parameter on `DirectionalAnimatedSprite.Draw`
  (default `Center`, unchanged). `FootprintBottomCenter` anchors the draw position at the frame's
  bottom-centre so a tall iso sprite stands on its (z-lifted) tile instead of being centred on it.
  An explicit `origin` still overrides the anchor. Facing/`Direction8` logic is unchanged.

## KhaozEngine 4.1.0

Additive. Logging normalization: packages that log now lean on the logger's category (already
rendered by `LogFormatter` as `[Category]`) instead of hand-rolled message prefixes, and fall back
to the ambient `Log` facade when no `ILogger` is injected. Two more packages gain logging where it
earns its keep. No public type removed; on-disk formats unchanged.

### KhaozEngine.Audio

- Log messages drop the redundant `Audio:` prefix across `AudioSystem` and the three backends. The
  category already identifies the source (`AudioSystem`, `MonoGameMusicBackend`, `MacOsMusicBackend`,
  `MacOsMusicPlayer`), so the prefix was doubling up. No behavior change beyond log text.

### KhaozEngine.Persistence

- `SaveEncoder`, `PersistenceQueue`, and `SettingsManager<T>` drop inline `[ClassName]` message
  prefixes and now resolve a logger via `?? Log.For<T>()` (the generic `SettingsManager<T>` uses the
  fixed category `SettingsManager` to avoid a `` `1 `` suffix). They log under their own category
  whether or not a logger is injected.
- `SaveEncoder`'s `logger` constructor argument is now **optional** (`ILogger? logger = null`); a null
  logger no longer throws, it falls back to the ambient facade. Callers passing a logger are
  unaffected.

### KhaozEngine.Content

- `ConfigLoader.Load<T>` now emits a Debug line naming the resolved source (disk path vs embedded
  resource) under category `ConfigLoader` - the usual "which config actually loaded" question. Adds a
  `KhaozEngine.Diagnostics` dependency. `JsonSchemaValidator` keeps its `TextWriter` reporter (it is a
  CLI tool surface, not runtime diagnostics).

### KhaozEngine.Localization

- `LocalizationManager.SetCulture` and `GetSupportedCultures` emit Debug lines (culture set, count of
  discovered cultures) under category `LocalizationManager`. Adds a `KhaozEngine.Diagnostics`
  dependency (still pure BCL, Diagnostics has no MonoGame dep).

Pure-compute packages (Ecs, Time, Sprites, UI, Graphics, Input, Serialization, Effects, App, Screens)
intentionally stay logless: no IO and no swallowed exceptions, so logging would be noise.

## KhaozEngine 4.0.0

Breaking. Inter-package tidy-up: a rendering primitive moves to the rendering package, and JSON
defaults are centralized in a new package. No runtime behavior change, but two namespaces moved and
`KhaozEngine.Effects` swaps a dependency, so consumers need `using` and possibly `<PackageReference>`
updates.

### KhaozEngine.Graphics

- `PrimitiveRenderer` and `ColorHelper` moved here from `KhaozEngine.UI` (namespace
  `KhaozEngine.UI` -> `KhaozEngine.Graphics`). They are low-level rendering helpers (1x1 pixel
  shapes, hex color parsing) with no UI concepts, so they belong in the rendering package that
  already sits below UI. **Migration:** add `using KhaozEngine.Graphics;` where you used
  `PrimitiveRenderer`/`ColorHelper`. `KhaozEngine.UI` consumers need no new package reference (UI
  already depends on Graphics); the types are just in a different namespace now.

### KhaozEngine.Effects

- Now depends on `KhaozEngine.Graphics` instead of `KhaozEngine.UI`. Its only use of UI was
  `PrimitiveRenderer`, which now lives in Graphics, so the package no longer drags in the whole UI
  widget set. **Migration:** if you reference `KhaozEngine.Effects` directly, no change; the
  transitive dependency just shifts from UI to Graphics.

### KhaozEngine.Serialization (new package)

- New leaf package holding `JsonDefaults`: shared `System.Text.Json` option baselines so config,
  persistence, and ECS serialize the same way. `TolerantRead` (case-insensitive, `//` comments,
  trailing commas), `IndentedWrite` (`WriteIndented`), and `IncludeFields` (round-trips public
  fields). Each is a single shared, effectively read-only instance. Pure BCL, no MonoGame.
- `KhaozEngine.Content` (`ConfigLoader`), `KhaozEngine.Persistence` (`AtomicJsonWriter`,
  `PersistenceQueue`, `FileSettingsStorage`), and `KhaozEngine.Ecs` (`WorldSerializer`) now consume
  `JsonDefaults` instead of each declaring their own options. Public APIs and on-disk format are
  unchanged; these packages gain a `KhaozEngine.Serialization` dependency.

## KhaozEngine 3.12.0

Additive. New keyed registry for directional sprites in `KhaozEngine.Sprites`.

### KhaozEngine.Sprites

- New `SpriteRegistry` - a keyed store of `DirectionalAnimatedSprite` with one bulk
  `Update(float deltaSeconds)` that advances every registered sprite's animation clock once per
  frame. `Add(key, sprite)` (non-empty key, no duplicates, non-null sprite), `Get(key)` returning
  the sprite or null, `Contains(key)`, and `Count`. Takes already-built sprites - loading by
  embedded-resource manifest name stays game-side, since resource names are game-specific.
  Centralizes the `Dictionary<string, DirectionalAnimatedSprite>` + per-frame bulk-advance that
  Hardpoint hand-rolls in `SpriteLibrary`.

## KhaozEngine 3.11.0

Additive seam so consumers stop wrapping `VirtualResolution` just to make screens headless-testable.

### KhaozEngine.Input

- New `IDesignViewport` interface: `int Width`, `int Height`, `float Scale`, `Matrix ScaleMatrix`.
  `VirtualResolution` now implements it (its existing properties already satisfy the contract - no
  behavior change). Screens that need only design-space size/scale/matrix can take an `IDesignViewport`
  and tests can hand them a fixed-size fake instead of standing up a `VirtualResolution`. Hardpoint's
  game-side `IViewport` + `VirtualResolutionViewport` adapter exist purely for this; they can drop the
  adapter and reference the engine interface directly.

## KhaozEngine 3.10.0

Shared camera-gesture core: `PannableCanvas` and `CameraController` now drive a `Camera2D` and share
one implementation of pan / zoom / pinch / clamp / tap. Additive API plus one scoped behavior change.

### KhaozEngine.Graphics

- `Camera2D.GetViewMatrix` now honors the viewport's X/Y offset (centers `Position` on
  `(viewport.X + W/2, viewport.Y + H/2)`). **Behavior change**, but only for a viewport with a non-zero
  X/Y origin (an inset sub-rectangle) - the previously unsupported/incorrect case. Whole-screen
  viewports (X = Y = 0, every prior call site) are unchanged. Makes inset viewports map correctly.
- New `Camera2D.PanByScreenDelta(screenDelta)` - grab-and-drag pan (`Position -= screenDelta / Zoom`).
- New `Camera2D.ZoomAboutScreenPoint(target, focusScreen, viewport, min, max)` - clamped zoom that keeps
  the world point under the focus fixed.
- New `PinchGestureTracker` - the shared two-finger pinch state machine (midpoint pan + zoom-about-focus).
- New `CameraGestures.TryGetTap(input, camera, viewport, out press, out release)` - the shared
  press-origin tap-vs-pan helper.
- `CameraController` now drives `Camera2D` through these shared pieces. No public API or behavior change.

### KhaozEngine.UI

- `PannableCanvas` delegates its transform / clamp / pan / tap math to a backing `Camera2D` (shared with
  `CameraController`). `CameraOffset` is preserved as the legacy additive view (`-Position * Zoom`).
  Drag pan, wheel-as-vertical-pan, scissor `Draw`, `BlockInput`, `Padding`, `ScrollPanSpeed`, and the
  press-origin tap invariant are byte-identical.
- New: real two-finger **pinch zoom** (the old `_zoom = 1f` seam is now live). New `MinZoom` / `MaxZoom`
  (defaults 0.1 / 10), `EnablePan` / `EnableZoom` (default true), and a `Camera` accessor. Wheel stays a
  vertical pan. Mouse-only behavior is unchanged. Disable pinch with `EnableZoom = false`; `EnablePan =
  false` disables all panning (drag, two-finger, and wheel).
- `Focus(rect)` now **fits zoom to the rect** (delegates to `Camera2D.Focus`, clamped to `MinZoom`/
  `MaxZoom`), fulfilling its long-standing "becomes fit-to-rect once zoom exists" intent - it previously
  only centered. Optional `paddingFraction` parameter. Use `CenterOn`/`CenterContent` for a center-only move.
- `KhaozEngine.UI` now references `KhaozEngine.Graphics` (transitive package dependency added).

## KhaozEngine 3.9.0

Camera framing + follow, both in `KhaozEngine.Graphics`. Additive, no breaking changes.

### Camera2D framing helpers: CenterOn + Focus (fit-to-rect zoom)

`Camera2D` gains the framing math that consumers were hand-rolling (Hardpoint's `BoardFraming`,
SpaceForge's grid framing, `PannableCanvas`'s long-dormant `Focus(rect)` zoom seam):

- `CenterOn(Vector2 world)` - sets `Position` so the world point is at the viewport center (an explicit
  alias for API parity).
- `Focus(Rectangle worldRect, Viewport viewport, float paddingFraction = 0f, float minZoom, float maxZoom)`
  - fit-to-rect: sets `Zoom` so the rect (optionally inflated by `paddingFraction` on each side) is fully
  visible (contain fit, `min(viewport.Width / rectW, viewport.Height / rectH)`), clamped to
  `minZoom`/`maxZoom`, then centers `Position` on the rect. Pure and headless. Does not clamp to world
  bounds - call `ClampPosition` after if the rect is a sub-region. A no-arg-viewport overload uses the
  stored `Viewport` property.

Because these live on `Camera2D`, both `CameraController` and (once consolidated) `PannableCanvas`
inherit them.

### CameraFollow (target-follow with smoothing + deadzone)

New `CameraFollow` drives a `Camera2D` to follow a moving target. The game decides what to follow; this
owns only the smoothing/deadzone/clamp. Kept separate from the gesture `CameraController` - a screen
typically uses one or the other.

- `Update(Vector2 target, float dt, Viewport viewport, Rectangle worldBounds)` - eases toward the target,
  then clamps via `Camera2D.ClampPosition`. Headless (explicit `Viewport`).
- **Frame-rate-independent smoothing**: per-frame catch-up is `1 - exp(-Stiffness * dt)`, so the result
  is independent of step size / frame rate. `Stiffness <= 0` snaps instantly.
- **Optional deadzone**: a screen-space (virtual) `Rectangle` the target may move within before the camera
  chases; once the target crosses an edge the camera moves just enough to put it back on that edge.
  `Rectangle.Empty` (default) disables it (camera centers on the target).

Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var follow = new CameraFollow(camera) { Stiffness = 8f, Deadzone = new Rectangle(360, 240, 200, 120) };
    // per frame:
    follow.Update(playerWorldPos, dt, GraphicsDevice.Viewport, levelBounds);
    // or frame a region instead of following:
    camera.Focus(levelBounds, GraphicsDevice.Viewport, paddingFraction: 0.05f, minZoom: 0.5f, maxZoom: 3f);

## KhaozEngine 3.8.0

New package `KhaozEngine.Sprites`: 2D sprite + directional-animation playback. Additive, no breaking
changes. Replaces flat-primitive entity rendering with directional, animated sprites for all games.

### KhaozEngine.Sprites (new)

- **`Direction8`** - the 8 facings `S, SE, E, NE, N, NW, W, SW`, ordered so the enum value is the
  direction's row index in a PixelLab grid sheet. `Direction8Extensions.FromVector(facing, fallback)`
  maps a movement/aim vector to the nearest of 8 in y-down screen space (+X east, +Y south); magnitude
  is irrelevant, a 22.5-degree seam rounds to the higher (clockwise) direction, and a zero vector
  returns `fallback`. `ToVector()` returns the unit facing.
- **`SpriteSheetLayout`** - pure grid math (no `Texture2D`, headless): `FromFrameSize` / `FromGrid`,
  then `GetFrame(row, column)` -> source `Rectangle`. **`SpriteSheet`** pairs it with a texture.
- **`SpriteFrame`** - a `(Texture2D, Rectangle)` drawable frame; frames carry their own texture so an
  animation can span one packed sheet or a set of loose per-frame textures.
- **`SpriteAnimation`** - ordered frames + per-frame duration + loop flag (`FromFps` or seconds ctor).
  **`SpriteAnimationPlayer`** advances it by a `float` seconds delta or a `GameTime`, yields the current
  frame, loops, flags `IsFinished` for one-shots, and `Play(anim, preservePhase)` swaps animations. A
  small relative tolerance on the frame boundary keeps exact-multiple deltas from dropping a frame to
  float noise.
- **`DirectionalAnimatedSprite`** - one animation per `Direction8`, plays the one matching the current
  facing, draws via `SpriteBatch` with a centered origin by default; switching facing preserves the
  animation phase so a walk cycle stays smooth. `Update(facing, gameTime)` does both in one call.
- **`PixelLabSpriteLoader`** - builds a `DirectionalAnimatedSprite` from a PixelLab export, either an
  assembled grid sheet (`FromGridSheet`: 8 direction rows x N frame columns) or loose per-direction
  frame textures (`FromFrames`). PixelLab's row order is isolated here (in `RowFor`) so the core types
  stay PixelLab-agnostic. Note: PixelLab exports loose per-frame PNGs, not a canonical sheet, so the
  grid layout matches an assembly step's output; verify row order against a real export on first use.

The animation clock decouples from `KhaozEngine.Time` deliberately (advances on a `float` delta), so
callers feed either `GameTime.ElapsedGameTime` or a scaled `GameClock.ScaledDeltaSeconds`.

## KhaozEngine 3.7.0

Two additive camera/viewport features. No breaking changes.

### KhaozEngine.Graphics: CameraController (pan/zoom/pinch gesture controller)

New `CameraController` drives an existing `Camera2D` from an `InputManager`, so gameplay can pan
and zoom an arbitrary world render without re-implementing the gesture math. It owns no matrix math
of its own: it reuses `Camera2D.ScreenToWorld` and `Camera2D.ClampPosition`.

- **Pan**: single-pointer drag and two-finger drag (by pinch midpoint travel). Grab-and-drag, so
  world content tracks the finger; the screen delta is divided by `Zoom` to a world delta.
- **Zoom**: scroll wheel (desktop) and pinch (mobile), clamped to `MinZoom`/`MaxZoom`. Zoom is about
  the cursor / pinch midpoint - the focal world point stays under the pointer. `WheelZoomStep` is the
  multiplicative factor per 120-unit notch (fractional/multi-notch deltas scale smoothly via a power).
- **Bounds clamp**: after pan/zoom, clamps via `Camera2D.ClampPosition(Position, worldBounds, viewport)`
  so the view stays inside a caller-supplied world rectangle (auto-centers when the world is smaller).
- **Tap vs pan**: `TryGetTap(out pressWorld, out releaseWorld)` mirrors `PannableCanvas.TryGetTap` and
  honors the press-origin invariant - gameplay places a tower on a tap but treats a drag as a pan
  (a pan returns true too, but its press/release world points differ, so a same-target check rejects it).
- **Headless**: `Update(Viewport, Rectangle worldBounds)` takes an explicit `Viewport` like `Camera2D`,
  so the step is unit-testable with no `GraphicsDevice`. Toggles: `EnablePan`, `EnableZoom`, `BlockInput`.

`KhaozEngine.Graphics` now references `KhaozEngine.Input` (for `InputManager`). Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var controller = new CameraController(input, camera) { MinZoom = 0.5f, MaxZoom = 4f };
    // per frame, after input.Update(...):
    controller.Update(GraphicsDevice.Viewport, worldBounds);
    if (controller.TryGetTap(out var pressWorld, out var releaseWorld)) { /* place on tap */ }
    spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());

Relationship to `PannableCanvas` (KhaozEngine.UI): both now carry pan/zoom gesture logic, but on
different coordinate conventions (`PannableCanvas` uses an additive offset and an inset sub-rectangle
viewport with scissor clipping; `CameraController` uses `Camera2D`'s position/zoom matrix). This
release ships `CameraController` standalone and leaves `PannableCanvas` as-is to avoid regressing the
games already on it (Hardpoint's map). Consolidating `PannableCanvas` onto `CameraController` is a
tracked follow-up; the two are not meant to diverge long-term.

### KhaozEngine.Input: opt-in desktop design-scale for VirtualResolution

`VirtualResolution` now offers a design-scaled mode on desktop, mirroring mobile: a fixed
`BaseWidth` × `ReferenceHeight` design space scaled to fill the window, so desktop UI presents the
same fixed design space (and scales up on a large/Retina window) instead of sizing in raw
back-buffer pixels.

- **Opt-in, non-breaking**: the desktop default (`isMobile:false` → scale 1, identity matrix, virtual
  size = back-buffer) is unchanged. Opt in with the new `VirtualResolution.DesignScaled(gdm, baseWidth,
  referenceHeight)` factory (still pass `isMobile:false` to the `InputManager`; only the scaling differs).
- **Fill policy**: fill-the-width, adaptive-height (the same as mobile) - no letterbox bars and no
  offset, so `ScreenToVirtual` stays a plain divide-by-`Scale` and `InputManager` hit-testing lines up.
- The `GraphicsDeviceManager` ctor argument is now nullable, and a new `Configure(int screenWidth,
  int screenHeight)` computes the scaling from an explicit size (`Initialize` delegates to it). This
  makes the scaling headless-testable and lets a consumer drive it from a known/fixed size.

Wiring a desktop game into design-scale:

    var vr = VirtualResolution.DesignScaled(graphicsDeviceManager, baseWidth: 932, referenceHeight: 430);
    vr.Initialize();                                  // and again on Window.ClientSizeChanged
    var input = new InputManager(isMobile: false, transform: vr);

## KhaozEngine 3.6.0

### KhaozEngine.Ecs: CachedQuery (per-tick allocation-free query reuse)

New `CachedQuery` lets sim hot paths reuse a single `Query` instead of allocating a fresh one
every tick. `World.Query()` returns `new Query(this)` per call, so calling it inside a per-tick
loop violates the consumers' "no per-frame allocation in sim hot paths" rule.

- `CachedQuery(Func<World, Query> build)` captures the filter builder once.
- `Query For(World world)` returns the reused `Query`, rebuilding it only when the `World`
  instance changes (`ReferenceEquals` check) - for consumers that recreate the `World` on
  run-reset. The underlying `Query` still self-refreshes its matched-archetype list on
  `ArchetypeGen` changes, so newly spawned archetypes are picked up through the cache.

Additive, no breaking changes. Usage:

    private readonly CachedQuery _projectiles = new(w => w.Query().With<ProjectileTag>());
    // per tick:
    _projectiles.For(world).ForEach((Entity e, ref Position p) => ...);

## KhaozEngine 3.5.0

### KhaozEngine.Graphics: DisplayManager (display/window configuration)

New `DisplayManager` centralizes MonoGame `GraphicsDeviceManager` + `GameWindow` setup so games
stop configuring the device bespoke.

- `DisplaySettings` (immutable record): `Width`/`Height`, `Mode` (`WindowMode.Windowed` /
  `BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
  floor, `SupportedOrientations`, `Title`. Factories `DisplaySettings.Landscape(w, h)` and
  `Portrait(w, h)`. Pure and headless-testable; build variants with `with`.
- `DevicePresets` catalog of common iOS logical-point sizes (iPhone SE to 15 Pro Max, iPad to
  Pro 12.9") via `DevicePreset.Portrait()` / `.Landscape()`.
- `DisplayManager(graphics, window, settings)` applies settings to the live device and exposes
  runtime mutators `Apply`, `SetResolution`, `SetMode`, `ToggleFullscreen`, `SetResizable`, plus
  `Width`/`Height`/`Size`/`IsFullscreen`. Enforces the min-size floor by clamping on
  `ClientSizeChanged`. Composes with `VirtualResolution`, which still reads the device for scaling.

One-liner for an iPhone 15 Pro Max landscape window (932x430):

    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));

## KhaozEngine 3.4.1

Bug fix for the 3.4.0 now-playing feature. No API or behaviour change for callers whose tracks all load.

- **KhaozEngine.Audio** - `AudioSystem.LoadContent` now drops any track that fails to load from its
  internal name list, keeping it aligned with the backend's compact track list. Previously a partial
  load failure left the names and the backend's indices misaligned, so `CurrentTrack` / `TrackChanged`
  reported the wrong song and `PlayTrack(name)` could resolve to the wrong index. The load log still
  reports `loaded/requested` against the originally requested count.

## KhaozEngine 3.4.0

Additive feature pass unblocking SpaceGame/Nullwake adoption, plus review-nit fixes. No breaking changes.

- **KhaozEngine.Persistence** - `SettingsManager<T>` gains an optional `sanitizeOnLoad` constructor hook
  (`Func<T,T>`). It runs on every load, including the initial load inside the constructor (which the
  `SettingsLoaded` event can't reach), so callers can clamp fields / migrate a schema version on the
  first load. Null = passthrough; a throwing hook is swallowed/logged and the unsanitized value is used.
  The README documents the `[JsonExtensionData]` + version-field downgrade-safe migration pattern.
- **KhaozEngine.Audio** - `AudioSystem` now supports explicit and repeating playback alongside random
  rotation: `PlayTrack(int)` / `PlayTrack(string)` (an unknown name or out-of-range index is a logged
  no-op, not a throw), a settable `PlayMode { RandomRotation, RepeatOne }` (default `RandomRotation`),
  and now-playing state via `CurrentTrack` plus the `TrackChanged` event.
- **KhaozEngine.Audio** - a transient exception while reading `IMusicBackend.IsPlaying` in `Update()`
  now skips the frame (logged) and recovers, instead of permanently disabling audio. The availability
  latch is reserved for real play/load failures.
- **KhaozEngine.Ecs** - `DeterministicRng.Next(maxExclusive)` and `Next(min, max)` now throw
  `ArgumentOutOfRangeException` on non-positive / empty ranges (previously a DivideByZero or
  negative-modulo trap).
- Docs/tests: `docs/USING-KHAOZENGINE.md` gains a `KhaozEngine.Graphics` / `Camera2D` section; the
  Effects pool-recycle test now asserts the oldest particles are actually overwritten.

## KhaozEngine 3.3.0

Batch 2 of the "promote duplicated game code into KhaozEngine" effort: three new packages plus
additions to two existing ones. All additive; no consumer adopts these yet.

- **KhaozEngine.Audio** (new; MonoGame + Diagnostics): `AudioSystem` (track-registry music player,
  seed-via-ctor + additive idempotent `RegisterTrack`/`RegisterTracks` that work pre- and post-load)
  over a public `IMusicBackend`. Public `MonoGameMusicBackend` and `MacOsMusicBackend` (the macOS
  backend works around MonoGame's broken `Song` playback via an AVAudioPlayer P/Invoke shim). Logs
  through an injected `ILogger` (defaults to the engine `Log`).
- **KhaozEngine.Effects** (new; MonoGame + UI): pooled, data-driven particle system. A
  `ParticleEmitterConfig` record holds all tunables; `ParticlePresets.Spark`/`.Ember` reproduce the
  promoted Nullwake hit effects; `ParticleSystem.Emit(config, position, baseColor, count)` with a
  ring-buffer pool. First resident of a generic visual-effects package (room for screen shake, flashes, etc.).
- **KhaozEngine.Graphics** (new; MonoGame): `Camera2D` - a generic 2D matrix camera
  (position/zoom/rotation → view matrix), headless `WorldToScreen`/`ScreenToWorld` (explicit `Viewport`,
  no `GraphicsDevice`), turn-key no-arg overloads via a settable `Viewport`, and a pure
  `ClampPosition` world-bounds helper. The base for a future follow/deadzone/parallax camera layer.
- **KhaozEngine.Persistence** additions: `AtomicJsonWriter` (crash-safe temp-then-move writes),
  `PersistenceQueue` (`IPersistenceQueue`; per-path coalescing async writer, never throws into the
  game, retry + `WriteFailed` event, blocking `Flush()` + flush-on-dispose), and
  `SettingsManager<T>` / `ISettingsStorage` / `FileSettingsStorage` (typed settings persisted via the
  queue, default paths through `KhaozEngine.App.AppDataPaths`). Persistence now also references `KhaozEngine.App`.
- **KhaozEngine.Ecs** addition: `DeterministicRng.CreateDerived(string systemName)` - named, stable,
  reproducible substreams (mixes the parent seed with a fixed string hash; not `string.GetHashCode`).
  Note: derived streams do not byte-match `System.Random`, so any consumer migrating to it must re-baseline golden values.

## KhaozEngine 3.2.0

Batch 1 of the "promote duplicated game code into KhaozEngine" effort. Three new pure-.NET packages
(plus a small consolidation of the `AppDataPaths` that 3.1.0 had shipped). No consumer adopts these yet.

- **KhaozEngine.App** (new, pure .NET): app/runtime helpers.
  - `BuildMetadata.Read(string key, string fallback, params Assembly?[] assemblies)` - reads
    `AssemblyMetadataAttribute` values at runtime, probing the supplied assemblies in order (null
    entries skipped), so a game can surface its own version/build identity without re-deriving it.
  - `AppDataPaths` - instance resolver for the OS-correct per-app data directory (Windows `%APPDATA%`,
    macOS `~/Library/Application Support`, Linux `$XDG_DATA_HOME`/`~/.local/share`, with fallbacks).
    `BaseDirectory` is resolved + created once and cached (thread-safe via `Lazy<T>`); convenience
    `SaveFilePath`/`SettingsFilePath`/`LogFilePath`/`PreviousLogFilePath`/`GetFilePath`. OS resolution
    sits behind an internal seam for headless testing.
  - `ServiceLocator : IServiceProvider` - generic register/resolve-by-type service registry backed by a
    `ConcurrentDictionary` (`Register`/`Replace`/`Get`/`TryGet`/`Has`/`GetService`). Fits
    `ScreenManager.Services`.
- **KhaozEngine.Localization** (new, pure .NET): `LocalizationManager(ResourceManager)` discovers the
  cultures backed by satellite resources (`GetSupportedCultures`) and sets the current thread culture
  (`static SetCulture`, fail-fast on null/empty); `DefaultCultureCode = "en-US"`.
- **KhaozEngine.Persistence** (new; refs `KhaozEngine.Diagnostics`): `SaveEncoder(byte[] hmacKey,
  string magicPrefix, ILogger logger)` wraps save JSON in a Base64 + HMAC-SHA256 envelope
  (`{prefix}:{hmac}:{base64}`) as a casual tamper-deterrent. Decoding is lenient (recovers the JSON
  even on an HMAC mismatch) and reports each outcome (Info / Warn / Error) through the injected
  engine `ILogger`.
- **AppDataPaths consolidation:** `KhaozEngine.App.AppDataPaths` is the canonical resolver; the
  duplicate static `KhaozEngine.Diagnostics.AppDataPaths` that 3.1.0 shipped is **removed** (engine
  logging is path-agnostic - pass resolved paths into `FileSinkOptions`). Removing a 3.1.0 public type
  is breaking in principle, but numbered 3.2.0 (not 4.0.0): no released consumer referenced it (3.1.0
  is not yet adopted by any game), consistent with 3.1.0's owner-choice handling of the `FileLogger`
  removal.

## KhaozEngine 3.1.0

- **KhaozEngine.Diagnostics**: replaced the minimal `FileLogger` with a full logging service.
  `LogManager` (instance core, injectable) + a static `Log` facade own a runtime-settable
  `MinimumLevel`, an injectable `IClock`, and a list of `ILogSink`s. Category loggers via
  `Log.For<T>()` / `GetLogger(string)` stamp a component tag on each `LogEntry`
  (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception). Writes are
  non-blocking by default (a single background thread drains a bounded queue; overflow is counted in
  `DroppedCount`, reported on the next flush, and never blocks the caller) with a synchronous mode for
  deterministic tests; `Flush`/`Shutdown` drain the queue and flush sinks, and logging never throws,
  including after shutdown.
- Sinks: `FileSink` (rotate-on-launch + optional size-based rotation + retention via
  `FileSinkOptions.MaxBytes`/`MaxFiles`, `AutoFlush` for crash survivability), `ConsoleSink`
  (stderr for errors), `DebugSink` (`System.Diagnostics.Trace`), and `InMemorySink` (tests). Games
  add their own target by implementing `ILogSink`.
- `CrashHandler.Install` wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`
  to log a `Fatal` `Crash` entry and flush, so games stop hand-rolling crash hooks.
- Promoted `AppDataPaths`: OS-correct per-app data directory resolver (Windows `%APPDATA%`, macOS
  `~/Library/Application Support`, Linux XDG), created on first access and cached per app name. Engine
  logging stays path-agnostic; games pass resolved paths into `FileSinkOptions`.
- **BREAKING (shipped as a minor):** `FileLogger` is removed; consumers move to `Log`/`LogManager`. The
  default log line format gains a `[Category]` field: `[ts] [LEVEL] [Category] message`. Numbered 3.1.0
  (not 4.0.0) by owner decision: every consumer is first-party and migrated in lockstep, so the 3.x
  line is kept. This deliberately deviates from the usual SemVer "breaking = major" rule. All packages
  to 3.1.0.

## KhaozEngine 3.0.0

- **KhaozEngine.UI**: new `PrimitiveRenderer.DrawRing` (static + instance overloads) draws a circle
  outline with sub-pixel **float** thickness by stitching rotated 1x1-pixel quads along the radius
  path, so fractional thicknesses render faithfully (unlike `DrawCircle`'s integer line width). No-op
  when radius or thickness is non-positive. `RingSegments(radius, segmentsOverride)` exposes the
  segment count: an explicit override (floored at 3) or a radius-adaptive count clamped to `[18, 64]`.
- New package **KhaozEngine.Diagnostics** with `FileLogger`: a thread-safe, timestamped file logger
  for diagnosing silent crashes and startup failures. `Initialize(logFilePath, previousLogFilePath?)`
  opens an `AutoFlush` `StreamWriter` and rotates an existing log aside (when a previous path is given)
  so the most recent run is always in the primary file; `Info`/`Warn`/`Error`/`Error(msg, ex)` write
  `[ts] [LEVEL] message` lines; `Shutdown` (also `Dispose`) flushes and closes. Every method swallows
  IO failures so logging can never crash the game. Pure `System.IO`, no MonoGame dependency. The log
  path is the caller's concern (each game resolves its own app-data path and passes it in). Extracted
  from SpaceGame's in-house `GameLogger` (Nullwake had a near-identical copy; Hardpoint had none);
  instance-based and headless-testable. Adopted by SpaceGame and Nullwake.
- **KhaozEngine.Content**: fix `JsonSchemaValidator` crash ("Overwriting registered schemas is not
  permitted") when multiple data files reference the same schema file (share a `$id`). The validator
  now passes an isolated `SchemaRegistry` via `BuildOptions` to each `JsonSchema.FromText()` call
  instead of using the global static registry, so repeated builds and multi-file directories with
  shared schemas no longer abort with exit code 134. No API surface change; all existing callers
  are unaffected.
- Major bump consolidates the Content validator fix, the new Diagnostics package, and the
  `DrawRing` primitive into one clean release after untangling concurrent development. All changes are
  additive; no behaviour change for existing consumers. All packages bump to 3.0.0.

## KhaozEngine 2.4.0

- **KhaozEngine.UI**: new `PannableCanvas`, a generic pannable viewport. Owns a camera offset;
  pans on drag (`InputManager.GetDragDelta`) and vertical wheel (`InputManager.GetScrollIn`) within
  a caller-set `Viewport`; clamps the camera to `ContentBounds` inflated by `Padding` (centering an
  axis when content is smaller than the viewport). Exposes `WorldToScreen`/`ScreenToWorld`,
  `PointerWorld`, and `TryGetTap(out pressWorld, out releaseWorld)` (gated on the press-origin tap
  invariant so it stays click-through-safe). `CenterOn`/`Focus`/`CenterContent` recenter the camera.
  `Draw(sb, gd, renderScale, scaleMatrix, drawWorld)` scissor-clips to the viewport and invokes a
  world-space draw callback (pass `vr.Scale`/`vr.ScaleMatrix`). Zoom is not implemented; a single
  fixed scale, with the transform seam kept for later.
- Generalizes the inline camera/pan code in Nullwake's `SkillTreeScreen` so a node-graph / map screen
  needs no per-game reinvention. Additive and opt-in; no behaviour change for existing consumers.
  All packages bump to 2.4.0.

## KhaozEngine 2.3.0

- **KhaozEngine.Time**: new `TimeSkip` (+ `TimeSkipResult`) for advancing a simulation by a span of
  sim-time in one analytical call. `Advance(simSeconds, step)` clamps to an optional `MaxSimSeconds`,
  scales by `Multiplier`, skips requests below `MinSimSeconds` (and any `<= 0`), invokes the consumer's
  analytical catch-up callback once, raises `Completed`, and returns a `TimeSkipResult`
  (requested/applied seconds, `WasCapped`, `Ran`). Static `TimeSkip.ElapsedSimSeconds(lastSave, now,
  timeScale)` computes offline wall time (clamped >= 0, optionally scaled by sim speed).
- For on-demand "fast-forward for credits" and offline catch-up. The engine simulates nothing itself
  (the game supplies the analytical step); there is no per-frame budget because analytical catch-up is
  instant. Additive and opt-in; no behaviour change for existing consumers. All packages bump to 2.3.0.

## KhaozEngine 2.2.0

- New package **KhaozEngine.Time** with `GameClock`: separates real delta time (UI, transitions,
  notifications) from a scaled simulation delta. `TimeScale` gives slow-mo (`<1`), normal (`1`), and
  fast-forward (`>1`); `Pause()`/`Resume()` freeze the sim orthogonally to `TimeScale` (resume keeps the
  intended speed); `Paused`/`Resumed` events fire on transitions; `IsPaused` is true when paused or
  `TimeScale == 0`.
- **KhaozEngine.Screens**: `ScreenManager` now owns a `GameClock` (new `ScreenManager(InputManager, GameClock)`
  overload to share one), exposes `Clock`/`IsPaused`/`TimeScale`/`RealDeltaSeconds`/`ScaledDeltaSeconds`,
  drives transitions on real dt (so they stay live while paused), dispatches new
  `GameScreen.OnPause()`/`OnResume()` virtuals to stacked screens on pause transitions, and is now
  `IDisposable` (unsubscribes from a shared clock).
- Additive and opt-in. Default `TimeScale == 1` makes scaled dt identical to today, so the existing
  consumers are unchanged. Gameplay reads `ScaledDeltaSeconds` (e.g. `world.Update(ScaledDeltaSeconds)`);
  UI/transitions/notifications keep using real time. SpaceGame's fixed-timestep lockstep never reads the
  scaled delta, so determinism is preserved. All packages bump to 2.2.0.

## KhaozEngine 2.1.0

- New package **KhaozEngine.Content** (pure .NET, depends on JsonSchema.Net): `ConfigLoader.Load<T>`
  (embedded/disk JSON) and `JsonSchemaValidator` (instance + directory validation), plus a bundled
  validator tool and a `buildTransitive` target that validates a consumer's `Data/` against its schemas
  when `KhaozContentDataDir` is set. Generalizes Nullwake's config pattern; opt-in. All packages bump to
  2.1.0 (unified versioning); no changes to the existing four.

## KhaozEngine 2.0.0 (unified versioning)

- All four packages (Input, Screens, UI, Ecs) now share one version line and the `v*` tag scheme; the
  separate `ecs-v*` line is retired and `Ecs` no longer overrides its version. **No functional change:**
  Input/Screens/UI `2.0.0` are identical to `0.2.1`, and Ecs `2.0.0` is identical to `1.6.0`. Future
  releases bump all four together. Games can adopt `2.0.0` whenever convenient; existing vendored
  `0.2.1`/`1.6.0` references keep working.

## KhaozEngine.Ecs 1.6.0

- Deterministic outcome model: `EntityCommandBuffer.Defer(Action<World>)` (ordered deferred actions);
  a pull-model typed event channel (`World.Emit<T>` / `Events<T>`, cleared by `AdvanceTick`); and
  `DeterministicRng` (xorshift128+, seedable, save/resume `State`). Drawing RNG inside deferred actions
  gives a reproducible draw sequence (record order = the deterministic iteration order from 1.5.0).
  Additive and opt-in. Completes the determinism work (Cycles A + B).

## KhaozEngine.Ecs 1.5.0

- Deterministic iteration order: queries, `ForEach`, and serialization now walk archetypes in a
  guaranteed creation order (an explicit ordered list) rather than relying on `Dictionary` enumeration.
  Iteration is reproducible for an identical operation sequence, run-to-run and across processes
  (foundation for lockstep determinism). Swap-remove within an archetype is unchanged. Additive.

## KhaozEngine.Ecs 1.4.0

- Add named system groups: `AddSystem(system, group)`, `SetGroupOrder(...)`, `UpdateGroup(name, dt)`,
  and `SystemGroups`. `Update(dt)` runs all groups in order; `UpdateGroup` runs one (e.g. a
  fixed-timestep simulation group). Systems without a group use `"default"`, so existing usage is
  unchanged. Additive.

## KhaozEngine.Ecs 1.3.0

- Add a parent-child hierarchy: built-in `Parent` component, `World.SetParent` / `Detach` /
  `GetParent` / `Children`, and `DespawnTree` (cascade) vs plain `Despawn` (detaches children to
  root). Cycle-guarded. Hierarchies serialize (the children index rebuilds on load; `Parent` is
  auto-included by `WorldSerializer`). Transform propagation stays game-side. Additive.

## KhaozEngine.Ecs 1.2.0

- Add per-tick change detection: `World.AdvanceTick()` (call once per frame), `Added<T>()` /
  `Removed<T>()` (automatic from structural changes), `Changed<T>()` with explicit `MarkChanged<T>(e)`
  (since `ref` writes are invisible to the ECS). `Removed<T>` may include despawned entities. The load
  path does not generate events. Additive; no breaking change.

## KhaozEngine.Ecs 1.1.0

- Add `WorldSerializer`: JSON save/load of a `World` (entities + components + id-allocator state).
  Entities restore at their exact id/version so `Entity`-typed fields survive; tags and free-slot
  versions are preserved. Construct with your component types or `FromAssemblyOf<T>()`. Resources and
  systems are not serialized. Additive; no breaking change.

## KhaozEngine.Ecs 1.0.0

- Rewrite as a struct-based archetype ECS: versioned `Entity`, archetype/column storage, `ref`
  `Get<T>`, `With`/`Without` queries, `ForEach` arities 1-8, `EntityCommandBuffer`, typed `Resources`.
- Breaking vs 0.1.x: components are now `struct : IComponent`; `Get<T>` returns `ref T`; the
  `List<Entity> Query<T>()` overloads are replaced by `ForEach`. Versioned independently of the
  other KhaozEngine packages (which stay on 0.2.x).

## 0.2.1

- Fix: `PrimitiveRenderer.DrawProgressBar` rendered short bars as a solid line in the border
  color. A bar only a few pixels tall (e.g. a zoomed-out HP bar at 2px) left zero inner height
  after subtracting a 1px border on each side, so the fill never drew and the border covered the
  whole bar. The border thickness is now capped to keep at least a 1px fill area, dropping to 0 on
  bars too small to fit one. Adds headless geometry regression tests.

## 0.2.0

- `InputManager`: middle/right mouse-button edges (`IsMiddle/RightDown/JustPressed/JustReleased`).
- `InputManager.Touches` - active touches in virtual coordinates with stable ids (`TouchPoint.Id`).
- `InputManager.TryGetPinch(out Pinch)` - virtual midpoint, distance, per-frame delta, scale ratio.
- Optional gamepad/keyboard controller cursor via `cursorSpeed` ctor arg + `Update(raw, isActive, dt)`.
- All additive; 0.1.x consumers are unaffected until they bump.

## 0.1.3

- Fix: desktop clicks were suppressed whenever the game window was not at the screen
  origin. `InputManager`'s in-window check compared window-relative mouse coords against
  `WindowBounds` carrying the window's screen offset, so `Contains` rejected every click.
  The check now ignores `WindowBounds.Location` (uses Width/Height only), and
  `MonoGameRawInput` reports the client area at the origin. Adds headless regression tests.

## 0.1.2

- Add per-package README files (shown on the NuGet package pages).
- Add this changelog.

## 0.1.1

- XML documentation comments across the public API of `KhaozEngine.Input`, `.Screens`, and `.Ecs`.
- Enable `GenerateDocumentationFile` so docs ship in the packages for IntelliSense.
- No functional change from 0.1.0.

## 0.1.0

Initial release. Four packages extracted from Hardpoint/Nullwake/SpaceGame:

- **KhaozEngine.Input** - unified pointer (mouse+touch), `IsTapIn` press-origin invariant
  (click-through fix), region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation,
  coordinate-transform seam (`Identity` / `Matrix` / `VirtualResolution`), all behind the testable
  `IRawInput` seam.
- **KhaozEngine.Screens** - screen stack with top-to-bottom routing, `ConsumeWhenVisible` /
  `ConsumeWhenHandled` policies, and transitions.
- **KhaozEngine.UI** - widget library, `PrimitiveRenderer`, `TextInputHandler`.
- **KhaozEngine.Ecs** - minimal `World` / `Entity` / `ISystem`.

30 headless tests. Hardpoint migrated onto it.
