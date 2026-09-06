namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A named ocean-surface bundle applied in one call via <see cref="OceanPresets.Apply(OceanPresetKind, WaterSettings)"/>. Each value sets the
    /// same coherent group of swell, ripple, and foam/glint fields on <see cref="WaterSettings"/>, so a map editor
    /// or sample can offer a simple dropdown instead of tuning every knob by hand.
    /// </summary>
    public enum OceanPresetKind
    {
        /// <summary>Near-flat swell, sparse foam. A sheltered bay or a calm lake.</summary>
        Calm,

        /// <summary>Close to <see cref="WaterSettings"/>'s own defaults. The recommended map-editor default.</summary>
        Moderate,

        /// <summary>Heavy swell and dense whitecaps. A storm-tossed open sea.</summary>
        Rough,
    }
}
