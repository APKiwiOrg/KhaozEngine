using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>The props-only half of <see cref="Scene3DChunkSink"/>'s two refresh seams. <see cref="Refresh"/>
    /// serves <see cref="IChunkPlacementRefreshSink"/>: it re-serves a loaded chunk's live placement-source layers,
    /// plus every companion layer hosted by one, and republishes only those layers' prop clusters.
    /// <see cref="Republish"/> is the shared second half, and on its own it serves
    /// <see cref="IChunkPropRefreshSink"/> with every layer's placements freshly computed by the sink. The terrain
    /// mesh, the terrain collider and dynamics are never read or written here, so a placement or generation-config
    /// edit costs placement work and a cluster swap instead of a chunk re-mesh.
    /// <para>For a placement edit, frozen-list placement layers and scatter layers are left alone: their placements
    /// are fixed at construction or determined by the field and the captured configs, so only a field swap
    /// (<see cref="TerrainStreamer.Invalidate(ChunkCoord)"/>) or a config swap followed by
    /// <see cref="TerrainStreamer.RefreshProps"/> changes them. Runs on the frame thread, like
    /// <see cref="Scene3DChunkSink.Apply"/>, because an HLOD layer's fresh merged mesh is uploaded here.</para>
    /// </summary>
    internal static class ChunkPlacementRefresh
    {
        /// <summary>The layers a placement refresh re-serves: every live-source placement layer and every companion
        /// layer whose host is one. Empty when the sink has no live source.</summary>
        internal static List<int> RefreshedLayers(IReadOnlyList<PropLayer> layers)
        {
            var refreshed = new List<int>();
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].PlacementSource is not null
                    || (layers[i].IsCompanion && layers[layers[i].HostLayerIndex].PlacementSource is not null))
                    refreshed.Add(i);
            return refreshed;
        }

        /// <summary>Re-serves <paramref name="load"/>'s live-source layers at its own ring and republishes their
        /// clusters (see <see cref="Republish"/>). Returns true when a refreshed gameplay layer registers colliders,
        /// so the caller rebuilds the chunk's prop statics from the adopted placements.</summary>
        internal static bool Refresh(PropClusterRenderer clusters,
            ConcurrentDictionary<PropClusterKey, long> generations, float chunkSize, ChunkCoord coord,
            Scene3DChunkSink.ChunkLoad load, IReadOnlyList<PropLayer> layers, TerrainField field)
        {
            List<int> refreshed = RefreshedLayers(layers);
            if (refreshed.Count == 0) return false;
            RectArea area = ChunkGrid.AreaOf(coord, chunkSize);

            var fresh = new IReadOnlyList<PropPlacement>[layers.Count];
            foreach (int i in refreshed)
            {
                if (layers[i].PlacementSource is not { } source) continue;
                var into = new List<PropPlacement>();
                source.PlacementsIn(area, into);
                fresh[i] = into;
            }
            foreach (int i in refreshed)
                if (layers[i].IsCompanion)
                    fresh[i] = PropScatter.GenerateCompanions(field, fresh[layers[i].HostLayerIndex],
                        layers[i].Companions!);
            return Republish(clusters, generations, chunkSize, coord, load, layers, fresh, refreshed);
        }

        /// <summary>Adopts <paramref name="fresh"/> for the <paramref name="refreshed"/> layers (every layer when
        /// null, in which case <paramref name="fresh"/> must hold an entry per layer) and republishes their clusters
        /// through <paramref name="clusters"/>, bumping an HLOD layer's generation in <paramref name="generations"/>
        /// exactly as a rebuild in place does. A gameplay chunk adopts the fresh placements into a new
        /// <see cref="Scene3DChunkSink.ChunkLoad.LayerProps"/> array. A decor chunk carries no individual props, so
        /// only an HLOD layer's merged mesh is rebuilt there. Returns true when a refreshed gameplay layer registers
        /// colliders.</summary>
        internal static bool Republish(PropClusterRenderer clusters,
            ConcurrentDictionary<PropClusterKey, long> generations, float chunkSize, ChunkCoord coord,
            Scene3DChunkSink.ChunkLoad load, IReadOnlyList<PropLayer> layers, IReadOnlyList<PropPlacement>[] fresh,
            IReadOnlyList<int>? refreshed)
        {
            RectArea area = ChunkGrid.AreaOf(coord, chunkSize);
            bool gameplay = load.Ring == ChunkRing.Gameplay;
            if (gameplay)
            {
                var props = new IReadOnlyList<PropPlacement>[layers.Count];
                for (int i = 0; i < props.Length; i++)
                    props[i] = fresh[i]
                        ?? (i < load.LayerProps.Length ? load.LayerProps[i] : Array.Empty<PropPlacement>());
                load.LayerProps = props;
            }

            bool colliders = false;
            int count = refreshed?.Count ?? layers.Count;
            for (int n = 0; n < count; n++)
            {
                int i = refreshed?[n] ?? n;
                PropLayer layer = layers[i];
                if (!gameplay && !layer.HasHlod) continue;
                PropClusterKey key = Scene3DChunkSink.ClusterKey(coord, i);
                long generation = 0;
                if (layer.HasHlod)
                {
                    generation = generations.AddOrUpdate(key, 1, static (_, current) => current + 1);
                    clusters.Invalidate(key);
                }
                PropClusterCpuBuild build = clusters.BuildCpu(
                    new PropClusterBuildRequest(key, generation, area, layer, fresh[i]));
                if (!gameplay) build = build.WithPlacementBatch(Array.Empty<PropPlacement>());
                clusters.Apply(key, build);
                if (layer.HasHlod && load.HlodMeshHandles is { } handles) handles[i] = clusters.HandleOf(key);
                colliders |= gameplay && layer.RegisterColliders;
            }
            return colliders;
        }
    }
}
