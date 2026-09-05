using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// One piece of floating text pinned to an anchor: what it says, what it is pinned to, where it started
    /// relative to that anchor, how it looks, how old it is, and which step of a simultaneous burst it took.
    /// <para>Held by <see cref="FloatingTextStore"/> and read by <see cref="FloatingTextRenderer"/>. Everything about
    /// where it is drawn is derived from these fields by <see cref="FloatingTextCurves"/>, so an entry carries no
    /// position of its own and nothing has to be re-integrated when a frame is dropped.</para>
    /// </summary>
    public readonly record struct FloatingText
    {
        /// <summary>What the line says. Already localized: this layer never resolves a string id, because it does
        /// not know whether the text is a name, a number, or a sentence.</summary>
        public string Text { get; init; }

        /// <summary>The opaque id of the thing this is pinned to, resolved to a screen point at draw time. A game's
        /// net id, entity id or sprite handle, whatever it can turn back into a point.</summary>
        public long AnchorId { get; init; }

        /// <summary>Design-space pixels from the anchor's own point at BIRTH, before any drift or stack step. Where
        /// a game says "over the head" rather than "at the feet".</summary>
        public Vector2 Offset { get; init; }

        /// <summary>How this entry looks and dies. Per entry rather than per store, so one store carries experience
        /// drops and damage numbers at once.</summary>
        public FloatingTextStyle Style { get; init; }

        /// <summary>Seconds since birth. Advanced by <see cref="FloatingTextStore.Age"/> and nothing else.</summary>
        public float Age { get; init; }

        /// <summary>Which step of the anchor's stack this entry took at BIRTH: the number of that anchor's entries
        /// already live when it was added. Never renormalized, because renormalizing is what would make the rest of
        /// a burst jump when its oldest expires. See <see cref="FloatingTextStyle.StackSpacing"/>.</summary>
        public int StackIndex { get; init; }
    }
}
