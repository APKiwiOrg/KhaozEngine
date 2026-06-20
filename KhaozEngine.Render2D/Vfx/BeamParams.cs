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

        /// <summary>Sideways wobble amplitude (pixels). 0 disables jitter.</summary>
        public float JitterAmount { get; init; }

        /// <summary>Sideways wobble speed (radians/second).</summary>
        public float JitterSpeed { get; init; }

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
            Segments = 24,
            Caps = BeamCap.None,
        };
    }
}
