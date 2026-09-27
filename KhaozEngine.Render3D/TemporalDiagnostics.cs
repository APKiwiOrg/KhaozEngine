using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>
/// The last rendered frame's temporal state, read via <see cref="Scene3D.LastTemporalDiagnostics"/>. A value snapshot in
/// the shape of <see cref="ShadowPassDiagnostics"/>: always on, allocation-free to read, and written once per frame on
/// the frame's first render. Before the first render the whole value is <c>default</c>, so <see cref="UpscaleRatio"/>
/// and <see cref="CountsFrameIndex"/> read 0 there rather than the parameter defaults below. Read it on the render
/// thread after the frame renders.
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
/// <param name="InternalWidth">Width of the internal target the frame's first render rendered at, under every
/// anti-aliasing mode. The four sizes read zero, and <see cref="UpscaleRatio"/> 1, on a frame whose first render had no
/// display area, such as a minimised window, rather than the last display size beside whatever internal target such a
/// render allocates: a pixel or two where the internal size follows the viewport, the unchanged fixed size under
/// <see cref="RenderScale.FixedInternal"/>.</param>
/// <param name="InternalHeight">Height of the internal target the frame's first render rendered at.</param>
/// <param name="DisplayWidth">Width of the target the frame's first render presented to.</param>
/// <param name="DisplayHeight">Height of the target the frame's first render presented to.</param>
/// <param name="Preset">The <see cref="TemporalSettings.Upscale"/> setting at the frame's first render, reported under
/// every anti-aliasing mode, and whether or not an explicit <see cref="TemporalSettings.UpscaleRatio"/> overrides
/// it.</param>
/// <param name="UpscaleRatio">The internal to display width ratio of a frame the temporal resolve ran on, which shows
/// an explicit <see cref="TemporalSettings.UpscaleRatio"/> and the render size caps, and 1 on any other frame, such as
/// one under another anti-aliasing mode, whatever its internal size.</param>
/// <param name="CountsFrameIndex">The frame the counts below were sampled on, minus 1 from the first render until the
/// first <c>Scene3D.RequestTemporalCounts</c>.</param>
/// <param name="DisoccludedPixels">Estimated display pixels whose history was rejected by depth or an off-screen
/// reprojection, from a 32 by 18 grid of 16 samples a cell.</param>
/// <param name="ReactivePixels">Estimated display pixels the reactive estimate marked, on the same grid.</param>
/// <param name="ClippedPixels">Estimated display pixels whose history the variance clip moved, on the same
/// grid.</param>
public readonly record struct TemporalDiagnostics(long FrameIndex, int JitterPhase, Vector2 JitterPixels,
    int KeyedRigid, int KeyedSkinned, int KeyCollisions, bool HistoryValid, TemporalResetReason LastReset,
    int InternalWidth = 0, int InternalHeight = 0, int DisplayWidth = 0, int DisplayHeight = 0,
    TemporalUpscale Preset = TemporalUpscale.Native, float UpscaleRatio = 1f, long CountsFrameIndex = -1,
    int DisoccludedPixels = 0, int ReactivePixels = 0, int ClippedPixels = 0);
