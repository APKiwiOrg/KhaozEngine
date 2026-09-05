using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// One screen-fixed banner: a line that appears at a point, travels to another, scales and fades on the way, and
    /// then goes. What a game puts a level-up or a milestone on, where the whole screen is the subject rather than
    /// one body in the world.
    /// <para>It carries its own two points rather than an anchor id, which is the whole difference from
    /// <see cref="FloatingText"/> and the reason the two have separate stores: a banner never has to be projected,
    /// never goes off screen, and never stacks against a sibling.</para>
    /// </summary>
    public readonly record struct FloatingBanner
    {
        /// <summary>What the banner says. Already localized.</summary>
        public string Text { get; init; }

        /// <summary>How it looks and dies. The same style type the anchored text uses, of which a banner reads
        /// <see cref="FloatingTextStyle.Color"/>, <see cref="FloatingTextStyle.LifetimeSeconds"/>, the two scales,
        /// the two fades and the shadow. <see cref="FloatingTextStyle.DriftPerSecond"/>,
        /// <see cref="FloatingTextStyle.StackSpacing"/> and <see cref="FloatingTextStyle.MaxPerAnchor"/> mean nothing
        /// here, because the travel is the two points and there is nothing to stack against.</summary>
        public FloatingTextStyle Style { get; init; }

        /// <summary>Design-space screen point the banner is centred on at birth, usually the middle of the
        /// screen.</summary>
        public Vector2 Start { get; init; }

        /// <summary>Design-space screen point it is centred on at the end of its lifetime, usually a corner it
        /// shrinks away toward.</summary>
        public Vector2 End { get; init; }

        /// <summary>Seconds since birth. Advanced by <see cref="FloatingBannerStore.Age"/> and nothing else.</summary>
        public float Age { get; init; }
    }
}
