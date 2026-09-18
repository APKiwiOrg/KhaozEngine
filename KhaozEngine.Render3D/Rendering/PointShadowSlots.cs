using System;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Which light owns which row of the point-shadow atlas, and which of those rows still hold a map worth
    /// sampling. A pure cache: no device, no scene, no geometry, so the whole eviction and dirty policy is
    /// headless-testable and the frame flow above it can be read as scheduling rather than as bookkeeping.
    /// <para>
    /// A row is identified by the pair (<c>key</c>, <see cref="LightShadowMode"/>) rather than by the key alone,
    /// because a <see cref="LightShadowMode.Dynamic"/> light carries no key of its own
    /// (<see cref="LightShadow.Dynamic"/> is key 0) and the scene keys one by its place in the light queue. That
    /// number collides with a static caller's key by construction, and the two must still be two rows.
    /// </para>
    /// <para>
    /// DIRTY AND NEVER-RENDERED ARE TWO DIFFERENT QUESTIONS, and conflating them is the bug this type is shaped
    /// around. A dirty row whose map was rendered on an earlier frame is worth sampling: it is a frame or two
    /// stale, which is exactly what the static rebuild budget trades away. A row nothing has ever rendered into
    /// holds whatever the allocation left there, so the light that owns it must be handed -1 and sample nothing at
    /// all. <see cref="EverRendered"/> is that second question.
    /// </para>
    /// </summary>
    internal sealed class PointShadowSlots
    {
        struct Entry
        {
            public bool Occupied;
            public long Key;
            public LightShadowMode Mode;
            public bool Dirty;
            public bool Rendered;          // has anything ever been drawn into this row under its CURRENT owner
            public int LastRequestedFrame; // the last frame Acquire named it, which is what eviction ranks on
            public int LastRenderedFrame;  // the last frame MarkClean named it, which is what staleness ranks on
        }

        readonly Entry[] _entries;

        /// <summary>Build a cache of <paramref name="capacity"/> rows, which is the atlas row count. A capacity of
        /// zero is legal and refuses every acquire, which is what a scene holds before any atlas exists.</summary>
        public PointShadowSlots(int capacity)
        {
            _entries = new Entry[Math.Max(0, capacity)];
        }

        /// <summary>How many rows the atlas this cache describes carries.</summary>
        public int Capacity => _entries.Length;

        /// <summary>How many rows currently belong to a light, including rows held for a light that was not
        /// requested on this frame and has not been evicted yet.</summary>
        public int InUse
        {
            get
            {
                int n = 0;
                foreach (Entry e in _entries) if (e.Occupied) n++;
                return n;
            }
        }

        /// <summary>
        /// RE-SEAT an owner without ever evicting anybody: if (<paramref name="key"/>,
        /// <paramref name="mode"/>) already holds a row, stamp it as requested on <paramref name="frame"/> and
        /// return it, keeping its map. Otherwise return -1 and change nothing.
        /// <para>
        /// This is PHASE ONE of a frame's acquire, and the frame path has to run it over every request before it
        /// offers a row to anybody, because eviction ranks on the last requested frame and a caller that acquired
        /// a newcomer FIRST would evict an incumbent that was about to ask. That eviction then cascades: the
        /// displaced incumbent acquires, evicts the next row, and one arrival costs as many valid maps as there
        /// are lights behind it. Re-seating every owner first makes every one of those rows "requested this
        /// frame", which <see cref="Acquire"/> already refuses to evict.
        /// </para>
        /// </summary>
        public int TryTouch(long key, LightShadowMode mode, int frame)
        {
            if (mode == LightShadowMode.None) return -1;
            for (int i = 0; i < _entries.Length; i++)
            {
                ref Entry e = ref _entries[i];
                if (!e.Occupied || e.Key != key || e.Mode != mode) continue;
                e.LastRequestedFrame = frame;
                if (mode == LightShadowMode.Dynamic) e.Dirty = true;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Claim the row for (<paramref name="key"/>, <paramref name="mode"/>) on <paramref name="frame"/>,
        /// returning its index or -1 when every row already belongs to a light requested on this same frame.
        /// <para>
        /// An existing owner keeps its row and its map (this is <see cref="TryTouch"/>). A free row is taken and
        /// comes back DIRTY with nothing rendered into it. When neither exists, the least recently requested row
        /// NOT requested this frame is evicted, and its new owner is likewise dirty with nothing rendered, because
        /// whatever is on that row belongs to the light that just lost it.
        /// </para>
        /// <para>
        /// A <see cref="LightShadowMode.Dynamic"/> row is marked dirty on every acquire, which is the whole
        /// difference between the two modes: it is rebuilt every frame it is rendered at all, where a static row
        /// is rebuilt only when the caller's signature says something under it moved.
        /// </para>
        /// </summary>
        public int Acquire(long key, LightShadowMode mode, int frame)
        {
            if (mode == LightShadowMode.None) return -1;
            int owned = TryTouch(key, mode, frame);
            if (owned >= 0) return owned;

            int free = -1;
            int evict = -1;
            for (int i = 0; i < _entries.Length; i++)
            {
                ref Entry e = ref _entries[i];
                if (!e.Occupied)
                {
                    if (free < 0) free = i;
                    continue;
                }
                if (e.LastRequestedFrame == frame) continue;   // in use by this very frame: not evictable
                if (evict < 0 || e.LastRequestedFrame < _entries[evict].LastRequestedFrame) evict = i;
            }

            int slot = free >= 0 ? free : evict;
            if (slot < 0) return -1;
            _entries[slot] = new Entry
            {
                Occupied = true,
                Key = key,
                Mode = mode,
                Dirty = true,
                Rendered = false,
                LastRequestedFrame = frame,
                LastRenderedFrame = int.MinValue,
            };
            return slot;
        }

        /// <summary>Whether <paramref name="slot"/>'s map has to be re-rendered before it is correct. Out-of-range
        /// and unoccupied rows answer false, because there is nothing to rebuild.</summary>
        public bool IsDirty(int slot) => InRange(slot) && _entries[slot].Occupied && _entries[slot].Dirty;

        /// <summary>Whether anything has ever been drawn into <paramref name="slot"/> under its current owner. A
        /// row that answers false holds uninitialized memory as far as its owner is concerned, so its light must
        /// be given no slot at all rather than a stale one.</summary>
        public bool EverRendered(int slot) => InRange(slot) && _entries[slot].Occupied && _entries[slot].Rendered;

        /// <summary>The frame <paramref name="slot"/> was last rendered on, or <see cref="int.MinValue"/> when it
        /// never has been. Ranks the rebuild queue: the stalest row is rebuilt first, and a row with nothing on it
        /// sorts ahead of every rendered one.</summary>
        public int LastRenderedFrame(int slot) =>
            InRange(slot) && _entries[slot].Occupied ? _entries[slot].LastRenderedFrame : int.MinValue;

        /// <summary>The key of <paramref name="slot"/>'s owner, or 0 when the row is free or out of range.</summary>
        public long KeyOf(int slot) => InRange(slot) && _entries[slot].Occupied ? _entries[slot].Key : 0L;

        /// <summary>Force <paramref name="slot"/> to be re-rendered. What the caller calls when its own caster
        /// signature for that light changed.</summary>
        public void MarkDirty(int slot)
        {
            if (!InRange(slot) || !_entries[slot].Occupied) return;
            _entries[slot].Dirty = true;
        }

        /// <summary>
        /// Forget every row's CONTENTS while keeping its owner: each occupied row becomes dirty and, more to the
        /// point, never-rendered, so its light is handed -1 until the pass has drawn into it again.
        /// <para>
        /// This is what a new atlas texture means. A layout change frees the texture the rows lived in, so a row
        /// left reading <see cref="EverRendered"/> would hand its light a slot pointing into freshly allocated
        /// memory and the receiver would sample whatever was in it, for as many frames as the rebuild budget takes
        /// to reach that row. Dirty alone does not say that, by design: a dirty row with content is the ordinary
        /// deferred rebuild and is worth sampling.
        /// </para>
        /// </summary>
        public void InvalidateEveryRow()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (!_entries[i].Occupied) continue;
                _entries[i].Dirty = true;
                _entries[i].Rendered = false;
            }
        }

        /// <summary>Record that <paramref name="slot"/> was rendered on <paramref name="frame"/>: it is clean, it
        /// has content, and it is now the freshest row rather than the stalest.</summary>
        public void MarkClean(int slot, int frame)
        {
            if (!InRange(slot) || !_entries[slot].Occupied) return;
            ref Entry e = ref _entries[slot];
            e.Dirty = false;
            e.Rendered = true;
            e.LastRenderedFrame = frame;
        }

        /// <summary>
        /// Free every <see cref="LightShadowMode.Dynamic"/> row outright. Called at the START of a frame's
        /// acquire, before phase one, so a dynamic row never survives the frame it was drawn on.
        /// <para>
        /// A DYNAMIC LIGHT HAS NO IDENTITY ACROSS FRAMES. It carries no key of its own, so the scene keys it by
        /// its place in the light queue, and that number belongs to a different light the moment one dynamic light
        /// expires and the rest shift down. A row kept across the frame boundary would be matched by its new
        /// namesake, reported as rendered, and sampled as a shadow cast from the old light's position, for as long
        /// as that light stayed past the per-frame dynamic budget. So the row goes back every frame and a dynamic
        /// light either renders this frame or samples nothing.
        /// </para>
        /// </summary>
        public void ReleaseDynamicRows()
        {
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Occupied && _entries[i].Mode == LightShadowMode.Dynamic) _entries[i] = default;
        }

        /// <summary>Free every row whose light was not requested on <paramref name="frame"/>. Called once a frame,
        /// so a light that stops being queued gives its row back to the next one that asks instead of being
        /// evicted later under pressure.</summary>
        public void ReleaseUnrequested(int frame)
        {
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Occupied && _entries[i].LastRequestedFrame != frame) _entries[i] = default;
        }

        bool InRange(int slot) => (uint)slot < (uint)_entries.Length;
    }
}
