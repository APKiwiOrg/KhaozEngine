using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>
/// The last rendered frame's temporal state, read via <see cref="Scene3D.LastTemporalDiagnostics"/>. A value snapshot in
/// the shape of <see cref="ShadowPassDiagnostics"/>: always on, allocation-free to read, default-valued before the first
/// render, and written once per frame on the frame's first render. Read it on the render thread after the frame renders.
/// </summary>
/// <param name="FrameIndex">The frame's index, advanced once per <see cref="Scene3D.Begin"/>.</param>
/// <param name="JitterPhase">Where the frame index falls in the jitter sequence, from 0, reported whether or not the
/// jitter is applied, see <see cref="JitterPixels"/>.</param>
/// <param name="JitterPixels">The sub-pixel jitter the frame rasterised with, in internal pixels, x right and y down,
/// each in [-0.5, 0.5). Zero while temporal rendering is inactive.</param>
/// <param name="KeyedRigid">Rigid draws the frame submitted with a motion key while temporal rendering was active,
/// <see cref="RigidInstanceDraw.ShadowOnly"/> draws excluded. Counted at submission, before culling, so a keyed draw
/// the camera never sees still counts, and only for draws made before the frame's first render. Zero while temporal
/// rendering is off.</param>
/// <param name="KeyedSkinned">Skinned draws the frame submitted with a motion key while temporal rendering was active.
/// Counted at submission, before culling, and only for draws made before the frame's first render. Zero while
/// temporal rendering is off.</param>
/// <param name="KeyCollisions">Keyed draws counted above whose key was already counted this frame in the same map, so
/// a rigid and a skinned draw that share a key never collide. Zero while temporal rendering is off.</param>
/// <param name="HistoryValid">Whether the frame had a previous frame to reproject from.</param>
/// <param name="LastReset">Why the history was last reset. <see cref="TemporalResetReason.FirstFrame"/> while temporal
/// rendering is off and on the first frame it is on.</param>
public readonly record struct TemporalDiagnostics(long FrameIndex, int JitterPhase, Vector2 JitterPixels,
    int KeyedRigid, int KeyedSkinned, int KeyCollisions, bool HistoryValid, TemporalResetReason LastReset);
