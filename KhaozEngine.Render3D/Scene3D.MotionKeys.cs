using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>
/// The motion-key half of <see cref="Scene3D"/> (TEMPORAL-FOUNDATIONS-DESIGN section 3). Each keyed draw's world
/// transform, and a skinned draw's composed palette, is recorded at submission, and last frame's stays readable for
/// the renderer.
/// <para>
/// Only while temporal rendering is active. Recording is decided per submission through <see cref="TemporalActive"/>,
/// which is the live requesters before the frame's first render and the frame's fixed value after it. The history
/// swaps at <see cref="Begin"/> when temporal rendering is active then, and is forgotten when it is not. With temporal
/// off the history is never created, or is emptied once and then left alone, so keys cost nothing and a later active
/// frame starts with no previous state instead of a stale one.
/// </para>
/// </summary>
public sealed partial class Scene3D
{
    // Created on the first frame temporal rendering is active, so a scene that never asks for it never allocates it.
    MotionHistory? _motionHistory;

    /// <summary>The history this frame's temporal consumers read, or null when no key has previous state: temporal
    /// rendering is off this frame, or it turned on after <see cref="Begin"/> on the scene's first temporal frame and
    /// no keyed draw has created the history yet. Its previous generation is the keyed submissions made between the last
    /// two <see cref="Begin"/> calls, and it is empty after a <see cref="Begin"/> with temporal rendering off. A frame
    /// that turns temporal rendering on after such a <see cref="Begin"/> therefore reads no previous transform for any
    /// key, which a consumer treats as a first sighting.</summary>
    internal MotionHistory? ActiveMotionHistory => TemporalActive ? _motionHistory : null;

    /// <summary>The history whether or not it is active this frame, null until temporal rendering first runs. For
    /// tests.</summary>
    internal MotionHistory? MotionHistoryForTests => _motionHistory;

    /// <summary>This frame's keyed rigid and skinned submissions and key collisions, all zero with temporal off.</summary>
    internal (int KeyedRigid, int KeyedSkinned, int KeyCollisions) MotionKeyCounts
        => TemporalActive && _motionHistory is { } history
            ? (history.KeyedRigid, history.KeyedSkinned, history.Collisions)
            : (0, 0, 0);

    // Called from Begin: swap generations when temporal rendering is active at Begin, forget everything when it is not.
    void BeginMotionFrame()
    {
        if (!TemporalActive)
        {
            _motionHistory?.Reset();
            return;
        }
        _motionHistory ??= new MotionHistory();
        _motionHistory.BeginFrame();
    }

    // A shadow-only draw is masked out of the colour pass, so no motion is ever read for it and its key is ignored.
    // Created here too, for a requester turned on after Begin on the scene's first temporal frame.
    void RecordRigidMotion(in RigidInstanceDraw draw)
    {
        if (draw.Motion.IsNone || draw.ShadowOnly || !TemporalActive) return;
        (_motionHistory ??= new MotionHistory()).RecordRigid(draw.Motion, draw.World);
    }

    // Records the composed palette ComposeBonesIntoSlot just wrote for this draw's slot, which is what the renderer
    // uploads, so a previous-palette skin pass reads the same kind of matrix as the current one.
    void RecordSkinnedMotion(in SkinnedInstanceDraw draw, int slot, int boneCount)
    {
        if (draw.Motion.IsNone || !TemporalActive) return;
        ReadOnlySpan<Matrix4x4> composed = CollectionsMarshal.AsSpan(_boneMatrices)
            .Slice(slot * SkinningMath.MaxBonesPerDraw, boneCount);
        (_motionHistory ??= new MotionHistory()).RecordSkinned(draw.Motion, draw.Model, composed);
    }
}
