using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>The tile-ground fragment programs, cheapest first. A mesh draws with the cheapest one that covers every
    /// layer its slots name, so ground that names no relief layer keeps the plain program's cost even inside a relief
    /// set, and only ground that names a hex-tiled layer pays for hex-tiling.</summary>
    internal enum TileGroundProgram : byte
    {
        /// <summary>TileGroundFrag, the albedo-only program.</summary>
        Plain = 0,
        /// <summary>TileGroundReliefFrag: surface maps, height blending and feather breakup.</summary>
        Relief = 1,
        /// <summary>TileGroundReliefHexFrag: the relief program with hex-tiling compiled in.</summary>
        ReliefHex = 2,
    }

    /// <summary>Which <see cref="TileGroundProgram"/> each layer, and so each mesh, needs. Pure, no GPU.</summary>
    internal static class TileGroundPrograms
    {
        /// <summary>Each layer's need. A layer's steep companion raises it to the companion's own need, because the
        /// companion's taps are taken wherever the layer's are.</summary>
        internal static TileGroundProgram[] ForLayers(IReadOnlyList<TileGroundLayerImage> layers)
        {
            var own = new TileGroundProgram[layers.Count];
            for (int i = 0; i < layers.Count; i++) own[i] = Own(layers[i]);
            var needs = new TileGroundProgram[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                needs[i] = own[i];
                if (layers[i].Steep is { } steep && steep.CompanionSlot >= 0 && steep.CompanionSlot < layers.Count
                    && own[steep.CompanionSlot] > needs[i])
                    needs[i] = own[steep.CompanionSlot];
            }
            return needs;
        }

        /// <summary>The program a mesh needs: the highest need of any slot any vertex names in any of its five lanes
        /// (the four corner slots in Uv.xy and Tangent.xy, the overlay slot plus one in Tangent.w), read as the
        /// fragment reads them, rounded and clamped to the set. A lane whose weight is zero still counts, which keeps
        /// the scan to the slots alone and can only choose a dearer program than needed, never a cheaper one.</summary>
        internal static TileGroundProgram ForMesh(ReadOnlySpan<ModelVertex> vertices, TileGroundProgram[] layers)
        {
            TileGroundProgram need = TileGroundProgram.Plain;
            int last = Math.Min(TileGroundMaterialConfig.MaxMaterials, layers.Length) - 1;
            if (last < 0) return need;
            foreach (ref readonly ModelVertex v in vertices)
            {
                need = Max(need, layers[Corner(v.Uv.X, last)]);
                need = Max(need, layers[Corner(v.Uv.Y, last)]);
                need = Max(need, layers[Corner(v.Tangent.X, last)]);
                need = Max(need, layers[Corner(v.Tangent.Y, last)]);
                if (v.Tangent.W > 0.5f) need = Max(need, layers[Math.Clamp((int)(v.Tangent.W - 0.5f), 0, last)]);
                if (need == TileGroundProgram.ReliefHex) break;
            }
            return need;
        }

        static TileGroundProgram Own(TileGroundLayerImage layer) =>
            layer.AntiTiling != TileGroundAntiTiling.None ? TileGroundProgram.ReliefHex
            : layer.SurfaceRgba is not null || layer.HeightMapped || layer.FeatherBreakup != 0f ? TileGroundProgram.Relief
            : TileGroundProgram.Plain;

        // The fragment's int(x + 0.5) then clamp: truncation toward zero, as C#'s cast does.
        static int Corner(float lane, int last) => Math.Clamp((int)(lane + 0.5f), 0, last);

        static TileGroundProgram Max(TileGroundProgram a, TileGroundProgram b) => a > b ? a : b;
    }
}
