using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The modern particle / VFX demo: every authored <see cref="VfxPresets"/> effect, one active at a
    /// time, played through a <see cref="ParticleEffectPlayer"/> and drawn with Render3D's modern particle pass
    /// (soft-faded procedural sprites, trails, light links and screen-space distortion). Renders through the
    /// showcase's shared Scene3D (injected via Init, since a GameScene cannot reach the app's 3D surface). Bloom is
    /// turned on for the intended authored look. OnExit restores Bloom and HDR to the engine defaults so the toggles
    /// never bleed into the menu / other rooms. The stage is a flat neutral ground plane with an orbit camera, and
    /// the active effect auto-replays hands-off so a headless smoke and screenshots always show a live effect. Esc
    /// returns to the menu.</summary>
    public sealed class RoomVfx : GameScene, IGameScene3D, IShowcaseRoom
    {
        static readonly StringId[] Hints = { ShowcaseStrings.ControlsVfx };

        /// <summary>
        /// The authored roster this room cycles, in a fixed order, one entry per <see cref="VfxPresets"/> preset.
        /// Each getter returns a FRESH preset per call, so the entry holds the getter rather than a realized
        /// instance. <c>Attracted</c> marks a preset the room must also supply a moving
        /// <see cref="ParticleAttractor"/> for.
        /// <para>
        /// Internal, not a local inside <see cref="OnEnter"/>, so <c>RoomVfxRosterTests</c> can pin it against the
        /// preset library by reflection. It fell a preset behind once already
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/258">#258</see>): EssenceMotes shipped in
        /// 14.6.0 and nothing here noticed, so the "runnable reference for the preset library" claim quietly
        /// stopped being true.
        /// </para>
        /// </summary>
        internal static readonly (string Name, Func<VfxPreset> Preset, bool Attracted)[] Roster =
        {
            ("FireBurst",     () => VfxPresets.FireBurst,     false),
            ("FrostShatter",  () => VfxPresets.FrostShatter,  false),
            ("HealMotes",     () => VfxPresets.HealMotes,     false),
            ("EmberDrift",    () => VfxPresets.EmberDrift,    false),
            ("SparkShower",   () => VfxPresets.SparkShower,   false),
            ("Shockwave",     () => VfxPresets.Shockwave,     false),
            ("SmokePlume",    () => VfxPresets.SmokePlume,    false),
            ("ArcaneSparkle", () => VfxPresets.ArcaneSparkle, false),
            ("HeatHaze",      () => VfxPresets.HeatHaze,      false),
            ("EssenceMotes",  () => VfxPresets.EssenceMotes,  true),
        };

        public StringId Title => ShowcaseStrings.RoomVfxTitle;
        public IReadOnlyList<StringId> ControlsHints => Hints;

        // The chrome's status line (raw dev diagnostics): the active effect, its 1-based index, the live particle
        // count summed across the active player's phase pools, and the bloom toggle state.
        public string? StatusLine
        {
            get
            {
                (string name, ParticleEffectPlayer player, _, _) = _effects[_active];
                int live = 0;
                for (int i = 0; i < player.PhaseCount; i++) live += player.PhaseSystem(i).ActiveCount;
                string bloom = _scene.Post.Bloom.Enabled ? "on" : "off";
                return $"Effect: {name} ({_active + 1}/{_effects.Length})   live {live}   bloom {bloom}";
            }
        }

        // The presets are authored origin-on-ground, +Y up, reading at 8-12 units, so every effect plays here.
        static readonly Vector3 EffectOrigin = Vector3.Zero;
        static readonly Vector3 EffectAim = Vector3.UnitY;

        // Auto-replay pause: once the active effect fully drains, wait this long before playing it again, so bursts
        // loop hands-off and the ambient presets run near-continuously (a small gap between cycles).
        const float ReplayPause = 0.6f;

        // The demo drain target for an Attracted preset: a point orbiting the effect origin and bobbing, so the
        // motes visibly stream toward something that MOVES. A stationary target reads as a plain implosion and
        // demos none of what the attractor is for.
        const float AttractorOrbitRadius = 1.5f;
        const float AttractorOrbitSpeed = 1.1f;     // radians/second
        const float AttractorHeight = 1.2f;
        const float AttractorBob = 0.35f;
        const float AttractorBobSpeed = 2.3f;
        const float AttractorStrength = 14f;
        const float AttractorKillRadius = 0.22f;    // absorb on arrival, so motes land rather than orbiting
        const float AttractorMaxSpeed = 7f;
        const float AttractorMarkerRadius = 0.16f;
        static readonly Color AttractorMarkerColor = new(1f, 0.85f, 0.45f, 1f);

        Scene3D _scene = null!;
        ShowcaseHud _hud = null!;      // shared chrome: the bloom / HDR toggles toast here

        // Guards OnExit against running before OnEnter has built the per-enter state (and OnEnter against leftover
        // state from a previous visit), matching Room3D / RoomDungeon's re-entry guard.
        bool _built;

        // Flat neutral ground plane so the additive effects and flat-ground rings read against something.
        MeshHandle _ground;

        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        // One player + its per-phase looks per preset, built once in OnEnter (each VfxPresets getter returns a fresh
        // instance, so they are realized here, not held statically). Exactly one is active at a time (_active).
        (string Name, ParticleEffectPlayer Player, ParticleLook[] Looks, bool Attracted)[] _effects = null!;
        int _active;
        float _replayCountdown;

        // Seconds since OnEnter, driving the demo attractor's orbit. Its own accumulator rather than the scene
        // clock so the target starts at a fixed phase on every entry and a screenshot is reproducible.
        float _attractorTime;

        public RoomVfx Init(Scene3D scene, ShowcaseHud hud)
        {
            _scene = scene; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // A large flat plane at the origin, subdivided so it lights evenly. Drawn each frame at Identity in a
            // neutral dark tone (see OnDraw3D). No terrain, physics or character: the room is only the effects.
            _ground = _scene.LoadMesh(MeshPrimitives.Plane(width: 40f, depth: 40f, subdivisionsX: 4, subdivisionsZ: 4));

            // Realize the authored Roster: each getter returns a fresh VfxPreset, so each entry becomes its own
            // player (bounded concurrent instances) plus a per-phase ParticleLook array for DrawEffect.
            _effects = new (string, ParticleEffectPlayer, ParticleLook[], bool)[Roster.Length];
            for (int i = 0; i < Roster.Length; i++)
            {
                VfxPreset preset = Roster[i].Preset();
                var player = new ParticleEffectPlayer(preset.Effect, maxInstances: 8, seed: 1);
                _effects[i] = (Roster[i].Name, player, preset.Looks.ToArray(), Roster[i].Attracted);
            }

            _active = 0;
            _replayCountdown = ReplayPause;
            _attractorTime = 0f;
            _effects[_active].Player.Play(EffectOrigin, EffectAim);

            _camera = new FollowCamera3D { Target = new Vector3(0f, 1f, 0f) };
            _camera.Distance = 10f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // The presets are authored to feed bloom, so the room shows the modern look by default. OnExit restores
            // this (and HDR) to PixelPostProcessSettings's own defaults.
            _scene.Post.Bloom.Enabled = true;

            _built = true;
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            InputState input = Manager!.Input;

            // Left / Right cycle the active preset (wrap both ways): clear the outgoing player, play the incoming.
            if (input.WasPressed(Key.Left)) Switch(-1);
            if (input.WasPressed(Key.Right)) Switch(1);

            // Space replays the active effect immediately.
            if (input.WasPressed(Key.Space))
            {
                ParticleEffectPlayer player = _effects[_active].Player;
                player.Clear();
                player.Play(EffectOrigin, EffectAim);
                _replayCountdown = ReplayPause;
            }

            // Tier 1 post toggles, each toasting its new state like Room3D's toggles.
            var post = _scene.Post;
            if (input.WasPressed(Key.B)) { post.Bloom.Enabled = !post.Bloom.Enabled; _hud.Toast($"[vfx] bloom = {post.Bloom.Enabled}"); }
            if (input.WasPressed(Key.H)) { post.Hdr.Enabled = !post.Hdr.Enabled; _hud.Toast($"[vfx] HDR = {post.Hdr.Enabled}"); }

            // Only the active player is advanced (the rest are cleared and idle).
            ParticleEffectPlayer active = _effects[_active].Player;
            _attractorTime += dt;
            if (_effects[_active].Attracted)
            {
                // Re-set every frame: ParticleAttractor is a value, so moving the target means handing the player
                // a new one. Set BEFORE Update so this frame's integration already pulls toward the new position.
                active.Attractor = new ParticleAttractor
                {
                    Target = AttractorTarget,
                    Strength = AttractorStrength,
                    StrengthCurve = ParticleCurve.EaseIn,
                    KillRadius = AttractorKillRadius,
                    MaxSpeed = AttractorMaxSpeed,
                };
            }
            active.Update(dt);

            // Auto-replay: while the effect is alive keep the pause primed. Once it fully drains, wait ReplayPause
            // then play it again, so bursts loop and the ambient presets run near-continuously with no input.
            if (active.AnyAlive)
            {
                _replayCountdown = ReplayPause;
            }
            else
            {
                _replayCountdown -= dt;
                if (_replayCountdown <= 0f)
                {
                    active.Play(EffectOrigin, EffectAim);
                    _replayCountdown = ReplayPause;
                }
            }

            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        /// <summary>Where the demo drain target is right now: orbiting the effect origin and bobbing in Y. Pure
        /// in <see cref="_attractorTime"/>, so the same elapsed time always puts it in the same place.</summary>
        Vector3 AttractorTarget => EffectOrigin + new Vector3(
            MathF.Cos(_attractorTime * AttractorOrbitSpeed) * AttractorOrbitRadius,
            AttractorHeight + MathF.Sin(_attractorTime * AttractorBobSpeed) * AttractorBob,
            MathF.Sin(_attractorTime * AttractorOrbitSpeed) * AttractorOrbitRadius);

        // Switch the active preset by dir (wrapping), clearing the outgoing player and playing the incoming one.
        void Switch(int dir)
        {
            // Drop the outgoing player's attractor along with its particles, so an Attracted preset left idle is
            // not holding a stale target from whenever it was last on screen.
            _effects[_active].Player.Clear();
            _effects[_active].Player.Attractor = null;
            _active = (_active + dir + _effects.Length) % _effects.Length;
            (string name, ParticleEffectPlayer player, _, _) = _effects[_active];
            player.Play(EffectOrigin, EffectAim);
            _replayCountdown = ReplayPause;
            _hud.Toast("[vfx] " + name);
        }

        public void OnDraw3D(Scene3D scene)
        {
            // Neutral dark ground so additive effects and flat-ground rings read against it.
            scene.Draw(_ground, Matrix4x4.Identity, new Color(0.16f, 0.17f, 0.20f, 1f));

            // The active effect only: one ParticleLook per phase, a shared light budget across the effect.
            (_, ParticleEffectPlayer player, ParticleLook[] looks, bool attracted) = _effects[_active];
            scene.DrawEffect(player, looks);

            // Mark the drain target. Without something visible there the motes read as streaming off toward
            // nothing, which demos the opposite of what the attractor does.
            if (attracted)
                scene.DebugWireSphere(AttractorTarget, AttractorMarkerRadius, AttractorMarkerColor, segments: 16);
        }

        // Tears down everything OnEnter built into the shared Scene3D, so the menu / other rooms render cleanly and a
        // re-entry rebuilds from scratch, matching Room3D / RoomDungeon's teardown. Guarded so an early exit (before
        // OnEnter finished) is a safe no-op.
        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            // The players own no GPU resources (the sim is render-free). Clear them so no instance keeps scheduling.
            foreach ((_, ParticleEffectPlayer player, _, _) in _effects)
            {
                player.Clear();
                player.Attractor = null;
            }

            _scene.UnloadMesh(_ground);
            _scene.CameraOverride = null;

            // Restore the two Post toggles this room flipped, back to PixelPostProcessSettings's own defaults (Bloom
            // default OFF byte-stable, HDR default ON), so leaving the room never bleeds the modern-look bloom or a
            // disabled HDR chain under the menu / 2D rooms. Post has no setter (a shared instance owned by Scene3D),
            // so the fields are reset individually, matching Room3D's OnExit.
            var post = _scene.Post;
            post.Bloom.Enabled = false;
            post.Hdr.Enabled = true;

            _effects = null!;
            _camera = null!;
            _camController = null!;
            _ground = default;
            _active = 0;
            _attractorTime = 0f;
        }
    }
}
