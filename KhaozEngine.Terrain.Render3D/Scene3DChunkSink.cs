using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. Holds one or more <see cref="PropLayer"/>s - each scatter layer has its own
    /// <see cref="ScatterConfig"/>, mesh set, and draw radius (a dense ground-cover layer at a short radius can
    /// ride alongside the sparse tree layer at a long one), and each companion layer rings its host scatter
    /// layer's placements with foliage. <c>Load</c> builds the chunk mesh at the requested LOD
    /// (<see cref="TerrainChunkBuilder"/>) + scatters every layer for the chunk; <c>ReLod</c> rebuilds the mesh in
    /// place (props are LOD-independent, so they are kept); <c>Unload</c> frees the mesh; <c>Draw</c> queues every
    /// loaded chunk + each layer's in-range props (XZ-culled to that layer's draw radius) every frame. Companions
    /// attach to their host's chunk, so each host emits its companions exactly once (a host lives in one chunk),
    /// even when they spill geometrically into a neighbour. Ships in the package so every game gets streaming for
    /// free.</summary>
    public sealed class Scene3DChunkSink : IChunkSink
    {
        readonly Scene3D _scene;
        readonly TerrainField _field;
        readonly IReadOnlyList<PropLayer> _layers;
        readonly float _chunkSize;
        readonly Scene3D.SplatMaterialHandle _material;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();

        /// <summary>Multi-layer sink. Each <see cref="PropLayer"/> is a scatter layer or a companion layer; a
        /// companion layer's <see cref="PropLayer.HostLayerIndex"/> must point at a scatter layer in
        /// <paramref name="layers"/>.</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                                float chunkSize, Scene3D.SplatMaterialHandle material = default)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _layers = layers ?? throw new ArgumentNullException(nameof(layers));
            if (layers.Count == 0)
                throw new ArgumentException("At least one PropLayer is required.", nameof(layers));
            for (int i = 0; i < layers.Count; i++)
            {
                PropLayer l = layers[i];
                if (l.IsCompanion)
                {
                    if (l.HostLayerIndex < 0 || l.HostLayerIndex >= layers.Count)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion HostLayerIndex {l.HostLayerIndex} is out of range.", nameof(layers));
                    if (layers[l.HostLayerIndex].IsCompanion)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion host {l.HostLayerIndex} must be a scatter layer.", nameof(layers));
                }
                else if (l.Scatter == null)
                {
                    throw new ArgumentException($"PropLayer {i} has neither a Scatter nor a Companions config.", nameof(layers));
                }
            }
            _chunkSize = chunkSize;
            _material = material;
        }

        /// <summary>Single-layer sink (back-compat): one scatter config, one mesh set, one draw radius.</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius,
                                Scene3D.SplatMaterialHandle material = default)
            : this(scene, field,
                   new[]
                   {
                       PropLayer.ScatterLayer(
                           scatter ?? throw new ArgumentNullException(nameof(scatter)),
                           propMeshes ?? throw new ArgumentNullException(nameof(propMeshes)),
                           propDrawRadius),
                   },
                   chunkSize, material)
        {
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            /// <summary>One placement list per layer (scatter or derived companions), index-aligned to the sink's layers.</summary>
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            public int Lod;

            /// <summary>Back-compat alias: the first layer's placements.</summary>
            public IReadOnlyList<PropPlacement> Props =>
                LayerProps.Length > 0 ? LayerProps[0] : Array.Empty<PropPlacement>();
        }

        /// <summary>The deterministic placements for every layer of a chunk (pure; headless-testable). Scatter
        /// layers first, then companion layers derived from their host layer's placements for THIS chunk.</summary>
        internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
            var layers = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (!_layers[i].IsCompanion)
                    layers[i] = PropScatter.Generate(_field, _layers[i].Scatter!, area);
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].IsCompanion)
                    layers[i] = PropScatter.GenerateCompanions(_field, layers[_layers[i].HostLayerIndex], _layers[i].Companions!);
            return layers;
        }

        /// <summary>The first layer's placements for a chunk (back-compat for the single-layer path).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) => ScatterLayersFor(coord)[0];

        public object Load(ChunkCoord coord, int lod)
        {
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            var load = new ChunkLoad
            {
                Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh),
                LayerProps = ScatterLayersFor(coord),
                Lod = lod,
            };
            _loaded[coord] = load;
            return load;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            load.Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);
            load.Lod = lod;
            // Props are LOD-independent; keep load.LayerProps.
        }

        public void Unload(ChunkCoord coord, object handle)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and each layer's in-range props (XZ-culled to that layer's draw radius).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (ChunkLoad load in _loaded.Values)
            {
                _scene.DrawTerrainChunk(load.Mesh);
                for (int i = 0; i < _layers.Count; i++)
                    _scene.DrawProps(load.LayerProps[i], _layers[i].Meshes, focus, _layers[i].DrawRadius);
            }
        }
    }
}
