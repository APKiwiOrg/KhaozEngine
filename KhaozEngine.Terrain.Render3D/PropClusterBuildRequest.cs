using System;
using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>Detached CPU inputs for one prop layer in one streamed world area.</summary>
    public sealed record PropClusterBuildRequest
    {
        public PropClusterKey Key { get; }
        public long Generation { get; }
        public RectArea Area { get; }
        public PropLayer Layer { get; }
        public IReadOnlyList<PropPlacement> Placements { get; }

        public PropClusterBuildRequest(PropClusterKey key, long generation, RectArea area, PropLayer layer,
                                       IReadOnlyList<PropPlacement> placements)
        {
            if (string.IsNullOrEmpty(key.LayerId))
                throw new ArgumentException("A cluster layer id is required.", nameof(key));
            ArgumentNullException.ThrowIfNull(placements);
            Key = key;
            Generation = generation;
            Area = area;
            Layer = layer;
            Placements = placements;
        }
    }
}
