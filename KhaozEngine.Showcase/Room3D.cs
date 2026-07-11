using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>One hand-placed town building: which CC0 kit <see cref="Id"/> (must match an entry in
    /// buildings.manifest.json), its world XZ, facing <see cref="Yaw"/> (radians), and uniform <see cref="Scale"/>.
    /// Rendered and collidable (its matching baked .coll is added as a scaled static in OnEnter).</summary>
    public readonly record struct TownBuilding(string Id, float X, float Z, float Yaw, float Scale);

    /// <summary>The walkable streamed 3D overworld, ported from TerrainWalkSample into a room. Renders through the
    /// showcase's shared Scene3D (injected via Init, since a GameScene cannot reach the app's 3D surface). Builds
    /// its world in OnEnter and tears it down in OnExit (the Scene3D is shared with the other rooms, so it must
    /// leave no camera override or loaded ring behind). Esc returns to the menu.</summary>
    public sealed class Room3D : GameScene, IGameScene3D
    {
        // distance-cull ring for instanced props around the focus point (matches TerrainWalkSample's PropDrawRadius).
        const float PropDrawRadius = 90f;

        const float CapsuleRadius = 0.3f;
        const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)

        // Town clearing: a flat plateau holding the hand-placed buildings. Sits inside BoundedClearing's rim
        // (inner radius 38 from origin, with a pass to the north) and clear of its lake at (-12,-4) r8. Pushed
        // north of spawn and widened so the buildings can spread out without clipping: the disc is centred at
        // (0,18) with radius 24, so its east/west edge (X=+-24 at Z=18, ~30 m from origin) stays well inside the
        // rim and only the due-north edge reaches into the pass. Values are a starting point, adjust by playtest.
        static readonly Vector2 TownCenter = new(0f, 18f);
        const float TownRadius = 24f, TownBlend = 0.25f;

        // Draw radius for the 7 hand-placed town buildings: few in count and always meant to be visible from
        // across the clearing, so this is a much wider cull ring than PropDrawRadius (matches Ruinborne's high
        // draw radius for its landmark buildings).
        const float BuildingDrawRadius = 320f;

        // The 7 CC0 Quaternius Medieval Village buildings, hand-placed around TownCenter as a loose village ring
        // biased north (the lake is off the SW, spawn is due south). Spread so no two footprints clip: the tightest
        // pair (house_2 / house_3) is ~10 m centre-to-centre, everything else more. All sit inside
        // TownRadius * (1 - TownBlend) ~= 18 m of TownCenter so they land on the fully-flattened plateau, not its
        // blended rim, and clear of the platform box (0,12), the textured stone block (3,3), and the lake (-12,-4).
        // Offsets are from TownCenter; world positions in the trailing comments. Tunable by playtest.
        const float BuildingScale = 1.5f;
        static IReadOnlyList<TownBuilding> CreateTownBuildings() => new[]
        {
            new TownBuilding("bell_tower", TownCenter.X,        TownCenter.Y + 15f,  0.0f,  BuildingScale), // (0, 33)  far N landmark
            new TownBuilding("house_1",    TownCenter.X + 14f,  TownCenter.Y + 10f, -2.2f,  BuildingScale), // (14, 28) NE
            new TownBuilding("house_2",    TownCenter.X - 14f,  TownCenter.Y + 10f,  2.2f,  BuildingScale), // (-14, 28) NW
            new TownBuilding("inn",        TownCenter.X + 17f,  TownCenter.Y - 2f,   0.0f,  BuildingScale), // (17, 16) E flank
            new TownBuilding("house_3",    TownCenter.X - 17f,  TownCenter.Y,       -0.7f,  BuildingScale), // (-17, 18) W flank
            new TownBuilding("well",       TownCenter.X + 10f,  TownCenter.Y - 11f,  0.0f,  BuildingScale), // (10, 7)  SE, small
            new TownBuilding("blacksmith", TownCenter.X - 11f,  TownCenter.Y - 11f,  0.9f,  BuildingScale), // (-11, 7) SW, clear of lake
        };

        Scene3D _scene = null!;
        Texture2D _white = null!;
        DpiFont _hud = null!;

        // Guards OnExit against running before OnEnter has built the per-enter state (and OnEnter against
        // leftover state from a previous visit), so the room can be entered and exited repeatedly against the
        // shared Scene3D without doubling meshes/statics or leaking GPU/physics resources.
        bool _built;

        TerrainField _field = null!;
        TerrainCollision _terrain = null!;
        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        // One uploaded mesh per prop-kit id. The streamer scatters per-chunk props from these on demand.
        readonly Dictionary<string, MeshHandle> _propMeshes = new();

        // Hand-placed town buildings (CC0 Quaternius Medieval Village): one uploaded mesh per building id, plus
        // the fixed placement list built once in OnEnter. Not streamed - drawn directly every frame like the
        // platform/textured-prop fixtures, since there are only 7 and they anchor the clearing.
        readonly Dictionary<string, MeshHandle> _buildingMeshes = new();
        readonly List<PropPlacement> _buildingPlacements = new();

        Scene3DChunkSink _sink = null!;
        TerrainStreamer _streamer = null!;

        // Prop collision proxies (authored mesh-baked .coll, imported from Ruinborne) keyed by prop id, and the
        // scatter rule - both retained so the F2 overlay can recompute the streamed tree/rock proxies for the
        // currently-loaded ring (see RebuildCollisionOverlay).
        IReadOnlyDictionary<string, PhysicsShape> _collisionShapes = null!;
        ScatterConfig _scatterConfig = null!;

        // Physics world shared by the chunk sink (adds/removes prop statics on stream load/unload) and
        // CharacterController3D (resolves the capsule against those statics).
        BepuPhysicsWorld _physics = null!;

        CharacterController3D _character = null!;
        MeshHandle _capsule;

        // Hand-placed visible platform box, and its matching static collider.
        MeshHandle _platformMesh;
        Matrix4x4 _platformXform;

        // Textured prop demo: a procedural mossy-stone block (albedo + normal), no binary asset.
        MeshHandle _texturedProp;
        Matrix4x4 _texturedPropXform;

        // Collision-shape debug overlay (F2): translucent proxy meshes over the real collision (town buildings +
        // the streamed tree/rock statics of the loaded ring), plus a legend panel while it is on. Reuses the
        // room's injected _white/_hud rather than owning its own font/texture. _overlayStatics holds only the
        // fixed (non-streamed) statics; the streamed proxies are added per-rebuild from the loaded chunk ring.
        List<CollisionStatic> _overlayStatics = null!;
        CollisionShapeOverlay _collisionOverlay = null!;
        OverlayLegend _legend = null!;
        // The loaded-chunk set the overlay proxies were last built for, so RebuildCollisionOverlay only re-uploads
        // when the ring actually changed (a chunk crossing), not every frame.
        readonly HashSet<ChunkCoord> _overlayBuiltChunks = new();

        // Turnkey animated character: CharacterAvatar (KhaozEngine.Game.Render3D) composes the controller + animation
        // + collision-robust facing + draw, so the room carries none of that glue. Null while the rig asset is
        // missing/unreadable, in which case the room falls back to drawing the greybox capsule.
        CharacterAvatar? _avatar;

        // Index into Palettes.All for the P palette-cycle toggle (see OnUpdate). 2 = Ember8, matching
        // PixelPostProcessSettings.ActivePalette's own default so entering the room and pressing P once
        // steps to the same place Render3DSample does.
        int _palIdx = 2;

        public Room3D Init(Scene3D scene, Texture2D white, DpiFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // Sample the natural ground at the town centre BEFORE flattening (a throwaway field over the
            // un-flattened preset), so the plateau's target height meets the surrounding ground smoothly.
            float townHeight = new TerrainField(TerrainPresets.BoundedClearing()).SampleHeight(TownCenter.X, TownCenter.Y);

            var terrainConfig = TerrainPresets.BoundedClearing();
            var features = new List<ITerrainFeature>(terrainConfig.Features!)
            {
                new FlattenFeature(TownCenter.X, TownCenter.Y, TownRadius, townHeight, TownBlend),
            };
            terrainConfig.Features = features.ToArray();
            _field = new TerrainField(terrainConfig);
            _terrain = new TerrainCollision(_field);

            // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6). Mesh bottom sits at y=0 in local space.
            _capsule = _scene.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

            // --- Physics world setup ----------------------------------------------------------
            // Prop collision uses the authored, mesh-baked .coll proxies imported from Ruinborne (thin trunk
            // cylinders for trees, hulls for rocks), loaded headless by filename id via
            // PropCollisionFormat.LoadDirectory - the same shapes Ruinborne collides + predicts against, so the F2
            // overlay draws the same trunk cylinders rather than an auto-baked hull. The shapes dictionary is
            // passed to Scene3DChunkSink, which on each chunk load/unload adds/removes the per-placement statics so
            // the character collides against exactly the props in the currently-loaded ring.
            _physics = new BepuPhysicsWorld();
            string propsDir = Path.Combine(AppContext.BaseDirectory, "assets", "props");
            _collisionShapes = PropCollisionFormat.LoadDirectory(propsDir);

            // Load the committed CC0 prop kit through the asset pipeline (decompressed glTF -> normalized to its
            // manifest heightMeters with the origin dropped to the base), one uploaded mesh per id.
            AssetManifest manifest = AssetManifest.Load(Path.Combine(propsDir, "props.manifest.json"));
            foreach (AssetEntry entry in manifest.Props)
                _propMeshes[entry.Id] = _scene.LoadMesh(PropLoader.LoadProp(entry));

            // Load the CC0 town buildings (Quaternius Medieval Village) through the same asset pipeline as the
            // forest props, one uploaded mesh per id, and build their fixed world placements. Buildings are not
            // streamed - they are hand-placed once and drawn every frame like the platform/textured-prop fixtures.
            // (Their collision is added from the baked .coll per id below, so the character cannot walk through them.)
            string bManifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "buildings", "buildings.manifest.json");
            AssetManifest buildings = AssetManifest.Load(bManifestPath);
            foreach (AssetEntry e in buildings.Props)
                _buildingMeshes[e.Id] = _scene.LoadMesh(PropLoader.LoadProp(e));
            foreach (TownBuilding b in CreateTownBuildings())
                _buildingPlacements.Add(new PropPlacement(b.Id, b.X, _terrain.GroundHeight(b.X, b.Z), b.Z, b.Scale, b.Yaw, variant: 0));

            // Character spawns on the ground at the origin. CapsuleRadius matches the visible greybox capsule so
            // the collision footprint lines up with what's drawn.
            _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
            _character.SetXZ(0f, 0f);
            _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight, _terrain.GroundNormal, _physics);

            // Wrap the controller in a CharacterAvatar: it skinned-ingests the committed Quaternius Universal CC0 rig,
            // maps its clips, scales it to the capsule, and from here owns the facing + animation + draw. Null on any
            // load failure, so the room keeps the greybox capsule.
            _avatar = CharacterAvatar.TryLoadGltf(_scene,
                Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb"), _character,
                onFailure: reason => Console.WriteLine($"Character load failed ({reason}); falling back to the capsule."));
            if (_avatar is not null)
                Console.WriteLine($"Animated character loaded (scale {_avatar.ModelScale:0.00}).");

            var terrainMaterial = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
            _camera.Distance = 9f;
            _camera.Occlusion = _physics;   // spring-arm: pull the eye in through a wall/roof rather than clip it
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // Outline post-process starts OFF in this room (press O to toggle it on); OnExit restores the shared
            // PixelPostProcessSettings default (on) for the menu / other rooms.
            _scene.Post.Outline = false;

            // A hand-placed visible platform box in the clearing so there is something to look at.
            const float platformHeight = 1.0f;
            var platformCenter = new Vector2(0f, 12f);
            var platformHalf = new Vector2(3f, 2.5f);
            float platformBaseY = _terrain.GroundHeight(platformCenter.X, platformCenter.Y);
            _platformMesh = _scene.LoadMesh(MeshPrimitives.Box(1f));
            _platformXform = Matrix4x4.CreateScale(2f * platformHalf.X, platformHeight, 2f * platformHalf.Y)
                             * Matrix4x4.CreateTranslation(platformCenter.X, platformBaseY + platformHeight * 0.5f, platformCenter.Y);

            // Matching static collider so the player can stand on / not walk through the platform.
            // BoxShape takes half-extents. ShapeFactory doubles them to Bepu full extents (3 -> 6).
            // The platform never unloads, so the handle is not retained.
            _physics.AddStatic(
                new BoxShape(new Vector3(platformHalf.X, platformHeight * 0.5f, platformHalf.Y)),
                Pose.At(new Vector3(platformCenter.X, platformBaseY + platformHeight * 0.5f, platformCenter.Y)));

            // Textured prop demo: a procedural mossy-stone block (albedo + normal), no binary asset.
            // Placed near spawn at (3, 3), clear of the platform (0, 12).
            // ScaleUv tiles the material 3x across each face so texels stay dense (crisp, not one stretched copy).
            _texturedProp = _scene.LoadMesh(
                MeshOps.WithTangents(MeshOps.ScaleUv(MeshPrimitives.Box(1.5f), 3f)),
                PropMaterialPresets.Procedural());
            float propX = 3f, propZ = 3f;
            _texturedPropXform = Matrix4x4.CreateTranslation(propX, _terrain.GroundHeight(propX, propZ) + 0.75f, propZ);

            // --- Collision-shape debug overlay (F2) ---------------------------------------------
            // Fixed (non-streamed) collision statics the overlay draws translucent proxies over. Town buildings
            // are appended just below; the streamed tree/rock proxies are added per-rebuild from the loaded chunk
            // ring (RebuildCollisionOverlay), matching Ruinborne's overlay.
            _overlayStatics = new List<CollisionStatic>();

            // Building collision: load the baked .coll per building id (offline-baked by the proxy-authoring tool
            // in the KECL format) and add each as a scaled static at its placement pose, so the 7 town buildings
            // are solid instead of walk-through. Mirrors Ruinborne's RuinbornePhysics.AddPlacement (LoadDirectory
            // + PhysicsShapeScale.Uniform + AddStatic). Also registered in _overlayStatics so F2 shows the building
            // collision proxies too.
            string buildingCollDir = Path.Combine(AppContext.BaseDirectory, "assets", "buildings");
            IReadOnlyDictionary<string, PhysicsShape> buildingShapes = PropCollisionFormat.LoadDirectory(buildingCollDir);
            foreach (TownBuilding b in CreateTownBuildings())
            {
                if (!buildingShapes.TryGetValue(b.Id, out PhysicsShape? shape)) continue;
                PhysicsShape scaled = PhysicsShapeScale.Uniform(shape, b.Scale);
                var bPose = new Pose(new Vector3(b.X, _terrain.GroundHeight(b.X, b.Z), b.Z),
                                      Quaternion.CreateFromYawPitchRoll(b.Yaw, 0f, 0f));
                _physics.AddStatic(scaled, bPose);
                _overlayStatics.Add(new CollisionStatic(scaled, bPose));
            }

            // The proxy meshes + legend are built lazily on first F2-enable and rebuilt when the loaded ring
            // changes (see OnUpdate / RebuildCollisionOverlay), so no proxy meshes are uploaded unless the overlay
            // is actually shown.
            _collisionOverlay = new CollisionShapeOverlay();
            _legend = new OverlayLegend();

            // Exclude trees from the town: reuse ForestRing's defaults but hole out the flattened plateau so no
            // tree spawns on the levelled ground the buildings sit on. Retained on the room (_scatterConfig) so the
            // overlay rebuild can regenerate the same placements the sink scattered.
            _scatterConfig = ScatterConfig.ForestRing();
            _scatterConfig.ClearingRadius = TownRadius;
            _scatterConfig.ClearingCenter = TownCenter;

            _sink = new Scene3DChunkSink(_scene, _field, _scatterConfig, _propMeshes,
                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial,
                ownsMaterial: true, physics: _physics, collisionShapes: _collisionShapes);
            _streamer = new TerrainStreamer(StreamerConfig.Default, _sink);

            // Prime the FULL initial ring at load time (this is the loading moment, not a frame, so the per-frame
            // MaxLoadsPerFrame budget is irrelevant here): pump until the loaded set stops growing.
            int loadedBefore = -1;
            while (_streamer.Loaded.Count != loadedBefore)
            {
                loadedBefore = _streamer.Loaded.Count;
                _streamer.Update(_character.Position, 0f);
            }
            // Step the physics world once after the initial ring loads so Bepu's broad phase is current.
            _physics.Step(1f / 30f);

            _built = true;
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            // --- rendering consult: live A/B of the outline knobs ---
            var post = _scene.Post;
            if (Manager!.Input.WasPressed(Key.M))
            {
                post.RenderScale = post.RenderScale == RenderScale.MatchViewport
                    ? RenderScale.FixedInternal : RenderScale.MatchViewport;
                Console.WriteLine($"[post] RenderScale = {post.RenderScale}");
            }
            if (Manager!.Input.WasPressed(Key.O))
            {
                post.Outline = !post.Outline;
                Console.WriteLine($"[post] Outline = {post.Outline}");
            }
            if (Manager!.Input.WasPressed(Key.L)) { post.OutlineDepthThreshold = MathF.Min(2f, post.OutlineDepthThreshold + 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.K)) { post.OutlineDepthThreshold = MathF.Max(0f, post.OutlineDepthThreshold - 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.H)) { post.OutlineNormalThreshold = MathF.Min(2f, post.OutlineNormalThreshold + 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.G)) { post.OutlineNormalThreshold = MathF.Max(0f, post.OutlineNormalThreshold - 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }

            // Starfield background (A) and cel-shading bands (C), matching Render3DSample's A and C handlers.
            if (Manager!.Input.WasPressed(Key.A)) { post.Starfield = !post.Starfield; Console.WriteLine($"[post] Starfield = {post.Starfield}"); }
            if (Manager!.Input.WasPressed(Key.C)) { post.CelBands = post.CelBands == 0 ? 4 : 0; Console.WriteLine($"[post] CelBands = {post.CelBands}"); }

            // Retro combo (R): toggles quantize+dither+pixelated together, cel bands, and the internal render
            // resolution, matching Render3DSample's R handler exactly.
            if (Manager!.Input.WasPressed(Key.R))
            {
                bool on = !post.Quantize;
                post.Quantize = post.Dither = post.Pixelated = on;
                post.CelBands = on ? 4 : 0;
                post.RenderWidth = on ? 320 : 1920; post.RenderHeight = on ? 180 : 1080;
                Console.WriteLine($"[post] Retro = {on}");
            }
            // Palette cycle (P): steps ActivePalette through Palettes.All, matching Render3DSample's P handler.
            if (Manager!.Input.WasPressed(Key.P))
            {
                _palIdx = (_palIdx + 1) % Palettes.All.Length;
                post.ActivePalette = Palettes.All[_palIdx];
                Console.WriteLine("[post] palette: " + post.ActivePalette.Name);
            }

            if (Manager!.Input.WasPressed(Key.F2))
            {
                _collisionOverlay.Enabled = !_collisionOverlay.Enabled;
                Console.WriteLine($"[overlay] Collision shape overlay = {_collisionOverlay.Enabled}");
            }

            // Physics world ticks once per frame before movement so newly-streamed props are registered.
            _physics.Step(dt);

            // Drive the character. The avatar (when the rig loaded) owns the movement + collision-robust facing +
            // animation in a single call; without it, drive the controller alone for the greybox capsule.
            if (_avatar is not null)
                _avatar.Update(Manager!.Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal, _physics);
            else
                _character.Update(Manager!.Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal, _physics);

            // Stream the world around the new player position (loads/unloads/re-LODs within MaxLoadsPerFrame).
            _streamer.Update(_character.Position, dt);

            // Keep the F2 overlay's streamed tree/rock proxies in sync with the loaded ring, but only while it is
            // shown (each rebuild uploads a proxy mesh per static) and only when the ring actually changed (a chunk
            // crossing), not every frame. First enable finds an empty _overlayBuiltChunks, so it builds immediately.
            if (_collisionOverlay.Enabled && !_overlayBuiltChunks.SetEquals(_streamer.Loaded))
                RebuildCollisionOverlay();

            // Target the avatar's presentation position (physics XZ + smoothed height) so the camera glides on stairs.
            _camera.Target = _avatar?.RenderPosition ?? _character.Position;
            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            _sink.Draw(_character.Position);

            // The hand-placed visible platform.
            scene.Draw(_platformMesh, _platformXform, new Color(0.62f, 0.6f, 0.66f, 1f));

            // Textured prop demo: procedural mossy-stone block (albedo + normal maps).
            scene.Draw(_texturedProp, _texturedPropXform, Color.White);

            // The hand-placed town buildings (not streamed, always in range at this draw radius).
            scene.DrawProps(_buildingPlacements, _buildingMeshes, _character.Position, BuildingDrawRadius);

            // Draw the character. The avatar draws its skinned mesh at the feet with the right facing/scale; without
            // it, draw the greybox capsule (Position is the capsule centre, feet = centre - half).
            if (_avatar is not null)
            {
                _avatar.Draw(scene);
            }
            else
            {
                Vector3 p = _character.Position;
                float footY = p.Y - CapsuleHalfHeight;
                scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, footY, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
            }

            // Translucent collision-shape proxies over the real collision (no-op while disabled).
            _collisionOverlay.Draw(scene);
        }

        // The collision-legend overlay draws through the point-space UI pass, so its text is crisp on HiDPI.
        public override void OnDrawUi(SpriteBatch batch)
        {
            var ui = Manager!.UiViewport;
            if (ui is null) return;
            if (_collisionOverlay.Enabled)
                _legend.Draw(batch, _hud.For(ui.DpiScale), _white, ui.DesignBounds);
        }

        // Maps the overlay's present shape kinds through its palette into legend rows (swatch + name).
        static IReadOnlyList<LegendEntry> BuildLegendEntries(CollisionShapeOverlay overlay)
        {
            var list = new List<LegendEntry>();
            foreach (CollisionShapeKind kind in overlay.PresentKinds)
                list.Add(new LegendEntry(overlay.Palette.For(kind), overlay.Palette.NameFor(kind)));
            return list;
        }

        // Rebuilds the F2 overlay's proxy set from the fixed statics (town buildings) plus the tree/rock collision
        // statics of every currently-loaded chunk, so streamed props show the same translucent trunk-cylinder /
        // hull proxies Ruinborne draws. Each streamed prop's scaled shape + world pose are computed exactly as the
        // streamer's ChunkStatics.AddAll builds them (PropScatter.Generate over the chunk area -> per-placement
        // PhysicsShapeScale.Uniform + a yaw-only pose at the placement's baked ground Y), so the proxies line up
        // with the real collision. Records the loaded ring it built for so OnUpdate only re-runs on a chunk crossing.
        void RebuildCollisionOverlay()
        {
            var statics = new List<CollisionStatic>(_overlayStatics);
            foreach (ChunkCoord coord in _streamer.Loaded)
            {
                RectArea area = ChunkGrid.AreaOf(coord, TerrainChunkRegion.DefaultSize);
                foreach (PropPlacement p in PropScatter.Generate(_field, _scatterConfig, area))
                {
                    if (!_collisionShapes.TryGetValue(p.Id, out PhysicsShape? shape)) continue;
                    PhysicsShape scaled = PhysicsShapeScale.Uniform(shape, p.Scale);
                    var pose = new Pose(new Vector3(p.X, p.Y, p.Z), Quaternion.CreateFromAxisAngle(Vector3.UnitY, p.Yaw));
                    statics.Add(new CollisionStatic(scaled, pose));
                }
            }
            _collisionOverlay.Build(_scene, statics);
            _legend.SetEntries(BuildLegendEntries(_collisionOverlay));

            _overlayBuiltChunks.Clear();
            foreach (ChunkCoord coord in _streamer.Loaded) _overlayBuiltChunks.Add(coord);
        }

        // Tears down everything OnEnter built into the shared Scene3D, so the menu/2D rooms render cleanly and a
        // re-entry rebuilds from scratch. Guarded so an early exit (before OnEnter finished) is a safe no-op, and
        // idempotent-safe on the disposables themselves (TerrainStreamer.Dispose/CollisionShapeOverlay.Dispose are
        // written to tolerate repeat calls).
        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            // TerrainStreamer.Dispose flushes the loaded ring through the sink (freeing every chunk mesh) and then
            // disposes the sink itself (it owns Scene3DChunkSink), matching TerrainWalkSample's turn-key teardown.
            // Do not also dispose _sink separately, that would double-dispose it.
            _streamer.Dispose();
            _collisionOverlay.Dispose();
            _physics.Dispose();

            // Free the meshes this room uploaded straight into the shared Scene3D (the streamer freed only the
            // streamed chunk meshes + its owned splat material). Without this each re-entry would leak a fresh copy
            // of the capsule, platform, textured block, character, and prop-kit meshes onto the shared scene.
            _scene.UnloadMesh(_capsule);
            _scene.UnloadMesh(_platformMesh);
            _scene.UnloadMesh(_texturedProp);
            foreach (MeshHandle h in _propMeshes.Values) _scene.UnloadMesh(h);
            foreach (MeshHandle h in _buildingMeshes.Values) _scene.UnloadMesh(h);
            if (_avatar is not null) _scene.UnloadSkinnedMesh(_avatar.Mesh);

            // Drop the follow camera so the default camera returns for the menu/2D rooms.
            _scene.CameraOverride = null;

            // Reset every Post field this room's OnUpdate can mutate, back to PixelPostProcessSettings's own
            // defaults (Post has no setter, it is a shared instance owned by Scene3D, so fields are reset
            // individually rather than reassigning the property).
            var post = _scene.Post;
            post.RenderScale = RenderScale.FixedInternal;
            post.Outline = true;
            post.OutlineDepthThreshold = 0.2f;
            post.OutlineNormalThreshold = 0.45f;
            post.Starfield = true;

            // Retro combo + palette (R/P in OnUpdate above) back to PixelPostProcessSettings's own defaults, so
            // leaving the room never bleeds a low-res/quantized/palette-swapped look under the menu or 2D rooms.
            post.Quantize = false;
            post.Dither = false;
            post.Pixelated = false;
            post.CelBands = 0;
            post.RenderWidth = 1600;
            post.RenderHeight = 900;
            post.ActivePalette = Palettes.Ember8;
            _palIdx = 2;

            // Null the per-enter fields so a re-entered room rebuilds fresh rather than reusing stale handles.
            _propMeshes.Clear();
            _buildingMeshes.Clear();
            _buildingPlacements.Clear();
            _sink = null!;
            _streamer = null!;
            _physics = null!;
            _collisionOverlay = null!;
            _overlayStatics = null!;
            _legend = null!;
            _collisionShapes = null!;
            _scatterConfig = null!;
            _overlayBuiltChunks.Clear();
            _avatar = null;
        }
    }
}
