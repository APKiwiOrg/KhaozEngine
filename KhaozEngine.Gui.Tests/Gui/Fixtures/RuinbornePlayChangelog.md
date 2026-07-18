# Ruinborne - Player Changelog

---

## 2026-07-10

### Build 0.3.16 (Alpha 001)

- **Minor**
  - Under the hood, the world layout (the valley, the town, the lake, the mountain wall, and where
    everything is placed) now loads from a single map file instead of being written directly into the
    game's code. Nothing should look or feel different: same terrain, same buildings, same wolf. If
    anything about the world looks off after this build, say so - that's the one thing to watch.

---

## 2026-07-09

### Build 0.3.15 (Alpha 001)

- **New**
  - Sword swings leave a light air trail off the blade tip, tracing the arc of your cut.
  - While your `sword` is drawn your arm actually carries it in a guard stance, standing,
    walking, or running, instead of swinging loosely like an empty hand.
  - Drawing and sheathing your `sword` is a real animated motion now instead of the blade
    teleporting between your hand and your back.

- **Bug**
  - The `sword` finally sits right in the hand: blade forward and low instead of sideways across
    the body. The swing itself is a professionally animated standing slash now, instead of the
    old stiff arm-only slap.
  - The sword swing now drives the whole upper body through the diagonal cut, so the slash reads
    as a committed strike instead of a stiff arm sweep.
  - Your own nameplate shows your name again instead of a wall of login token text.
  - Sword hits now land when the blade sweeps in front instead of the instant you click, and the
    blade trail only lights up on the forward slice.

---

### Build 0.3.14 (Alpha 001)

- **Minor**
  - Shallow water at the lake shore is visible now: wading in ankle deep used to render as if you
    were standing on dry sand. The beach texture line also matches the real waterline.

---

### Build 0.3.13 (Alpha 001)

- **Minor**
  - On Windows the game no longer opens a separate black console window behind it. Launching
    from a terminal still prints diagnostics to that terminal.

---

### Build 0.3.12 (Alpha 001)

- **Bug**
  - Menu and update-popup dimming now covers the whole window: on wide or unusual window shapes
    the edges used to stay bright while the middle dimmed.

---

### Build 0.3.11 (Alpha 001)

- **Bug**
  - Swimming is smooth again: the server was flip-flopping every tick about whether you were in
    the water, which made swimmers and floaters stutter.
  - Other players now actually look like they are swimming instead of endlessly jumping, and
    their sword correctly goes away while they swim.

---

## 2026-07-08

### Build 0.3.10 (Alpha 001)

- **New**
  - Chat got a real chat box: press Enter to open it, scroll back through history with the mouse
    wheel, Enter sends and stays open for a follow-up, Esc closes it.
- **Bug**
  - Pressing Enter to chat actually opens the composer now. It used to open and close itself in
    the same instant, which looked like nothing happened.
  - HUD and chat text is crisp at every resolution instead of slightly blurred.

---

### Build 0.3.9 (Alpha 001)

- **Minor**
  - If a future update is marked required, the game now downloads it and restarts to apply on its
    own, with no keypress, so a mandatory fix can reach you even if the update popup is not in front
    of you. Optional updates are unchanged: they still wait for you to press the key.

---

### Build 0.3.8 (Alpha 001)

- **Bug**
  - The in-game update popup never actually showed itself, quietly stranding everyone on their
    current version. It now appears properly when a new build is ready, and updating is a
    keypress again.
  - The dark band on legs and feet is now fixed at the source: your own shadow no longer paints
    itself onto your body. The temporary workaround from build 0.3.4 is gone with it.

---

### Build 0.3.7 (Alpha 001)

- **Minor**
  - The game now writes a session log and a crash report file, so when something goes wrong we
    can actually find out why instead of guessing. Logs live in the game's data folder.

---

### Build 0.3.6 (Alpha 001)

- **Bug**
  - The live `wolf` had 1 health from an old database row, dying to a single hit and paying no
    XP or gold. It now has its intended 30 health, 4 sword swings worth, and pays out again.
  - The live swing cooldown row also still held the old fast value, so the 2 second attack pace
    now actually applies on the server once deployed.

---

### Build 0.3.5 (Alpha 001)

- **Minor**
  - The camera no longer judders up and down while you swim: vertical motion now eases with
    the same smoothing the rest of the movement already had.

---

### Build 0.3.4 (Alpha 001)

- **New**
  - Your `sword` now rides on your back while sheathed, slung diagonally behind the right
    shoulder, and other players see it there too.
- **Rebalance**
  - Swinging is deliberate now: one swing every 2 seconds (it was almost three per second), and
    the swing itself plays out over 0.7 seconds instead of a blur. Attack speed is now a weapon
    stat, so faster blades can exist later.
- **Bug**
  - Swinging (or jumping mid-fight) no longer snaps your other arm into a stiff T-pose: only the
    sword arm follows the swing now.
  - The `sword` sits properly in the fist, blade forward and edge down, instead of angling
    backwards through the ground.
  - Fixed the dark band that painted itself across your feet and shins and slid up and down as
    you moved: your character's ground shadow was tinting its own legs.

---

### Build 0.3.3 (Alpha 001)

- **Bug**
  - Swimmers float at the surface now instead of sinking a couple of metres under the waterline
    (the swim animations carried their own vertical offset that pushed the body deep underwater).
  - The `sword` is its full length again, sits blade-forward in the hand, and slashes in front of
    you. It was half size and gripped along the wrong hand axis, which pointed it backwards, made
    it render as a broken thin line, and kept every swing behind the body.
- **Minor**
  - The outfit paint switched to its light variant, so legs and feet no longer shade dark at a
    distance.

---

### Build 0.3.2 (Alpha 001)

- **Bug**
  - Everyone was frozen in a T-pose: a broken avatar bake shipped every animation clip with no
    motion in it, so characters slid around rigidly instead of walking, swimming, or swinging. All
    of it moves again: idle, walk, run, jump, fall, treading water, swimming, and the `sword` swing.
  - Re-drawing your `sword` is reliable now. After sheathing once (or being force-sheathed by a
    swim), drawing again looked like nothing happened on your own screen, even though everyone else
    could see the blade out.

---

### Build 0.3.1 (Alpha 001)

- **New**
  - You can swim now. Wade in and the water slows you with depth; past chest deep you switch to a
    proper surface swim, treading in place or stroking forward. Shallow water still lets you hop out.
    Your `sword` sheathes itself while you swim, and other players see you swimming too.
  - Updates now show up in-game: a themed popup tells you when a new build is available, shows the
    download progress live, and applies it on a keypress, instead of the game quietly stalling at
    launch while it downloaded.
- **Minor**
  - The wade slowdown is now enforced by the server, and it eases in from ankle depth for a more
    natural feel at the shoreline.
  - The game window now opens immediately at launch; the update check happens in the background after
    you connect.

---

### Build 0.3.0 (Alpha 001)

- **New**
  - Your `sword` now actually hits: swing at the `wolf` and it takes damage, its health bar drops for
    everyone watching, and enough hits bring it down. It respawns near its den about half a minute
    later, fresh for the next fight.
  - Slaying the `wolf` earns you XP and gold. Your level, XP, and gold now show at the bottom-right
    of the screen and are saved with your character.
  - Zone chat: press `Enter`, type, and `Enter` again to talk to everyone in the zone. The last few
    lines show at the bottom-left. `Esc` closes the box without sending.
  - The lake finally has water in it: a real reflective surface with sun glints, sitting in the basin
    beside town. And the water pushes back: you slow to a wade as you go deeper, and past chest depth
    your `sword` sheathes itself, no fighting in deep water.
  - Characters now cast shadows. A soft ground shadow is on by default, and a new `Shadows` setting
    (Off / Simple / Full) in the settings menu lets you pick full sun-cast shadows or turn them off.
  - A proper sky: a graded blue with a sun disc that matches where the light (and your new shadow)
    actually comes from.
- **Minor**
  - Health bars over players and creatures now show real health instead of always-full decoration.
  - Menus and settings screens have a crisper, higher-contrast look.

---

### Build 0.2.2 (Alpha 001)

- **New**
  - You can now carry a `sword`: press `R` to draw or sheathe it, and left-click to swing. Other
    players see your drawn `sword` and every swing you make. The camera now orbits with the right
    mouse button, since left-click is the swing.

---

### Build 0.2.1 (Alpha 001)

- **Bug**
  - Fixed auto-update on Mac: the game could download a new version but never finish installing it,
    quietly rolling back to the old build every time. Mac players now stay up to date automatically,
    like Windows and Linux players already did. (Engine KhaozEngine 10.9.0.)
  - Fixed a rare mix-up where joining and disconnecting at just the wrong moment could let another
    player's position be saved onto your character. Your save now always stays your own.
  - The server now rides out brief database hiccups instead of quietly losing track of a player's
    save position until they reconnect, and no longer crashes if the database is still waking up
    when the server starts.

---

## 2026-07-05

### Build 0.2.0 (Alpha 001)

- **Minor**
  - Under-the-hood networking upgrade that future-proofs the shared world for long-running servers.
    You'll need this build to connect to the server. (Engine KhaozEngine 10.7.1.)
- **Bug**
  - Fixed a jitter when you stop moving (walk or sprint): your character and the camera now settle
    smoothly instead of shaking back and forth, and your character no longer briefly spins to face the
    wrong way as it stops.
  - Fixed a rare case where your saved character position could be lost if you left the game at the
    exact moment the server was loading you in.

---

## 2026-07-04

### Build 0.1.98 (Alpha 001)

- **Minor**
  - The game now remembers where you left its window and what size it was, and reopens it there next
    time instead of snapping back to the default position. (Engine KhaozEngine 9.26.0.)

---

### Build 0.1.97 (Alpha 001)

- **Minor**
  - The updater now shows a progress window themed to match the rest of the game while it downloads.
- **Bug**
  - Fixed a timing bug where the updater could fail to relaunch the game cleanly after applying an
    update. (Engine KhaozEngine 9.25.0.)

---

### Build 0.1.95 (Alpha 001)

- **Rebalance**
  - Default audio levels are now `Master` 50, `Music` 45, `Sound Effects` 70, a more comfortable
    out-of-the-box mix.

---

### Build 0.1.94 (Alpha 001)

- **Minor**
  - Changing the graphics `Quality` setting no longer resizes the window.
  - The pause and settings overlay now dims the whole window so it is easier to read over the game.
  - The default window size is now 1600x900.

---

### Build 0.1.92 (Alpha 001)

- **New**
  - Press `Esc` to open a pause menu. `Graphics` and `Sound` settings can now be changed from there
    while you play and take effect immediately. (Engine KhaozEngine 9.24.0.)

---

### Build 0.1.91 (Alpha 001)

- **Bug**
  - Other players and creatures now move more smoothly on your screen, with less jitter in how their
    motion is shown. (Engine KhaozEngine 9.23.0.)

---

### Build 0.1.87 (Alpha 001)

- **Bug**
  - Walk and run animations now keep pace with how fast you're actually moving, so feet stop sliding or
    "skating" across the ground when your speed doesn't match the animation. The `wolf` gets the same fix: its
    gallop keeps up with the chase instead of gliding. (Engine KhaozEngine 9.20.0.)

---

### Build 0.1.85 (Alpha 001)

- **New**
  - Creatures wear a nameplate now too: the `wolf` (and any future NPC) floats the same name-and-health plate
    players do, dropped to sit just above the animal. Health shows full for now.

---

### Build 0.1.82 (Alpha 001)

- **Minor**
  - Names over players' heads are now a proper nameplate: the name sits in a rounded plate with a health bar
    under it, instead of a bare line of text. The health bar shows full for now, there are no health stats yet.
    (Engine KhaozEngine 9.17.0.)

---

### Build 0.1.81 (Alpha 001)

- **Minor**
  - The grass "fuzz"/shimmer fix now uses a lighter anti-aliasing technique (`FXAA`) that costs a fraction of the
    previous 2x supersampling, so it runs smoother while still calming the moving-camera shimmer on the ground.
  - Removed the developer diagnostic keys (`F7` terrain-sampler cycle, `F8` GPU debug panel, `F9` anti-alias toggle)
    that were added while hunting that shimmer. The cause is found and fixed, so the tooling is gone.
    (Engine KhaozEngine 9.15.0.)

---

### Build 0.1.80 (Alpha 001)

- **New**
  - Ruinborne now shows in your Discord status while you play (`Rich Presence`): what you're doing and how long
    you've been in. Purely cosmetic, and it does nothing if you don't run Discord. (Engine KhaozEngine 9.14.0.)

---

## 2026-07-03

### Build 0.1.78 (Alpha 001)

- **Minor**
  - Under the hood: every bit of on-screen text (the sign-in screen, connection banners, the `F1`/`F8` debug
    panels) now runs through a localisation layer instead of being hard-coded. Still English-only, so nothing
    looks different yet, but the game is now set up to add other languages later. (Engine KhaozEngine 9.14.0.)

---

### Build 0.1.77 (Alpha 001)

- **Bug**
  - The two spawn rocks got a pass: they now sit slightly into the ground instead of perching on top, they're
    plain grey to match the other rocks (they had been rendering dark), and each has its own collision shape
    baked from its actual model instead of borrowing another rock's.

---

### Build 0.1.76 (Alpha 001)

- **New**
  - Two rock models a friend made are sitting just past the spawn point, one either side, so you can walk up
    and look at them in-game. They're solid - you bump into them like any other rock.

---

### Build 0.1.63 (Alpha 001)

- **Bug**
  - Blacksmith chimney collider redone against the actual stone stack: the low strip that ran along the
    wall to the door is gone (that stone is the door arch, not chimney), the flat band on the wall is gone,
    and the stack collider is now as deep as the real masonry at every height.

---

### Build 0.1.62 (Alpha 001)

- **Bug**
  - The blacksmith's front chimney collider now follows the real taper: wide hearth at the bottom, the
    full-width breast up to head height (0.1.61 left its right side uncovered), then the narrow shaft. The
    firewood next to it is a simple knee-high collider at the actual log height instead of a tall block.

---

### Build 0.1.61 (Alpha 001)

- **Bug**
  - The blacksmith chimney's wide hearth base is solid again (0.1.60's slim-down kept only the narrow shaft,
    leaving a walk-through gap in the lower stack beside the door).

---

### Build 0.1.60 (Alpha 001)

- **Bug**
  - Chimney collision pass from the `F2` view: every chimney stack is now solid (they had no colliders, so
    you could clip through them when up on a roof) - the crest chimney on the round-window house, the side
    chimney on the two-wing house, and the blacksmith's rooftop stack. The blacksmith's front chimney
    collider also slimmed down to the actual stone (it stuck out half a metre into open air beside it).

---

### Build 0.1.59 (Alpha 001)

- **Minor**
  - Under the hood, the server moved onto the engine's sharded world stack (so it can eventually save world
    objects like mobs, ore, and dropped items across restarts, and shard for bigger crowds later). Nothing
    should look or feel different: same movement, same world, same players. If your movement feels off near the
    mountain wall, the town, or props after this build, say so - that's the one thing to watch.

---

### Build 0.1.58 (Alpha 001)

- **Bug**
  - The last two chunky colliders from the `F2` view are gone: the well's collider no longer fills the open
    space between the stone ring and its little roof, and the blacksmith's forge/woodpile colliders stop at
    the real forge hood and log pile instead of running up into the porch roof. (These were old anti-stuck
    fills that the sloped roof colliders made unnecessary.)

---

### Build 0.1.57 (Alpha 001)

- **Bug**
  - The inn's dormer windows now each get their own little roof collider. Before, the three dormers on each
    side of the roof shared one long band collider running across all of them (visible as a flat purple strip
    in the `F2` view). house_2's two dormers also stop stretching their colliders across the main roof.

---

### Build 0.1.56 (Alpha 001)

- **Bug**
  - Reduced the shimmering "fuzz" on the grass at middle distance when you move the camera. The ground now uses
    sharper-angle texture filtering plus a touch of distance blur, so the grass stops sparkling into noise as you
    pan around (engine 9.5.0). The trees/rocks/buildings fix from 0.1.53 stays. If the ground looks too soft now,
    say so - it's a dial we can back off.

---

### Build 0.1.55 (Alpha 001)

- **Bug**
  - Final `F2` collision-view polish round: roof colliders no longer poke past gable crests in little X tips
    (they meet exactly at the peak), the inn's dormer windows each get their own small roof collider instead
    of one criss-crossing band, the two-wing house's roof pieces stop bleeding through each other at the
    junction, and eave colliders no longer overshoot the actual roof edge.

---

## 2026-07-02

### Build 0.1.54 (Alpha 001)

- **Bug**
  - Collision polish from the `F2` view feedback: dormer windows sticking out of roofs now carry their own
    little sloped colliders on every house, the boxy invisible shells around upper floors and gables are gone
    (walls now stop where the roof starts), the stray flat panels floating over some roofs are gone, and
    overlapping duplicate roof pieces were removed.

---

### Build 0.1.53 (Alpha 001)

- **Bug**
  - Fixed textures breaking into a shimmering "pixely" mess at medium and far distance when the camera moves.
    Trees, rocks, buildings, and the cabin now stay smooth into the distance (engine mip-chain fix, 9.2.0).

---

### Build 0.1.52 (Alpha 001)

- **Bug**
  - Restored the square stone house's roof collider (0.1.51's ridge fix accidentally removed its hip roof;
    the ridge lines still meet cleanly everywhere).

---

### Build 0.1.51 (Alpha 001)

- **Bug**
  - Roof colliders now meet exactly at the rooftop ridge lines. The last of the X shapes in the `F2` debug view
    (at gable peaks, across one house's dormers, and at the well's roof tip) are gone.

---

### Build 0.1.50 (Alpha 001)

- **Bug**
  - Roof colliders now stop at the ridge instead of crossing past it (the X shapes in the `F2` view over roof
    tips and dormer windows are gone), and the well's roof collider matches its actual roof again.

---

### Build 0.1.49 (Alpha 001)

- **Bug**
  - Same content as 0.1.48 (which never shipped: its release run caught one more stuck spot). Also fixed:
    sliding down the blacksmith's roof could drop you into a pocket between the woodpile, forge and chimney
    that you couldn't leave. That corner is solid now, so you slide on off the roof to open ground.

---

### Build 0.1.48 (Alpha 001, unreleased)

- **Bug**
  - Roof collision now matches the actual roofs. Every building's roof collider is computed from the real
    geometry (the `F2` debug view should hug the shapes now): the square stone house gets its proper flat-topped
    roof, the blacksmith and inn roofs sit at the right slopes, and porch/awning roofs no longer have an
    invisible flat underside hanging below them.
  - Fixed three spots where you could get stuck pinned between something you stood on and a roof above it:
    jumping onto the blacksmith's forge, the well's rim, and the blacksmith entrance under its front eave.

---

### Build 0.1.47 (Alpha 001)

- **New**
  - New debug view: press `F2` to see the invisible collision shapes drawn as see-through colored blocks over the
    world (building bodies and steps, tree trunks, rocks), with a small legend explaining the colors. Press `F2`
    again to hide it. Purely visual, no gameplay effect. Handy for spotting collision that does not match what
    you see, so if something looks off in this view, report it.
- **Minor**
  - Engine updated to KhaozEngine 9.1.0 (adds the overlay renderer, no gameplay changes).

---

### Build 0.1.45 (Alpha 001)

- **Bug**
  - Building collision playtest fixes. The inn's veranda rails now block you instead of letting you walk through
    them (the door gap stays open), the blacksmith's front chimney is solid so you no longer clip inside it, and
    the small house with the overhanging wing had its entrance collider on the wrong wall: the steps now sit on
    the real door under the wing.
  - Roofs are solid and sloped. If you end up on a roof you now stand or slide on the visible slope instead of
    floating half-inside it, and jumping under an eave bumps it instead of putting your head through. Applies to
    every building including the well's little roof.

---

### Build 0.1.44 (Alpha 001)

- **Bug**
  - Building collision now fits the buildings. Walls stop you where you see the wall instead of up to half a
    metre out (the old collision boxes wrapped the roof overhangs), and the well is round instead of a fat
    square. Porch posts, rails and other thin trim no longer collide at all.
  - Entrance steps are real steps now. Each door's collider sits on the building's actual stone steps (the old
    ones floated off to the side), you walk straight up onto a flat landing at the door, and you can stand
    anywhere on the steps without sliding off. Applies to every entrance: the inn's front and back porches, the
    blacksmith, all three houses and the bell tower.

---

## 2026-07-01

### Build 0.1.43 (Alpha 001)

- **Bug**
  - You no longer get stuck on buildings. Town buildings now collide as simplified solid shapes instead of their
    full detailed geometry, so you can't wedge on the little wooden feet around the edges or on objects in the
    blacksmith forge. Building entrance steps are a smooth ramp now, so stairs no longer catch you. (Engine
    KhaozEngine 8.11.0.)

---

## 2026-06-30

### Build 0.1.42 (Alpha 001)

- **Minor**
  - The game window now shows the Ruinborne icon in the title bar and taskbar (Windows and Linux). macOS keeps using the app bundle icon.

---

## 2026-06-29

### Build 0.1.21 (Alpha 001)

- **Minor**
  - Thinned out the big round dark-green bushes (down to about a tenth as many). The grass, ferns and other
    ground cover are unchanged; only that one bushy plant is rarer now. Still purely visual.

---

### Build 0.1.20 (Alpha 001)

- **Bug**
  - Player names stay put when you move the camera. A name above a distant player used to pop in and out as you
    rotated or zoomed the view, because the show/hide range was measured from the camera rather than from your
    character. It now measures from your character, so spinning the view no longer flickers names. (Engine
    KhaozEngine 7.72.0.)

---

### Build 0.1.19 (Alpha 001)

- **Minor**
  - Tuned the ground cover. Thinned the open grass to about 60% so it reads as scattered rather than a carpet,
    packed more foliage tight around tree trunks so they blend into the ground, added foliage hugging the base of
    every town building where it meets the terrain, and let some lighter cover into the town and around the well.
    Still purely visual.

---

### Build 0.1.18 (Alpha 001)

- **New**
  - The ground is grown over. The bare terrain between the trees now has grass, ferns, bushes and the odd mushroom
    scattered across it, and each tree has a few shrubs and ferns clustered around its base, so trees sit in the
    landscape instead of poking out of flat ground. Purely a visual change. (Engine KhaozEngine 7.71.0.)

---

### Build 0.1.17 (Alpha 001)

- **Bug**
  - Smoother other players. Remote players used to step/teleport a little as they walked (their position updated
    only at the server's tick rate); they now glide smoothly between updates, matching how their animation already
    looked. Your own character was already smooth. (Engine KhaozEngine 7.70.0.)

---

## 2026-06-28

### Build 0.1.16 (Alpha 001)

- **Bug**
  - Fixed the terrain rendering as solid white on Windows. On Direct3D11 (Windows) the whole ground showed up as a
    flat white sheet instead of the textured grass/dirt/rock surfaces - a graphics bug in how the terrain shader's
    per-vertex data was laid out for that backend (Mac and Linux were unaffected). The ground now renders correctly
    on Windows too. (Engine KhaozEngine 7.69.1.)

---

### Build 0.1.15 (Alpha 001)

- **Minor**
  - Under the hood: your saved access token now lives next to the rest of Ruinborne's app data, under the same
    shared folder the other games use, instead of its own separate folder. One-time effect: after this update you
    may be asked to paste your token once more, then it's remembered again as before.

---

### Build 0.1.14 (Alpha 001)

- **Bug**
  - Rocks are grey again. The scattered rocks rendered with an olive/green tint (a bad colour baked into the
    rock art, left over from how the meshes were imported); they're now a neutral stone grey.

---

### Build 0.1.13 (Alpha 001)

- **Bug**
  - More animation fixes. The walk/run cycle no longer resets every few seconds (you'd notice it most while
    sprinting), and other players no longer flail the jump/fall animation while running over hills. (Engine
    KhaozEngine 7.68.0.)

---

### Build 0.1.12 (Alpha 001)

- **New**
  - Real ground textures. The terrain's first-pass procedural surfaces are replaced with real photographic
    grass, dirt, rock, sand, and snow (CC0, ambientCG), blended across the world with normal-mapped detail, so
    the ground and mountains read as actual surfaces. (Engine KhaozEngine 7.67.0.)

---

### Build 0.1.11 (Alpha 001)

- **Bug**
  - Fixed the player animation. Characters were sliding around frozen on the first frame of a pose; they now
    idle/walk/run/jump properly, for you and everyone else. (Engine KhaozEngine 7.67.0.)

---

### Build 0.1.10 (Alpha 001)

- **New**
  - Textured ground and mountains. The terrain used to be flat colour bands; it now renders blended
    grass/dirt/rock/sand/snow surface layers with normal-mapped relief, so the ground and the mountain wall
    have texture and depth instead of a solid-colour ramp. (First-pass procedural textures; real surface art is
    a follow-up. Engine KhaozEngine 7.65.1.)

---

### Build 0.1.9 (Alpha 001)

- **New**
  - Players have bodies now. Everyone is a proper animated character instead of a capsule - they idle, walk, run,
    and jump/fall, and turn to face the way they're moving. (CC0 Quaternius art; engine KhaozEngine 7.65.0.)

---

### Build 0.1.8 (Alpha 001)

- **Minor**
  - Hold `Backspace` to delete. The token field now repeats while you hold a key (`Backspace` or any character),
    instead of one press per character, so fixing a pasted token is no longer a tap-fest. (Engine KhaozEngine
    7.63.0.)

---

### Build 0.1.7 (Alpha 001)

- **Bug**
  - Fixed the token screen rejecting valid tokens. Newer tokens (the ones that carry your display name) were
    refused with "that does not look like a valid token"; they're accepted now.

---

### Build 0.1.6 (Alpha 001)

- **New**
  - Player names. Each player now has their name floating above their head. Your name comes from your token (mint
    one with a name: `Ruinborne.Server mint <account> <name>`); if you don't pick a name it shows your account
    name.
- **Bug**
  - Smoother movement. Walking and jumping against the live server no longer jitters or stutters: the camera and
    other players glide instead of snapping. (Engine KhaozEngine 7.62.0.)

---

### Build 0.1.5 (Alpha 001)

- **New**
  - Real trees and rocks. The grey placeholder shapes are gone: the world is now scattered with proper pines,
    oaks, and rocks (CC0 Quaternius). Trees and rocks are solid now, you walk around them rather than through.
  - Jump. Press `Space` to jump; you can run off a ledge and fall, and jump up onto the rocks and walk across the
    top. (Engine KhaozEngine 7.60.0.)

---

### Build 0.1.4 (Alpha 001)

- **Bug**
  - Under the hood: the 0.1.3 token-paste fix is now fixed at the engine source (KhaozEngine 7.59.1), so a
    modifier chord like `Cmd/Ctrl+V` can no longer leak a stray character into any text field. No visible change.

---

### Build 0.1.3 (Alpha 001)

- **Bug**
  - Fixed the token paste screen rejecting valid tokens. Pasting with `Cmd/Ctrl+V` was typing a stray character
    into the field, so the token failed verification and you got "could not connect". Paste works now.

---

### Build 0.1.2 (Alpha 001)

- **New**
  - Sign-in. First launch now asks for an alpha access token: paste the token you were sent and it is
    remembered for next time. The server verifies a signed token on connect, so your character is tied to your
    token rather than to whoever joins first; a wrong or expired token bounces you back to re-enter.
- **Minor**
  - Cloud by default. Release (downloaded) builds connect to the live cloud server with no arguments; Debug
    builds still target a local server.
- **Bug**
  - Reconnect no longer freezes. Rejoining (or landing on a recycled player slot) used to leave you frozen on a
    stale movement ack. Fixed on KhaozEngine 7.59.0.

---

### Build 0.1.0 (Alpha 001)

- **New**
  - First playable slice. Bootstrapped the repo on KhaozEngine 7.48.0.
  - Windowed client streams the Ruinborne overworld (sunken ruin flats rising to a broken mountain wall,
    a flooded crater pool, a levelled spawn courtyard) with deterministic prop scatter (standing stones,
    broken pillars, rubble). Third-person walk: WASD, mouse-drag orbit, scroll zoom, shift run.
  - Headless authoritative server over the same terrain; client connects via LiteNetLib on localhost and
    renders a capsule per replicated player.
