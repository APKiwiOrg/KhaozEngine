using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// How an energy-beam band terminates at each endpoint.
    /// </summary>
    public enum BeamCap
    {
        /// <summary>Flat, square ends (the band's rectangular quads stop hard at the endpoints). The default.</summary>
        None = 0,

        /// <summary>
        /// A round (cylindrical) end-cap: a soft disc of radius half the band's pulse-adjusted width centred on each
        /// endpoint, so the beam reads as a capsule/cylinder rather than a rectangle. Requires a glow texture be
        /// passed to <see cref="EnergyBeam.Draw"/> (the radial disc is sampled from it); with no glow texture the
        /// ends stay square.
        /// </summary>
        Round = 1,
    }

    /// <summary>
    /// What shape a beam's sideways displacement takes. Both modes are driven by
    /// <see cref="BeamParams.JitterAmount"/> and <see cref="BeamParams.JitterSpeed"/>, which mean different things
    /// in each.
    /// </summary>
    public enum BeamJitter
    {
        /// <summary>
        /// A coherent sinusoidal sideways wobble: one smooth wave travelling along the beam, so the beam reads as a
        /// wavy straight line. <see cref="BeamParams.JitterAmount"/> is the wave amplitude in pixels and
        /// <see cref="BeamParams.JitterSpeed"/> its rate in radians per second. The default, and what every beam
        /// did before the jagged mode existed.
        /// </summary>
        Wave = 0,

        /// <summary>
        /// A jagged electric bolt: every segment boundary is displaced perpendicular to the axis by its OWN signed
        /// noise, under a mid-span envelope that pins both endpoints exactly on the axis, and each quad is drawn
        /// between its two displaced boundaries rather than along the axis. This is the chain-lightning / tesla /
        /// shock look the sinusoidal wobble cannot express, however it is tuned
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/239">#239</see>).
        /// <see cref="BeamParams.JitterAmount"/> is the peak displacement in pixels at mid-span, and
        /// <see cref="BeamParams.JitterSpeed"/> becomes the RE-ROLL RATE in whole new bolts per second (0 holds one
        /// still bolt, it does not disable the displacement). The bolt for a given
        /// <see cref="BeamParams.JitterSeed"/> and time is a pure function of the two, so the beam stays stateless
        /// and every client draws the same bolt at the same time.
        /// </summary>
        Jagged = 1,
    }

    /// <summary>
    /// Tunables for an additive energy beam (see <see cref="EnergyBeam.Draw"/>). Immutable; derive variants with
    /// <c>with</c> expressions. A wide soft <see cref="GlowWidth"/>/<see cref="GlowColor"/> band sits under a
    /// bright thin <see cref="CoreWidth"/>/<see cref="CoreColor"/> core; the core can be broken into flowing
    /// dashes (<see cref="DashLength"/>/<see cref="DashGap"/>/<see cref="DashSpeed"/>), pulse in brightness/width
    /// (<see cref="PulseSpeed"/>/<see cref="PulseAmount"/>), wobble sideways (<see cref="JitterAmount"/>/
    /// <see cref="JitterSpeed"/>), flare at the endpoints (<see cref="FlareRadius"/>), and round its ends
    /// (<see cref="Caps"/>).
    /// </summary>
    public readonly record struct BeamParams
    {
        /// <summary>Width (pixels) of the bright inner core.</summary>
        public float CoreWidth { get; init; }

        /// <summary>Colour of the inner core.</summary>
        public Color CoreColor { get; init; }

        /// <summary>Width (pixels) of the soft outer glow band drawn under the core. 0 disables the glow band.</summary>
        public float GlowWidth { get; init; }

        /// <summary>Colour of the outer glow band.</summary>
        public Color GlowColor { get; init; }

        /// <summary>Length (pixels) of each lit dash on the core. 0 = solid (no dashing).</summary>
        public float DashLength { get; init; }

        /// <summary>Length (pixels) of the gap between dashes.</summary>
        public float DashGap { get; init; }

        /// <summary>Flow speed (pixels/second) the dash pattern scrolls along the beam.</summary>
        public float DashSpeed { get; init; }

        /// <summary>Radius (pixels) of the glow flare drawn at each endpoint. 0 disables flares.</summary>
        public float FlareRadius { get; init; }

        /// <summary>Brightness/width pulse speed (radians/second). 0 disables pulsing.</summary>
        public float PulseSpeed { get; init; }

        /// <summary>Pulse amplitude [0,1]: fraction by which brightness/width oscillates.</summary>
        public float PulseAmount { get; init; }

        /// <summary>Sideways displacement amplitude (pixels). 0 disables jitter in both
        /// <see cref="BeamJitter"/> modes. Under <see cref="BeamJitter.Jagged"/> this is the PEAK displacement,
        /// reached at mid-span, with both endpoints pinned on the axis.</summary>
        public float JitterAmount { get; init; }

        /// <summary>Under <see cref="BeamJitter.Wave"/>, the sideways wobble speed in radians/second (0 disables
        /// the wobble). Under <see cref="BeamJitter.Jagged"/>, the RE-ROLL RATE in whole new bolts per second,
        /// where 0 holds one still bolt rather than disabling anything.</summary>
        public float JitterSpeed { get; init; }

        /// <summary>Which shape the sideways displacement takes: the coherent sinusoidal
        /// <see cref="BeamJitter.Wave"/> (the default, and what every beam did before) or the per-segment random
        /// <see cref="BeamJitter.Jagged"/> electric bolt. Reads both <see cref="JitterAmount"/> and
        /// <see cref="JitterSpeed"/>, which mean different things per mode.</summary>
        public BeamJitter JitterShape { get; init; }

        /// <summary>Picks WHICH bolt <see cref="BeamJitter.Jagged"/> draws. Two beams alive at once with the same
        /// seed draw the same bolt, so give concurrent arcs different seeds. Unused by
        /// <see cref="BeamJitter.Wave"/>.</summary>
        public int JitterSeed { get; init; }

        /// <summary>Number of quad segments the beam is split into for dashing/jitter sampling. Clamped to >= 1.</summary>
        public int Segments { get; init; }

        /// <summary>
        /// How the core and glow bands terminate at each endpoint. <see cref="BeamCap.None"/> (the default) keeps
        /// the original square ends; <see cref="BeamCap.Round"/> adds a soft disc cap (radius = half the band's
        /// pulse-adjusted width) at both ends of each band so the beam reads as a capsule. Independent of
        /// <see cref="FlareRadius"/>: a beam with <c>FlareRadius = 0</c> and <see cref="BeamCap.Round"/> still has
        /// rounded ends. Round caps need a glow texture passed to <see cref="EnergyBeam.Draw"/>.
        /// </summary>
        public BeamCap Caps { get; init; }

        /// <summary>A solid cyan-white beam with a soft glow band and endpoint flares (a sensible starting point).</summary>
        public static BeamParams Default => new()
        {
            CoreWidth = 3f,
            CoreColor = new Color(0.85f, 0.95f, 1f, 1f),
            GlowWidth = 12f,
            GlowColor = new Color(0.2f, 0.6f, 1f, 0.5f),
            DashLength = 0f,
            DashGap = 0f,
            DashSpeed = 0f,
            FlareRadius = 10f,
            PulseSpeed = 0f,
            PulseAmount = 0f,
            JitterAmount = 0f,
            JitterSpeed = 0f,
            JitterShape = BeamJitter.Wave,
            JitterSeed = 0,
            Segments = 24,
            Caps = BeamCap.None,
        };

        /// <summary>A short-lived white-hot electric arc: a jagged bolt re-rolling many times a second under a
        /// wide blue glow. Pair it with a per-arc <see cref="JitterSeed"/> so two arcs on screen at once are not
        /// the same bolt, and drive the fade yourself by scaling the two colours' alpha over the arc's
        /// lifetime.</summary>
        public static BeamParams ElectricArc => new()
        {
            CoreWidth = 2f,
            CoreColor = new Color(0.95f, 0.98f, 1f, 1f),
            GlowWidth = 9f,
            GlowColor = new Color(0.45f, 0.7f, 1f, 0.45f),
            DashLength = 0f,
            DashGap = 0f,
            DashSpeed = 0f,
            FlareRadius = 7f,
            PulseSpeed = 0f,
            PulseAmount = 0f,
            JitterAmount = 9f,
            JitterSpeed = 18f,          // 18 whole new bolts a second: the flicker
            JitterShape = BeamJitter.Jagged,
            JitterSeed = 0,
            Segments = 14,
            Caps = BeamCap.None,
        };
    }
}
