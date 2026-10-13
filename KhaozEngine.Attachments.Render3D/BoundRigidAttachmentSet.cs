using System;
using System.Buffers.Binary;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A skeleton binding that borrows its mesh resources from the cache.</summary>
/// <remarks>Use on the cache's owning render thread. Submission calls on this view must not overlap.</remarks>
public sealed class BoundRigidAttachmentSet
{
    readonly RigidAttachmentSetCache _owner;
    readonly RigidAttachmentBinding _binding;
    readonly RigidAttachmentSetDefinition _definition;
    readonly MeshHandle[][] _partHandles;
    readonly Matrix4x4[] _worlds;
    readonly RigidInstanceDraw[] _draws;
    readonly uint _revisionLow, _revisionHigh;
    readonly int _drawCount;

    internal BoundRigidAttachmentSet(RigidAttachmentSetCache owner, RigidAttachmentSetDefinition definition,
        MeshHandle[][] partHandles, ContractJointMap map)
    {
        _owner = owner;
        _binding = new RigidAttachmentBinding(definition, map);
        _definition = definition;
        _partHandles = partHandles;
        foreach (MeshHandle[] handles in partHandles) _drawCount += handles.Length;
        _worlds = new Matrix4x4[_binding.PartCount];
        _draws = new RigidInstanceDraw[_drawCount];
        byte[] revision = Convert.FromHexString(definition.SemanticSha256.AsSpan(0, 16));
        _revisionLow = BinaryPrimitives.ReadUInt32LittleEndian(revision);
        _revisionHigh = BinaryPrimitives.ReadUInt32LittleEndian(revision.AsSpan(4));
    }

    public Skeleton Skeleton
    {
        get { _owner.ThrowIfDisposed(); return _binding.Skeleton; }
    }

    /// <summary>One draw per material group of every canonical part.</summary>
    public int DrawCount
    {
        get { _owner.ThrowIfDisposed(); return _drawCount; }
    }

    /// <summary>Write borrowed descriptors in canonical part-ID and material order after validating the whole pose.</summary>
    /// <remarks>Replaces Mesh, World and Motion and preserves every other template field. Entries after DrawCount
    /// remain untouched. Valid calls allocate no managed memory. Change instanceKey when replacing an incarnation.</remarks>
    public int WriteDraws(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template,
        Span<RigidInstanceDraw> destination)
    {
        _owner.ThrowIfDisposed();
        if (destination.Length < _drawCount)
            throw new ArgumentException($"Output capacity {destination.Length} must hold {_drawCount} attachment draws.", nameof(destination));
        _binding.ComposeWorlds(skeleton, palette, model, _worlds);
        MotionKey revision = MotionKey.Combine(MotionKey.Combine(instanceKey, _revisionLow), _revisionHigh);
        int draw = 0;
        for (int part = 0; part < _partHandles.Length; part++)
        {
            MotionKey partKey = MotionKey.Combine(revision, _definition.Parts[part].Id);
            MeshHandle[] handles = _partHandles[part];
            for (int material = 0; material < handles.Length; material++)
                destination[draw++] = template with
                {
                    Mesh = handles[material],
                    World = _worlds[part],
                    Motion = MotionKey.Combine(partKey, (uint)material),
                };
        }
        return draw;
    }

    /// <summary>Queue the whole posed set with opaque white tint, no dissolve and shadow casting enabled.</summary>
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey)
    {
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        Queue(skeleton, palette, model, instanceKey, template);
    }

    /// <summary>Validate and compose the complete pose before submitting any material group.</summary>
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template)
    {
        int count = WriteDraws(skeleton, palette, model, instanceKey, template, _draws);
        for (int i = 0; i < count; i++) _owner.Scene.DrawMesh(in _draws[i]);
    }
}
