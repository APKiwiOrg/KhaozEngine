using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class BoundAttachmentSet
{
    /// <summary>Write the mixed draws in set order with the latest shape of this set's wearer motion.</summary>
    /// <remarks>Chain joints follow the supplied host anchors. The borrowed palettes remain valid until this
    /// binding's next preparation, submission or cache disposal. The motion is read only.</remarks>
    public int WriteDraws(WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MotionKey instanceKey, in RigidInstanceDraw template, Span<AttachmentDraw> destination)
    {
        ArgumentNullException.ThrowIfNull(motion);
        return WriteMixedDraws(skeleton, palette, model, instanceKey, template, destination, motion);
    }

    /// <summary>Queue this set and its wearer motion with opaque white tint, no dissolve and shadow casting enabled.</summary>
    public void Queue(WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MotionKey instanceKey)
    {
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        Queue(motion, skeleton, palette, model, instanceKey, template);
    }

    /// <summary>Validate the whole pose, then queue this set using its wearer motion and the supplied template.</summary>
    public void Queue(WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MotionKey instanceKey, in RigidInstanceDraw template)
    {
        ArgumentNullException.ThrowIfNull(motion);
        QueueSetDraws(skeleton, palette, model, instanceKey, template, motion);
    }

    /// <summary>Add this set to the caller's outline group with the latest shape of its wearer motion.</summary>
    public void QueueOutline(WornMotion motion, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MeshOutlineGroup group, float dissolve = 0f, bool dissolveComplement = false)
    {
        ArgumentNullException.ThrowIfNull(motion);
        QueueSetOutline(skeleton, palette, model, group, dissolve, dissolveComplement, motion);
    }
}
