using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>
/// A body and its worn sets with the outfit rule applied: the body draws only its uncovered triangles, a lower layer
/// piece draws only what no higher layer covers and a covered rigid part draws nothing. Hidden triangles are cut
/// through shared skinned mesh views, so every render pass draws the same triangles.
/// </summary>
/// <remarks>Binding computes the draw layout and allocates scratch. Valid draw calls allocate no managed memory.
/// Use on the cache's owning render thread with no overlapping calls. Dispose the outfit
/// before its cache and before the body's meshes unload, because its views draw from both.</remarks>
public sealed partial class BoundOutfit : IDisposable
{
    readonly AttachmentSetCache _owner;
    readonly OutfitViewCache _views;
    readonly BoundAttachmentSet[] _worn;
    readonly OutfitDraw[][] _draws;
    readonly SkinnedMeshHandle[] _body;
    readonly bool[] _bodyVisible;
    readonly ViewKey[] _used;
    readonly string _bodyRigIdentity;
    readonly Skeleton? _hostSkeleton;
    object? _motionIdentity;
    bool _disposed;

    internal BoundOutfit(AttachmentSetCache owner, OutfitViewCache views, BoundAttachmentSet[] worn, OutfitDraw[][] draws,
        SkinnedMeshHandle[] body, bool[] bodyVisible, ViewKey[] used, LoadedBodyRegions bodyRegions)
    {
        _owner = owner;
        _views = views;
        _worn = worn;
        _draws = draws;
        _body = body;
        _bodyVisible = bodyVisible;
        _used = used;
        _bodyRigIdentity = bodyRegions.Map.RigIdentity;
        _hostSkeleton = bodyRegions.HostSkeleton;
    }

    internal object MotionIdentity => _motionIdentity ??= new object();

    /// <summary>Create independent motion state for one wearer of this binding.</summary>
    public WornMotion CreateMotion(LoadedBodyColliders? bodyColliders, ChainSimulationOptions? options = null)
    {
        ThrowIfDisposed();
        Skeleton skeleton = _hostSkeleton ?? (_worn.Length > 0 ? _worn[0].Skeleton :
            throw new InvalidOperationException("Creating motion requires a body skin or a worn set with a skeleton."));
        ReadOnlySpan<Matrix4x4> inverseBind = default;
        foreach (BoundAttachmentSet set in _worn)
            if (ReferenceEquals(set.Skeleton, skeleton) && !set.HostInverseBind.IsEmpty)
            {
                inverseBind = set.HostInverseBind;
                break;
            }
        return WornMotionBinding.Create(MotionIdentity, skeleton, inverseBind, _worn, _draws,
            bodyColliders, _bodyRigIdentity, options);
    }

    /// <summary>The number of body material parts, the region map's part count.</summary>
    public int BodyPartCount
    {
        get { ThrowIfDisposed(); return _body.Length; }
    }

    /// <summary>The mesh to draw for body part <paramref name="bodyPart"/>: its own handle when nothing covers it, a
    /// view of its uncovered triangles when something does, and false when every triangle is covered.</summary>
    public bool TryBodyMesh(int bodyPart, out SkinnedMeshHandle mesh)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(bodyPart);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bodyPart, _body.Length);
        mesh = _body[bodyPart];
        return _bodyVisible[bodyPart];
    }

    /// <summary>The number of draws worn set <paramref name="wornIndex"/> writes: one per material group of every
    /// part, less the covered rigid parts and the material groups whose triangles are all covered.</summary>
    public int DrawCount(int wornIndex)
    {
        ThrowIfDisposed();
        return Draws(wornIndex).Length;
    }

    /// <summary>Write the draws of worn set <paramref name="wornIndex"/> in the set's own part-ID and material order,
    /// each naming the mesh or view the outfit leaves visible.</summary>
    /// <remarks>Every other field, motion keys included, matches <see cref="BoundAttachmentSet"/>'s mixed
    /// <c>WriteDraws</c>, so a set with no layer draws exactly as it does alone. The same pose and capacity rules
    /// apply, and the unused destination suffix stays untouched.</remarks>
    public int WriteDraws(int wornIndex, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MotionKey instanceKey, in RigidInstanceDraw template, Span<AttachmentDraw> draws) =>
        WriteVisibleDraws(wornIndex, skeleton, palette, model, instanceKey, template, draws, null);

    int WriteVisibleDraws(int wornIndex, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MotionKey instanceKey, in RigidInstanceDraw template, Span<AttachmentDraw> draws, WornMotion? motion)
    {
        ThrowIfDisposed();
        motion?.RequireBinding(_motionIdentity);
        OutfitDraw[] kept = Draws(wornIndex);
        if (draws.Length < kept.Length)
            throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.OutputTooSmall,
                $"Output capacity {draws.Length} must hold {kept.Length} outfit draws.", nameof(draws))
            { ActualCapacity = draws.Length, RequiredCapacity = kept.Length };
        ReadOnlySpan<AttachmentDraw> all =
            _worn[wornIndex].PrepareDraws(skeleton, palette, model, instanceKey, template, motion, wornIndex);
        for (int i = 0; i < kept.Length; i++)
        {
            ref readonly OutfitDraw draw = ref kept[i];
            AttachmentDraw source = all[draw.Draw];
            draws[i] = draw.IsSkinned ? source.WithSkinnedMesh(draw.Skinned) : source;
        }
        return kept.Length;
    }

    /// <summary>Add worn set <paramref name="wornIndex"/>'s visible draws to the caller's outline group, as
    /// <see cref="BoundAttachmentSet.QueueOutline(Skeleton, ReadOnlySpan{Matrix4x4}, in Matrix4x4, MeshOutlineGroup, float, bool)"/> does for a whole set.</summary>
    public void QueueOutline(int wornIndex, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MeshOutlineGroup group, float dissolve = 0f, bool dissolveComplement = false) =>
        QueueVisibleOutline(wornIndex, skeleton, palette, model, group, dissolve, dissolveComplement, null);

    void QueueVisibleOutline(int wornIndex, Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        MeshOutlineGroup group, float dissolve, bool dissolveComplement, WornMotion? motion)
    {
        ThrowIfDisposed();
        motion?.RequireBinding(_motionIdentity);
        OutfitDraw[] kept = Draws(wornIndex);
        BoundAttachmentSet set = _worn[wornIndex];
        set.PrepareOutlinePose(skeleton, palette, model, motion, wornIndex);
        IAttachmentScene scene = _owner.Scene;
        for (int i = 0; i < kept.Length; i++)
        {
            ref readonly OutfitDraw draw = ref kept[i];
            if (draw.IsSkinned)
                scene.DrawSkinnedOutlineDissolved(group, draw.Skinned, set.PartPalette(draw.Part), set.PartWorld(draw.Part),
                    dissolve, dissolveComplement);
            else
                scene.DrawMeshOutlineDissolved(group, draw.Rigid, set.PartWorld(draw.Part), dissolve, dissolveComplement);
        }
    }

    /// <summary>Give back this outfit's views. A view another outfit still uses stays live, and the last user's
    /// release lets the scene free it at the next frame boundary. Idempotent.</summary>
    /// <remarks>Every view is given back even when the scene refuses one, and the first refusal is rethrown after the
    /// rest. The outfit is disposed either way and never retries, since a refused view is no longer one to free.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? first = null;
        foreach (ViewKey key in _used)
        {
            try { _views.Release(key); }
            catch (Exception error) { first ??= ExceptionDispatchInfo.Capture(error); }
        }
        first?.Throw();
    }

    OutfitDraw[] Draws(int wornIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wornIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wornIndex, _draws.Length);
        return _draws[wornIndex];
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner.ThrowIfDisposed();
    }
}

/// <summary>One draw a worn set keeps: its index among the set's own draws, its part, and the mesh it draws.</summary>
internal readonly record struct OutfitDraw(int Draw, int Part, bool IsSkinned, MeshHandle Rigid, SkinnedMeshHandle Skinned);
