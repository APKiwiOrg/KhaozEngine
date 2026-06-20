using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Tunables for an additive energy beam (see <see cref="EnergyBeam.Draw"/>). Immutable; derive variants with
    /// <c>with</c> expressions. A wide soft <see cref="GlowWidth"/>/<see cref="GlowColor"/> band sits under a
    /// bright thin <see cref="CoreWidth"/>/<see cref="CoreColor"/> core; the core can be broken into flowing
    /// dashes (<see cref="DashLength"/>/<see cref="DashGap"/>/<see cref="DashSpeed"/>), pulse in brightness/width
    /// (<see cref="PulseSpeed"/>/<see cref="PulseAmount"/>), wobble sideways (<see cref="JitterAmount"/>/
    /// <see cref="JitterSpeed"/>), and flare at the endpoints (<see cref="FlareRadius"/>).
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
        };
    }
}
