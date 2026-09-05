using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// How one piece of floating world text looks and how it dies: its colour, how long it lives, where it drifts,
    /// how it scales, how it fades in and out, how many of it one anchor may hold at once, and how far apart a
    /// simultaneous burst stacks. Immutable, so derive variants with <c>with</c> and keep one static per kind of
    /// line (an experience drop, a level up, a gameplay notice).
    /// <para>A bare <c>new()</c> is all-zero, which means a zero lifetime, which means nothing is ever drawn. That is
    /// the same no-op default <see cref="AttentionBeaconParams"/> has and for the same reason: a style nobody filled
    /// in should show nothing rather than a white line at the origin. Use <see cref="Default"/> for the preset.</para>
    /// <para>GAME-AGNOSTIC ON PURPOSE. Nothing here knows what the text says or where the anchor is in a world: the
    /// store holds an opaque anchor id and the renderer is handed a function that turns one into a screen point, so
    /// a 3D game projecting a body and a 2D game reading a sprite position use the same types.</para>
    /// </summary>
    public readonly record struct FloatingTextStyle
    {
        /// <summary>Tint of the text. Its own alpha multiplies the fade curve, so a style may start translucent.</summary>
        public Color Color { get; init; }

        /// <summary>How long an entry lives, in seconds. At or below zero nothing is ever drawn and every entry
        /// expires on the first <c>Age</c>.</summary>
        public float LifetimeSeconds { get; init; }

        /// <summary>Design-space pixels per second the text travels from where it was born. Y grows DOWN, so
        /// <c>(0, -40)</c> rises and <c>(-30, -40)</c> drifts up and to the left.</summary>
        public Vector2 DriftPerSecond { get; init; }

        /// <summary>Text scale at birth, passed straight to <c>SpriteBatch.DrawString</c>.</summary>
        public float StartScale { get; init; }

        /// <summary>Text scale at the end of the lifetime. Equal to <see cref="StartScale"/> for no zoom.</summary>
        public float EndScale { get; init; }

        /// <summary>Seconds of fade IN from transparent at birth. Zero pops in at full alpha.</summary>
        public float FadeInSeconds { get; init; }

        /// <summary>Seconds of fade OUT before the end of the lifetime, so the fade is the LAST N seconds rather
        /// than the first. Zero pops out.</summary>
        public float FadeOutSeconds { get; init; }

        /// <summary>How many live entries one anchor may hold. A further add evicts that anchor's OLDEST. Zero is
        /// unlimited, which is the right answer only when the game already rate-limits what it adds.</summary>
        public int MaxPerAnchor { get; init; }

        /// <summary>Design-space pixels between two entries of one anchor born on the SAME frame, applied DOWN the
        /// screen so the oldest of a burst sits highest. It is a birth-time step and nothing else: entries born
        /// apart are already separated by <see cref="DriftPerSecond"/>, and an entry's step never changes once it
        /// exists, so an older sibling expiring cannot make the rest jump.</summary>
        public float StackSpacing { get; init; }

        /// <summary>Draw a one-pass drop shadow under the text, offset by <see cref="ShadowOffset"/> and tinted
        /// <see cref="ShadowColor"/>. What keeps a light number legible over a light floor.</summary>
        public bool Shadow { get; init; }

        /// <summary>Design-space offset of the drop shadow from the text, scaled with the text. Ignored when
        /// <see cref="Shadow"/> is false.</summary>
        public Vector2 ShadowOffset { get; init; }

        /// <summary>Tint of the drop shadow. Its alpha multiplies the same fade curve the text uses, so the pair
        /// fades as one. Ignored when <see cref="Shadow"/> is false.</summary>
        public Color ShadowColor { get; init; }

        /// <summary>A white line that rises 40 px a second for a second and a half, holds its size, fades in over
        /// the first tenth and out over the last half, stacks four deep 14 px apart, and carries a black drop
        /// shadow. A sensible starting point for an experience drop or a gameplay line beside a character.</summary>
        public static FloatingTextStyle Default => new()
        {
            Color = Color.White,
            LifetimeSeconds = 1.5f,
            DriftPerSecond = new Vector2(0f, -40f),
            StartScale = 1f,
            EndScale = 1f,
            FadeInSeconds = 0.1f,
            FadeOutSeconds = 0.5f,
            MaxPerAnchor = 4,
            StackSpacing = 14f,
            Shadow = true,
            ShadowOffset = new Vector2(1f, 1f),
            ShadowColor = new Color(0f, 0f, 0f, 1f),
        };
    }
}
