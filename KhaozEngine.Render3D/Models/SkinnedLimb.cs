using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Turn-key procedural limb: a single stateful component that owns a tube <see cref="SkinnedMeshHandle"/>
    /// plus its reusable motion buffers and drives the whole tentacle / cable / tail pipeline
    /// (<see cref="SkinnedMeshBuilder.BuildTube"/> -&gt; <see cref="ProceduralChainSolver"/> -&gt;
    /// <see cref="PolylineFrames"/> -&gt; <see cref="Scene3D.DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>) so a game stands a limb up in two calls:
    /// <c>new SkinnedLimb(scene, ...)</c> then per-frame <c>Update(...)</c> + <c>Draw(...)</c>.
    ///
    /// <para>The motion math (solve -&gt; frames -&gt; bones) is pure and GPU-free: it runs against caller-owned
    /// scratch buffers with no per-frame allocation, and is fully headless-testable via
    /// <see cref="CreateHeadless"/> (a limb with no GPU mesh whose <see cref="Update(Vector3,Vector3,Vector3,float)"/>,
    /// <see cref="Bones"/> and <see cref="Spine"/> all work). Only the tube upload and
    /// <see cref="Draw(Scene3D,Matrix4x4,Color)"/> touch the GPU.</para>
    ///
    /// <para>Presentation only - the bone/spine state is animation output; never feed it back into sim, RNG, or
    /// netcode.</para></summary>
    public sealed class SkinnedLimb : IDisposable
    {
        readonly Scene3D? _scene;          // null only for a headless (GPU-less) limb
        readonly Axis _axis;
        readonly Vector3[] _spine;         // reused every Update; one point per bone
        readonly Matrix4x4[] _bones;       // reused every Update; one world transform per bone
        SkinnedMeshHandle _handle;         // default for a headless limb
        bool _disposed;

        /// <summary>Tuning for the writhe / reach solve. Mutable so a game can retune the idle motion at runtime
        /// (e.g. ramp <see cref="ChainConfig.WritheAmplitude"/> as a boss enrages) without rebuilding the limb.</summary>
        public ChainConfig Config;

        /// <summary>Build a tube limb and upload it to <paramref name="scene"/>. <paramref name="boneCount"/> sets both
        /// the rig and the spine length (one point/bone per joint). The tube runs along <paramref name="axis"/> with
        /// <paramref name="radius"/> / <paramref name="length"/> / ring + radial segment counts as per
        /// <see cref="SkinnedMeshBuilder.BuildTube"/>. The mesh is untextured (vertex colour * tint).</summary>
        public SkinnedLimb(Scene3D scene, float radius, float length, int ringSegments, int radialSegments,
                           int boneCount, in ChainConfig config, Axis axis = Axis.Z)
            : this(scene, BuildAndLoad(scene, radius, length, ringSegments, radialSegments, boneCount, axis, default, useMaps: false),
                   boneCount, axis, config)
        {
        }

        /// <summary>As the untextured ctor, but binds <paramref name="texture"/> as the tube's albedo.</summary>
        public SkinnedLimb(Scene3D scene, float radius, float length, int ringSegments, int radialSegments,
                           int boneCount, in ChainConfig config, Scene3D.TextureHandle texture, Axis axis = Axis.Z)
            : this(scene, BuildAndLoad(scene, radius, length, ringSegments, radialSegments, boneCount, axis,
                       new Scene3D.SurfaceMaps(texture), useMaps: true),
                   boneCount, axis, config)
        {
        }

        /// <summary>As the untextured ctor, but binds a full PBR-lite <paramref name="maps"/> set (albedo + optional
        /// normal + optional roughness) to the tube.</summary>
        public SkinnedLimb(Scene3D scene, float radius, float length, int ringSegments, int radialSegments,
                           int boneCount, in ChainConfig config, Scene3D.SurfaceMaps maps, Axis axis = Axis.Z)
            : this(scene, BuildAndLoad(scene, radius, length, ringSegments, radialSegments, boneCount, axis, maps, useMaps: true),
                   boneCount, axis, config)
        {
        }

        SkinnedLimb(Scene3D? scene, SkinnedMeshHandle handle, int boneCount, Axis axis, in ChainConfig config)
        {
            if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));
            _scene = scene;
            _handle = handle;
            _axis = axis;
            Config = config;
            _spine = new Vector3[boneCount];
            _bones = new Matrix4x4[boneCount];
            // Rest the bones along the run axis so Bones/Spine read sensibly before the first Update.
            Solve(Vector3.Zero, AxisVec(axis), DefaultUp(axis), 0f);
        }

        /// <summary>Construct a limb with NO GPU mesh: the writhe / reach / frames pipeline and the
        /// <see cref="Bones"/> / <see cref="Spine"/> readouts all work, but <see cref="Draw(Scene3D,Matrix4x4,Color)"/>
        /// is a no-op and <see cref="Dispose"/> frees nothing. Lets the motion plumbing be exercised and asserted
        /// headless, with no GPU device. Production code uses a real <see cref="Scene3D"/> ctor.</summary>
        public static SkinnedLimb CreateHeadless(int boneCount, in ChainConfig config, Axis axis = Axis.Z)
            => new SkinnedLimb(null, default, boneCount, axis, config);

        static SkinnedMeshHandle BuildAndLoad(Scene3D scene, float radius, float length, int ringSegments,
            int radialSegments, int boneCount, Axis axis, Scene3D.SurfaceMaps maps, bool useMaps)
        {
            if (scene is null) throw new ArgumentNullException(nameof(scene));
            var tube = SkinnedMeshBuilder.BuildTube(radius, length, ringSegments, radialSegments, boneCount, axis);
            return useMaps ? scene.LoadSkinnedMesh(tube, maps) : scene.LoadSkinnedMesh(tube);
        }

        /// <summary>Number of bones (= spine points) in the limb.</summary>
        public int BoneCount => _bones.Length;

        /// <summary>The axis the tube runs along.</summary>
        public Axis RunAxis => _axis;

        /// <summary>The GPU mesh handle (default for a headless limb).</summary>
        public SkinnedMeshHandle Handle => _handle;

        /// <summary>This frame's bone world transforms (model space), one per bone - exactly what
        /// <see cref="Draw(Scene3D,Matrix4x4,Color)"/> feeds to <see cref="Scene3D.DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>. Valid after the
        /// ctor and refreshed by every <c>Update</c>. The backing buffer is reused, so copy if you need to retain it
        /// past the next <c>Update</c>.</summary>
        public ReadOnlySpan<Matrix4x4> Bones => _bones;

        /// <summary>This frame's solved spine (one point per bone), pre-frame-orientation. Same reuse caveat as
        /// <see cref="Bones"/>.</summary>
        public ReadOnlySpan<Vector3> Spine => _spine;

        /// <summary>Writhe-only update: solve the idle writhe from <paramref name="root"/> /
        /// <paramref name="forward"/> / <paramref name="up"/> at <paramref name="clockSeconds"/>, then refresh
        /// <see cref="Bones"/>. No allocation.</summary>
        public void Update(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds)
        {
            ThrowIfDisposed();
            Solve(root, forward, up, clockSeconds);
        }

        /// <summary>Writhe + FABRIK reach update: as the writhe-only overload, then bend the limb toward
        /// <paramref name="target"/> by <paramref name="reachWeight"/> in [0,1] (0 = natural writhe tip, 1 = tip
        /// pulled onto the target, clamped to the limb's reach). No allocation.</summary>
        public void Update(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds,
                           Vector3 target, float reachWeight)
        {
            ThrowIfDisposed();
            ProceduralChainSolver.SolveReach(root, forward, up, clockSeconds, target, reachWeight, Config, _spine);
            PolylineFrames.BuildInto(_spine, _axis, NonParallelUp(up, forward), _bones);
        }

        void Solve(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds)
        {
            ProceduralChainSolver.Solve(root, forward, up, clockSeconds, Config, _spine);
            PolylineFrames.BuildInto(_spine, _axis, NonParallelUp(up, forward), _bones);
        }

        /// <summary>Queue the limb for drawing with this frame's <see cref="Bones"/>. A headless limb (or a disposed
        /// one) is a no-op.</summary>
        public void Draw(Scene3D scene, Matrix4x4 model, Color tint)
        {
            if (_disposed || _handle.Generation == 0) return;
            scene.DrawSkinned(_handle, _bones, model, tint);
        }

        /// <summary>As <see cref="Draw(Scene3D,Matrix4x4,Color)"/> with an explicit <paramref name="material"/>
        /// (emissive + specular).</summary>
        public void Draw(Scene3D scene, Matrix4x4 model, Color tint, Material material)
        {
            if (_disposed || _handle.Generation == 0) return;
            scene.DrawSkinned(_handle, _bones, model, tint, material);
        }

        /// <summary>Free the tube's GPU buffers (via <see cref="Scene3D.UnloadSkinnedMesh"/>) and mark the limb spent.
        /// Idempotent; a no-op for a headless limb.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_scene is not null && _handle.Generation != 0)
            {
                _scene.UnloadSkinnedMesh(_handle);
                _handle = default;
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SkinnedLimb));
        }

        // Frame builder needs an up that is not parallel to forward, else its basis degenerates; fall back to the
        // axis-default up when the caller's up is colinear with forward.
        static Vector3 NonParallelUp(Vector3 up, Vector3 forward)
        {
            Vector3 u = up.LengthSquared() < 1e-10f ? Vector3.UnitY : Vector3.Normalize(up);
            Vector3 f = forward.LengthSquared() < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(forward);
            if (MathF.Abs(Vector3.Dot(u, f)) > 0.999f)
                u = MathF.Abs(f.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            return u;
        }

        static Vector3 AxisVec(Axis a) => a switch
        {
            Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ
        };

        static Vector3 DefaultUp(Axis a) => a switch
        {
            // Up must not be parallel to the run axis.
            Axis.Y => Vector3.UnitZ, _ => Vector3.UnitY
        };
    }
}
