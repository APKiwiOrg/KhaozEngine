using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    public sealed partial class Pointer
    {
        readonly List<Rect> _occluders = new();
        bool _occludeAll;

        /// <summary>
        /// Add an occluded area for the rest of this frame. A hit query whose test point lies in an occluded area
        /// misses, so content covered by something drawn in front of it answers no pointer input there. Cleared
        /// at the start of every <see cref="Update(KhaozEngine.Windowing.InputState)"/> and by
        /// <see cref="ClearOcclusion"/>. Unlike <see cref="BlockRegion"/>, which only reserves an area for
        /// <see cref="IsBlocked"/>, occlusion changes what the hit queries report.
        /// </summary>
        public void Occlude(Rect region) => _occluders.Add(region);

        /// <summary>Occlude the whole surface for the rest of this frame, so every hit query misses wherever the
        /// pointer is. Cleared with the occluded areas.</summary>
        public void OccludeAll() => _occludeAll = true;

        /// <summary>True if <paramref name="point"/> lies in an area occluded this frame through
        /// <see cref="Occlude"/>, or the whole surface is occluded through <see cref="OccludeAll"/>.</summary>
        public bool IsOccluded(Vector2 point)
        {
            if (_occludeAll) return true;
            foreach (var r in _occluders) if (r.Contains(point)) return true;
            return false;
        }

        /// <summary>Drop every occluded area and the whole-surface occlusion before the next
        /// <see cref="Update(KhaozEngine.Windowing.InputState)"/>, so content drawn after this call reads the
        /// pointer as if nothing covered it.</summary>
        public void ClearOcclusion()
        {
            _occluders.Clear();
            _occludeAll = false;
        }
    }
}
