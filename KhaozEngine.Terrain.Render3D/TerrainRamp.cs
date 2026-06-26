using KhaozEngine.Primitives;

namespace KhaozEngine.Terrain
{
    /// <summary>Maps splat weights to a vertex colour for the current (pre-PBR) terrain slice: a weighted blend of
    /// five greybox palette colours (matching make_clearing_greybox.py's height/slope ramp). Drop-in replaceable
    /// by PBR splat textures later.</summary>
    public static class TerrainRamp
    {
        public static readonly Color Grass = new(0.27f, 0.42f, 0.18f);
        public static readonly Color Dirt  = new(0.34f, 0.30f, 0.24f);
        public static readonly Color Rock  = new(0.44f, 0.42f, 0.40f);
        public static readonly Color Sand  = new(0.76f, 0.70f, 0.50f);
        public static readonly Color Snow  = new(0.93f, 0.94f, 0.96f);

        public static Color Of(in TerrainSplatWeights w) => new(
            Grass.R * w.Grass + Dirt.R * w.Dirt + Rock.R * w.Rock + Sand.R * w.Sand + Snow.R * w.Snow,
            Grass.G * w.Grass + Dirt.G * w.Dirt + Rock.G * w.Rock + Sand.G * w.Sand + Snow.G * w.Snow,
            Grass.B * w.Grass + Dirt.B * w.Dirt + Rock.B * w.Rock + Sand.B * w.Sand + Snow.B * w.Snow,
            1f);
    }
}
