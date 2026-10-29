using System;

namespace KhaozEngine.TileWorld;

/// <summary>
/// Turns on <see cref="TileWorldView.CameraFade"/>: while the camera eye is inside a placed object's drawn
/// triangles, that object's colour fades out and its shadow stays. A third-person camera that the world does not
/// stop, through trees and props for instance, otherwise ends up looking at the inside of a model.
/// </summary>
/// <remarks>
/// An object contains the eye when the generalized winding number of its triangles there is at least 0.5 in
/// magnitude, or when its nearest triangle is within <see cref="ClipRadius"/>, which is the near plane slicing a
/// face from just outside. Once it contains the eye it keeps doing so until both tests fail by a further
/// <see cref="ExitMargin"/>, so an eye resting on a surface does not flicker the object in and out.
/// </remarks>
public sealed class TileCameraFadeOptions
{
    /// <summary>Which archetypes take part. An archetype the predicate rejects never fades.</summary>
    public required Func<TileObjectArchetype, bool> Eligible { get; init; }

    /// <summary>Metres from the eye within which a nearby triangle counts as the eye being inside. The default,
    /// 0.25 m, covers the near-plane rectangle of a 0.1 m near plane with margin.</summary>
    public float ClipRadius { get; init; } = 0.25f;

    /// <summary>Further metres the eye must clear before an object it was inside stops containing it.</summary>
    public float ExitMargin { get; init; } = 0.15f;

    /// <summary>Seconds a fade takes each way, out and back in. Zero or less snaps.</summary>
    public float FadeSeconds { get; init; } = 0.15f;
}
