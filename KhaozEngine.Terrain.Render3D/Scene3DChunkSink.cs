using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. <c>Load</c> builds the chunk mesh at the requested LOD (<see cref="TerrainChunkBuilder"/>)
    /// + scatters the chunk's props (<see cref="PropScatter"/> over the chunk's half-open <see cref="RectArea"/>),
    /// uploads the mesh, and returns a mutable <see cref="ChunkLoad"/> holder. <c>ReLod</c> rebuilds the mesh at the
    /// new tier in place (props are LOD-independent, so they are kept). <c>Unload</c> frees the mesh. <c>Draw</c>
    /// queues every loaded chunk + its props within <c>propDrawRadius</c> (XZ) of the focus each frame. Ships in the
    /// package so every game gets streaming for free.</summary>
    public sealed class Scene3DChunkSink : IChunkSink
    {
        readonly Scene3D _scene;
        readonly TerrainField _field;
        readonly ScatterConfig _scatter;
        readonly IReadOnlyDictionary<string, MeshHandle> _propMeshes;
        readonly float _chunkSize;
        readonly float _propDrawRadius;
        readonly Scene3D.SplatMaterialHandle _material;
        readonly IPhysicsWorld? _physics;
        readonly IReadOnlyDictionary<string, PhysicsShape>? _collisionShapes;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();

        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius,
                                Scene3D.SplatMaterialHandle material = default,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _scatter = scatter ?? throw new ArgumentNullException(nameof(scatter));
            _propMeshes = propMeshes ?? throw new ArgumentNullException(nameof(propMeshes));
            _chunkSize = chunkSize;
            _propDrawRadius = propDrawRadius;
            _material = material;
            _physics = physics;
            _collisionShapes = collisionShapes;
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            public IReadOnlyList<PropPlacement> Props = Array.Empty<PropPlacement>();
            public int Lod;
            /// <summary>Static body handles added for this chunk's props; empty when no physics world is wired.</summary>
            public List<StaticHandle> Statics = new();
        }

        /// <summary>The deterministic prop placements for a chunk's area (pure; headless-testable).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) =>
            PropScatter.Generate(_field, _scatter, ChunkGrid.AreaOf(coord, _chunkSize));

        public object Load(ChunkCoord coord, int lod)
        {
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            var load = new ChunkLoad
            {
                Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh),
                Props = ScatterFor(coord),
                Lod = lod,
            };
            _loaded[coord] = load;
            if (_physics is not null && _collisionShapes is not null)
                ChunkStatics.AddAll(_physics, _collisionShapes, load.Props, load.Statics);
            return load;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            load.Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);
            load.Lod = lod;
            // Props are LOD-independent; keep load.Props.
        }

        public void Unload(ChunkCoord coord, object handle)
        {
            var load = (ChunkLoad)handle;
            if (_physics is not null)
                ChunkStatics.RemoveAll(_physics, load.Statics);
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and its in-range props (XZ-culled to propDrawRadius of focus).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (ChunkLoad load in _loaded.Values)
            {
                _scene.DrawTerrainChunk(load.Mesh);
                _scene.DrawProps(load.Props, _propMeshes, focus, _propDrawRadius);
            }
        }
    }
}
