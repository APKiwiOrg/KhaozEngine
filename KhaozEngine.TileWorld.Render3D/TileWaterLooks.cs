using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>The per-plane <see cref="WaterLook"/>s a tile world's bodies draw with. A look, not an
/// <c>OceanPreset</c>: a preset rewrites the scene-wide water settings, and a tile world's river has to be able
/// to sit in the same frame as whatever else the game's scene look already describes.</summary>
public static class TileWaterLooks
{
    /// <summary>A calm inland river or stream: off the shared FFT ocean, flat surface, glassy normals, no surf,
    /// foam only in the shoreline band, silty colours and a short depth blend so a 60 to 80 cm bed still darkens.
    /// <para>Modelled on Ruinborne's inland lake look, which is the engine's reference calm-water body. FOUR
    /// fields are that look unchanged, because they say "not a sea" rather than "not big":
    /// <see cref="WaterLook.WaveSource"/> procedural (which also drops shoaling and breaking surf, they ride the
    /// same gate), <see cref="WaterLook.SwellAmplitude"/> zero (a swell wavelength meant for a sea is
    /// undersampled by the outer grid rings and interpolates into a facet quilt, and zero drops the whole
    /// Gerstner term), <see cref="WaterLook.NormalStrength"/> at 0.05 (the ripple normal field is what is left
    /// once the swell is gone, and the sea's strength shades calm water like chop), and
    /// <see cref="WaterLook.SurfStrength"/> zero.</para>
    /// <para>The rest differ, and every one of them differs because of SCALE. A lake is a couple of hundred
    /// metres across and deep. A river is a few metres wide and less than a metre deep, so the sea's distances
    /// (a 1.6 m foam band, a 0.6 m waterline feather, a 2.5 m ripple wavelength, a 2.5 m depth blend) are all
    /// wider than the body they would be measured across, and each of them would consume the whole
    /// river.</para>
    /// <para><b>Shared and NOT to be mutated.</b> <see cref="WaterLook"/> is a class of public fields, so this
    /// one instance is handed to every plane of every world in the process. Copy it and change the copy, or pass
    /// a look of your own through <see cref="TileWorldViewOptions.WaterLook"/>.</para></summary>
    public static readonly WaterLook River = new()
    {
        // ---- Kept from the inland lake ------------------------------------------------------------------
        WaveSource = WaterWaveSource.Procedural,
        SwellAmplitude = 0f,
        NormalStrength = 0.05f,
        SurfStrength = 0f,

        // ---- Changed for a river ------------------------------------------------------------------------

        // The lake killed foam outright with FoamStrength 0, because it had no shoreline worth marking at that
        // size. A river is almost all shoreline, and a soft rim where the water meets the carved bank is what
        // stops the edge reading as a cut-out. So foam stays on, at less than half the sea's intensity.
        FoamStrength = 0.35f,

        // Whitecaps off explicitly. The zero swell already leaves them nothing to read, and saying so here keeps
        // the look honest for a caller who copies it and puts a little swell back.
        FoamCrestCoverage = 0f,

        // The band is measured as depth UNDER the surface, so the sea's 1.6 m would foam every pixel of a river
        // whose bed is 80 cm down. 35 cm foams the bank slope and nothing else.
        FoamShoreWidth = 0.35f,

        // Silt, not sea. The shallow colour is what most of a river shows, since most of it is shallow.
        ShallowColor = new Color(0.33f, 0.36f, 0.24f, 0.80f),

        // Set rather than inherited, deliberately, and the one addition to the design's list. The shallow colour
        // grades into the DEEP colour as the bed drops away, so leaving deep inherited would grade a silty brown
        // river into whatever blue the scene's sea uses over the 80 cm this look calls deep.
        DeepColor = new Color(0.10f, 0.13f, 0.10f, 0.90f),

        // The sea blends over 2.5 m, which a river never reaches, so an inherited value would leave the whole
        // body at its shallow colour and perfectly flat in tone. 0.8 m is the deepest a carved bed usually gets.
        ShallowDepth = 0.8f,

        // Slightly clearer than the sea, so the bed texture the author carved stays readable through it. Modest
        // on purpose: the depth grade above is what should carry the read, not transparency.
        Opacity = 0.9f,

        // The alpha feather at the waterline. At the sea's 0.6 m a river would be feathered toward transparent
        // bank to bank, which is the same scale problem as the foam band.
        ShoreFadeDistance = 0.2f,

        // Ripple wavelength. The sea's 2.5 m puts less than one ripple across a three-tile river, so the surface
        // reads as a single slow bulge rather than moving water.
        WaveScale = 0.8f,
    };
}
