using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Per-key previous transforms and bone palettes for temporal effects (TEMPORAL-FOUNDATIONS-DESIGN section 3). Two
/// generations: CURRENT, filled by this frame's keyed submissions, and PREVIOUS, last frame's, which the renderer
/// reads. <see cref="BeginFrame"/> swaps them and empties the new current, so a key that was not drawn last frame has
/// no previous state and a first sighting reports camera-only motion.
/// <para>
/// <see cref="Scene3D"/> swaps once per <see cref="Scene3D.Begin"/> when temporal rendering is active at that Begin and
/// calls <see cref="Reset"/> when it is not. The swap counts Begin calls, not rendered frames.
/// </para>
/// <para>
/// Transforms are ABSOLUTE, exactly as submitted, so a render origin step between the two frames adds no error. The
/// reader reduces both frames against the current origin. Skinned palettes retain the renderer's prepared matrix
/// or DQ words, source identity and profile, with absolute bounds for local history rejection.
/// </para>
/// <para>
/// Grow-only. The maps keep their capacity across the swap and the palette arrays only ever grow, so once a scene's
/// keyed population has been seen in both generations a frame allocates nothing.
/// </para>
/// <para>
/// A key recorded twice in one frame is a collision. The last submission wins and <see cref="Collisions"/> counts it.
/// Rigid and skinned keys live in separate maps, so a key used once by each is no collision.
/// </para>
/// </summary>
internal sealed class MotionHistory
{
    readonly record struct SkinnedEntry(Matrix4x4 World, SkinnedMotionIdentity? Identity,
        int Offset, int Length, int DqOffset, int DqLength, MeshBounds? WorldBounds);

    sealed class Generation
    {
        public readonly Dictionary<ulong, Matrix4x4> Rigid = new();
        public readonly Dictionary<ulong, SkinnedEntry> Skinned = new();
        public Matrix4x4[] Palettes = Array.Empty<Matrix4x4>();
        public int PaletteCount;
        public RigidDualQuaternion[] DqPalettes = Array.Empty<RigidDualQuaternion>();
        public int DqPaletteCount;

        public void Clear()
        {
            Rigid.Clear();
            Skinned.Clear();
            PaletteCount = 0;
            DqPaletteCount = 0;
        }

        // Appends one palette and returns where it starts. A collision appends again and leaves the loser's
        // region unused until the next Clear, which bounds the waste to one frame.
        public static int AppendPalette<T>(ref T[] storage, ref int count, ReadOnlySpan<T> palette)
        {
            int offset = count, need = checked(offset + palette.Length);
            if (need > storage.Length) Array.Resize(ref storage, Math.Max(need, storage.Length * 2));
            palette.CopyTo(storage.AsSpan(offset));
            count = need;
            return offset;
        }
    }

    Generation _current = new(), _previous = new();

    /// <summary>Keyed rigid submissions recorded since the last <see cref="BeginFrame"/>, collisions included.</summary>
    public int KeyedRigid { get; private set; }

    /// <summary>Keyed skinned submissions recorded since the last <see cref="BeginFrame"/>, collisions included.</summary>
    public int KeyedSkinned { get; private set; }

    /// <summary>Submissions since the last <see cref="BeginFrame"/> whose key was already recorded this frame in the
    /// same map.</summary>
    public int Collisions { get; private set; }

    /// <summary>Make this frame's records last frame's and start an empty current frame. Keys not recorded in the
    /// frame that just ended are gone.</summary>
    public void BeginFrame()
    {
        (_current, _previous) = (_previous, _current);
        _current.Clear();
        KeyedRigid = KeyedSkinned = Collisions = 0;
    }

    /// <summary>Forget both frames, for a frame with temporal rendering off, so a later active frame never reads a
    /// stale map as last frame's.</summary>
    public void Reset()
    {
        _current.Clear();
        _previous.Clear();
        KeyedRigid = KeyedSkinned = Collisions = 0;
    }

    /// <summary>Record a keyed rigid draw's absolute world transform for this frame. <see cref="MotionKey.None"/>
    /// records nothing.</summary>
    public void RecordRigid(MotionKey key, in Matrix4x4 world)
    {
        if (key.IsNone) return;
        KeyedRigid++;
        ref Matrix4x4 slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_current.Rigid, key.Value, out bool exists);
        if (exists) Collisions++;
        slot = world;
    }

    /// <summary>Last frame's absolute world transform for <paramref name="key"/>, if it was drawn.</summary>
    public bool TryGetPreviousRigid(MotionKey key, out Matrix4x4 world)
    {
        if (!key.IsNone && _previous.Rigid.TryGetValue(key.Value, out world)) return true;
        world = default;
        return false;
    }

    /// <summary>Record a keyed skinned draw's absolute model transform and composed palette for this frame. The
    /// palette is copied. <see cref="MotionKey.None"/> records nothing.</summary>
    public void RecordSkinned(MotionKey key, in Matrix4x4 world, ReadOnlySpan<Matrix4x4> palette)
        => RecordSkinned(key, world, null, palette, default, null);

    /// <summary>Copy the uploaded source identity, prepared payload words and absolute posed bounds. Replacing an
    /// actor never erases its previous bounds or any other actor's history.</summary>
    public void RecordSkinned(MotionKey key, in Matrix4x4 world, in SkinnedMotionIdentity identity,
        ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq, in MeshBounds worldBounds)
        => RecordSkinned(key, world, identity, lbs, dq, (MeshBounds?)worldBounds);

    void RecordSkinned(MotionKey key, in Matrix4x4 world, SkinnedMotionIdentity? identity,
        ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq, MeshBounds? worldBounds)
    {
        if (key.IsNone) return;
        KeyedSkinned++;
        int offset = Generation.AppendPalette(ref _current.Palettes, ref _current.PaletteCount, lbs);
        int dqOffset = Generation.AppendPalette(ref _current.DqPalettes, ref _current.DqPaletteCount, dq);
        ref SkinnedEntry slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_current.Skinned, key.Value, out bool exists);
        if (exists) Collisions++;
        slot = new SkinnedEntry(world, identity, offset, lbs.Length, dqOffset, dq.Length, worldBounds);
    }

    /// <summary>Legacy LBS view of last frame's absolute model transform and composed palette, if it was drawn.
    /// DQ records require the identity-aware overload. The span stays valid until the next <see cref="BeginFrame"/>
    /// or <see cref="Reset"/>. Its length is the
    /// bone count at record time, so a length that differs from the current draw's means a different mesh under the
    /// same key, which the caller treats as no previous state.</summary>
    public bool TryGetPreviousSkinned(MotionKey key, out Matrix4x4 world, out ReadOnlySpan<Matrix4x4> palette)
    {
        if (!key.IsNone && _previous.Skinned.TryGetValue(key.Value, out SkinnedEntry entry)
            && (entry.Identity is null || entry.Identity.Value.Profile.Method == SkinningMethod.LinearBlend))
        {
            world = entry.World;
            palette = new ReadOnlySpan<Matrix4x4>(_previous.Palettes, entry.Offset, entry.Length);
            return true;
        }
        world = default;
        palette = default;
        return false;
    }

    /// <summary>Read last frame's exact prepared words only when source, generation and profile correspond.
    /// The spans remain valid until the next BeginFrame or Reset, including across this frame's replacements.</summary>
    public bool TryGetPreviousSkinned(MotionKey key, in SkinnedMotionIdentity expected, out Matrix4x4 world,
        out ReadOnlySpan<Matrix4x4> lbs, out ReadOnlySpan<RigidDualQuaternion> dq)
    {
        if (!key.IsNone && _previous.Skinned.TryGetValue(key.Value, out SkinnedEntry entry)
            && entry.Identity == expected)
        {
            world = entry.World;
            lbs = _previous.Palettes.AsSpan(entry.Offset, entry.Length);
            dq = _previous.DqPalettes.AsSpan(entry.DqOffset, entry.DqLength);
            return true;
        }
        world = default;
        lbs = default;
        dq = default;
        return false;
    }

    /// <summary>Last frame's absolute bounds, even when its source no longer corresponds to this frame's.</summary>
    public bool TryGetPreviousBounds(MotionKey key, out MeshBounds worldBounds)
        => TryGetBounds(_previous, key, out worldBounds);

    /// <summary>This frame's absolute bounds, retained beside the old bounds for projected history rejection.</summary>
    public bool TryGetCurrentBounds(MotionKey key, out MeshBounds worldBounds)
        => TryGetBounds(_current, key, out worldBounds);

    static bool TryGetBounds(Generation generation, MotionKey key, out MeshBounds worldBounds)
    {
        if (!key.IsNone && generation.Skinned.TryGetValue(key.Value, out SkinnedEntry entry)
            && entry.WorldBounds is { } bounds)
        {
            worldBounds = bounds;
            return true;
        }
        worldBounds = default;
        return false;
    }
}
