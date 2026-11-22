using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class BoundOutfit
{
    /// <summary>Write the visible draws of the selected worn set with the latest shape of this outfit's motion.</summary>
    /// <remarks>Chain joints follow the supplied host anchors. The borrowed palettes remain valid until the worn
    /// binding's next preparation, submission or cache disposal. The motion is read only.</remarks>
    public int WriteDraws(int wornIndex, WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template, Span<AttachmentDraw> draws)
    {
        ArgumentNullException.ThrowIfNull(motion);
        return WriteVisibleDraws(wornIndex, skeleton, palette, model, instanceKey, template, draws, motion);
    }

    /// <summary>Add the selected worn set's visible draws to the outline group using this outfit's motion.</summary>
    public void QueueOutline(int wornIndex, WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MeshOutlineGroup group, float dissolve = 0f, bool dissolveComplement = false)
    {
        ArgumentNullException.ThrowIfNull(motion);
        QueueVisibleOutline(wornIndex, skeleton, palette, model, group, dissolve, dissolveComplement, motion);
    }
}
