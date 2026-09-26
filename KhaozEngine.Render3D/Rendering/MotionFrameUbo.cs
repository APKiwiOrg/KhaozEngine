using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The per-frame uniforms every temporal variant's vertex stage reads, the <c>MotionFrame</c> block of
/// <see cref="Internal.ShaderSources.MotionFrameMembersGlsl"/>. 144 bytes, uploaded whole once per temporal frame.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MotionFrameUbo
{
    /// <summary>This frame's unjittered render-relative view-projection.</summary>
    public Matrix4x4 CurViewProj;
    /// <summary>Last frame's, rebased to this frame's render origin, or this frame's when there is no valid history.</summary>
    public Matrix4x4 PrevViewProj;
    /// <summary>x = 1 when last frame is valid history, else 0, which makes every variant write exactly zero.</summary>
    public Vector4 Params;

    public const uint SizeInBytes = 144;
}
