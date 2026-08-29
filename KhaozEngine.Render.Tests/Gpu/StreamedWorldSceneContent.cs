using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE STREAMED-WORLD SCENE the measurement rows share, built to the shape a live Ruinborne client
    /// reports rather than to a shape that was convenient to write. It loads the meshes and issues the draws;
    /// it owns no device, no framebuffer and no command list, so a caller measures it through whatever
    /// instrument that caller needs.
    /// <para>
    /// THE SHAPE IS READ OFF THE GAME'S SHIPPED CONTENT. 447 resident chunk meshes, one merged HLOD cluster mesh
    /// per chunk, and the individual props that can exist inside the 240 m gameplay ring, plus a crowd of
    /// characters at the player mesh's own vertex and bone counts. The chunks and their clusters are DISTINCT
    /// mesh handles, because that is what makes a streamed world one run (and one draw call) per chunk rather
    /// than one big instanced run, and it is the property every count taken against this scene turns on.
    /// </para>
    /// <para>
    /// GEOMETRY IS DELIBERATELY LIGHT (the chunk and prop meshes are small primitives). Every number measured
    /// against this scene so far is a COUNT or a BYTE figure, both of which depend on instance, draw and vertex
    /// counts and not on triangle density. A row that wants a triangle-bound reading needs a different scene and
    /// should say so rather than reusing this one.
    /// </para>
    /// <para>
    /// Two rows read it today: <see cref="FrameUploadAttributionGpuTests"/> for the per-frame upload split, and
    /// <see cref="MetalRecordCostGpuTests"/> for rollout gate MM1's three record-time counts
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/566">#566</see>).
    /// </para>
    /// </summary>
    internal sealed class StreamedWorldSceneContent
    {
        /// <summary>Resident chunks: each is its own mesh, so each is its own run and its own draw.</summary>
        internal const int ChunkMeshes = 447;

        /// <summary>One merged HLOD cluster mesh per chunk (the tree layer).</summary>
        internal const int HlodMeshes = 447;

        /// <summary>Authored placements inside the 240 m gameplay ring, over shared meshes.</summary>
        internal const int PropInstances = 3000;

        /// <summary>Every rigid instance the full scene queues, chunks and clusters and props together.</summary>
        internal const int RigidInstances = ChunkMeshes + HlodMeshes + PropInstances;

        // Ruinborne's player mesh is 13,637 vertices (player_human_male.glb). BuildTube's vertex count is
        // (rings + 1) * radial, so 340 x 40 lands at 13,640: the same order, and exact enough that a per-character
        // cost measured through it is the real one.
        internal const int CharacterRings = 340, CharacterRadial = 40, CharacterBones = 48;

        /// <summary>Vertices per character, which is what the CPU-skinning stream is linear in.</summary>
        internal const int CharacterVertices = (CharacterRings + 1) * CharacterRadial;

        /// <summary>The shadow tier the live client runs.</summary>
        internal const int ShadowResolution = 2048, Cascades = 4;

        const float ChunkSize = 60f;

        readonly List<MeshHandle> _chunks = new();
        readonly List<MeshHandle> _hlod = new();
        readonly MeshHandle[] _props = new MeshHandle[4];
        readonly SkinnedMeshHandle _character;
        readonly Matrix4x4[] _palette;
        readonly List<Matrix4x4> _propWorlds = new();
        readonly List<Vector3> _chunkOrigins = new();

        /// <summary>Load every mesh this scene draws into <paramref name="scene"/>. One instance belongs to one
        /// scene: the handles it holds are that scene's.</summary>
        internal StreamedWorldSceneContent(Scene3D scene)
        {
            ArgumentNullException.ThrowIfNull(scene);

            int side = (int)MathF.Ceiling(MathF.Sqrt(ChunkMeshes));
            for (int i = 0; i < ChunkMeshes; i++)
            {
                int cx = i % side, cz = i / side;
                _chunkOrigins.Add(new Vector3((cx - side / 2) * ChunkSize, 0f, (cz - side / 2) * ChunkSize));
                _chunks.Add(scene.LoadMesh(MeshPrimitives.Tile(ChunkSize, 0.4f)));
            }

            for (int i = 0; i < HlodMeshes; i++)
                _hlod.Add(scene.LoadMesh(MeshPrimitives.RoundedBox(3f, 0.5f, 3)));

            _props[0] = scene.LoadMesh(MeshPrimitives.Box(0.8f));
            _props[1] = scene.LoadMesh(MeshPrimitives.Cone(0.5f, 2.2f, 6));
            _props[2] = scene.LoadMesh(MeshPrimitives.Sphere(1.6f, 10, 12));
            _props[3] = scene.LoadMesh(MeshPrimitives.RoundedBox(1.4f, 0.3f, 4));

            uint seed = 0x51AB_C0DE;
            float Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24); }
            for (int i = 0; i < PropInstances; i++)
            {
                float x = (Next() - 0.5f) * 480f, z = (Next() - 0.5f) * 480f;
                _propWorlds.Add(Matrix4x4.CreateRotationY(Next() * 6.28f) * Matrix4x4.CreateTranslation(x, 0f, z));
            }

            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(
                0.5f, 4f, CharacterRings, CharacterRadial, CharacterBones, Axis.Z);
            _character = scene.LoadSkinnedMesh(tube);
            _palette = (Matrix4x4[])tube.RestPose.Clone();
        }

        /// <summary>The live client's shadow tier, which every row measuring this scene builds its scene
        /// with.</summary>
        internal static ShadowSettings Shadows() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowMapResolution = ShadowResolution,
            ShadowCascadeCount = Cascades,
        };

        /// <summary>Point <paramref name="scene"/>'s camera at the world the way every row measuring it
        /// does, so two rows are looking at the same thing.</summary>
        internal static void FrameCamera(Scene3D scene)
        {
            ArgumentNullException.ThrowIfNull(scene);

            scene.Camera.Azimuth = 0.6f;
            scene.Camera.Elevation = 0.35f;
            scene.Camera.Frame(Vector3.Zero, new Vector3(70f, 25f, 70f));
        }

        /// <summary>
        /// One frame of the scene with <paramref name="characters"/> skinned draws, all placed inside the camera
        /// frustum so every one of them really is skinned (a culled draw skips its upload, which would make a
        /// sweep measure the cull instead of the stream), and <paramref name="chunks"/> of the resident chunk
        /// meshes drawn, which is how a caller reproduces streaming churn.
        /// </summary>
        internal void Draw(Scene3D scene, int characters, int chunks)
        {
            ArgumentNullException.ThrowIfNull(scene);

            for (int i = 0; i < chunks; i++)
                scene.Draw(_chunks[i], Matrix4x4.CreateTranslation(_chunkOrigins[i]),
                    new Color(0.5f, 0.52f, 0.46f, 1f));
            for (int i = 0; i < HlodMeshes; i++)
                scene.Draw(_hlod[i], Matrix4x4.CreateTranslation(_chunkOrigins[i] + new Vector3(0f, 1.5f, 0f)),
                    new Color(0.3f, 0.45f, 0.28f, 1f));
            for (int i = 0; i < _propWorlds.Count; i++)
                scene.Draw(_props[i & 3], _propWorlds[i], new Color(0.35f, 0.55f, 0.3f, 1f));
            for (int i = 0; i < characters; i++)
            {
                float a = i * 0.7f;
                var world = Matrix4x4.CreateTranslation(
                    MathF.Cos(a) * (2f + i * 0.35f), 1f, MathF.Sin(a) * (2f + i * 0.35f));
                scene.DrawSkinned(_character, _palette, world, Color.White);
            }
        }
    }
}
