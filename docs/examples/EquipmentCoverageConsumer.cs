using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Examples;

// Consumer-owned example. Construct on the scene thread after uploading every annotated host slice.
// The binding is owned here. The adapter, skeleton, equipment cache, scene and submission are borrowed.
public sealed class EquipmentCoverageConsumer : IDisposable
{
    readonly IAttachmentScene scene;
    readonly Skeleton skeleton;
    readonly MotionKey actor;
    readonly HostCoverageBinding binding;
    CoveredAttachmentInstance[] equipment = [];
    AttachmentDraw[][] ordinaryScratch = [];

    public EquipmentCoverageConsumer(IAttachmentScene scene, Skeleton skeleton,
        IReadOnlyList<SkinnedMeshHandle> hosts, MotionKey actor, CoverageRecessionTable table)
    {
        this.scene = scene;
        this.skeleton = skeleton;
        this.actor = actor;
        binding = new(scene, skeleton, hosts, actor, table);
    }

    // Cold equip. A refused replacement preserves the active configuration and fallback scratch.
    public bool TrySetEquipment(ReadOnlySpan<CoveredAttachmentInstance> replacement, out CoverageFailure failure)
    {
        if (!binding.TrySetEquipment(replacement, out failure)) return false;
        equipment = replacement.ToArray();
        ordinaryScratch = new AttachmentDraw[equipment.Length][];
        for (int i = 0; i < equipment.Length; i++)
            ordinaryScratch[i] = new AttachmentDraw[equipment[i].Set.DrawCount];
        return true;
    }

    // Under GameApp3D call Queue from OnDraw3D. The host already joined Frame.Submission and prepares on return.
    // The caller supplies its resolved opaque key and every host material descriptor for this pose.
    // The return value describes preparation. Query Scene3D.CoverageFailureFor after scene preparation
    // for later publication refusals. Queue once per actor between BeginFrame and PrepareFrame.
    public bool Queue(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, CoverageStateKey key,
        in RigidInstanceDraw equipmentTemplate, ReadOnlySpan<SkinnedInstanceDraw> hosts, out CoverageFailure failure)
    {
        bool accepted = binding.TryPrepare(skeleton, palette, model, key, equipmentTemplate, out var frame, out failure);
        if (accepted) Forward(scene, binding.PreparedEquipmentDraws.Span);
        else
        {
            // A hole has no prepared equipment descriptors. Draw the active equipment normally.
            for (int i = 0; i < equipment.Length; i++)
            {
                ulong incarnation = equipment[i].InstanceId;
                var equipmentKey = MotionKey.Combine(MotionKey.Combine(actor, (uint)incarnation), (uint)(incarnation >> 32));
                QueueOrdinary(scene, equipment[i].Set, skeleton, palette, model, equipmentKey,
                    equipmentTemplate, ordinaryScratch[i]);
            }
        }
        foreach (var host in hosts)
        {
            // Explicit uncovered current pose on refusal. Preserve every other material/dissolve/shadow field.
            var draw = host with { Model = model, Motion = actor, Coverage = accepted ? frame : default };
            scene.DrawSkinned(draw, palette);
        }
        return accepted;
    }

    // Legacy adapters need only IAttachmentScene. Scratch is reserved at cold equip.
    public static void QueueOrdinary(IAttachmentScene scene, BoundAttachmentSet equipment, Skeleton skeleton,
        ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, MotionKey key, in RigidInstanceDraw template,
        Span<AttachmentDraw> scratch)
    {
        int count = equipment.WriteDraws(skeleton, palette, model, key, template with { Coverage = default }, scratch);
        Forward(scene, scratch[..count]);
    }

    static void Forward(IAttachmentScene scene, ReadOnlySpan<AttachmentDraw> descriptors)
    {
        foreach (var draw in descriptors)
            if (draw.IsSkinned) scene.DrawSkinned(draw.Skinned, draw.BoneMatrices.Span);
            else scene.DrawMesh(draw.Rigid);
    }

    // Custom hosts only. GameApp3D already owns this loop and must not call SubmitFrame from OnDraw3D.
    // Call once for the scene, with cached callbacks for an allocation-sensitive host. recordScene is
    // the host's existing surface/render callback and must not begin, end or submit this list itself.
    public static void SubmitFrame(Scene3D scene, GpuFrameSubmission submission, IGpuCommandList commands,
        int width, int height, Action queueActors, Action<IGpuCommandList> recordScene)
    {
        submission.Begin();
        scene.BeginFrame(submission);
        try
        {
            queueActors();
            // Registers the scene's one aggregate participant and completion consumer for all actors.
            scene.PrepareFrame(width, height);
            commands.Begin();
            recordScene(commands);
            commands.End();
            submission.Submit(commands);
        }
        catch
        {
            submission.Abandon();
            throw;
        }
    }

    // At host teardown, drain/dispose GpuFrameSubmission first, then these bindings, then caches/scenes.
    public void Dispose() => binding.Dispose();
}
