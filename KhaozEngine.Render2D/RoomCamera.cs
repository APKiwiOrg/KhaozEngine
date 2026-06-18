using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Turnkey Metroidvania-style room camera: follows a target confined to the region (<see cref="CameraRoom"/>)
    /// it is in, and eases (blends) to reframe when the target crosses into a new region, then resumes
    /// following. Composes an internal <see cref="CameraFollow"/> (in-room feel, exposed via <see cref="Follow"/>)
    /// and <see cref="CameraBlend"/> (region hand-off). Headless, no GPU.
    /// </summary>
    public sealed class RoomCamera
    {
        private readonly Camera2D _camera;
        private readonly IReadOnlyList<CameraRoom> _rooms;
        private readonly CameraFollow _follow;
        private readonly CameraBlend _blend;
        private int _activeIndex = -1;

        /// <summary>Creates a room camera over <paramref name="camera"/> with the given rooms (priority is list order).</summary>
        public RoomCamera(Camera2D camera, IReadOnlyList<CameraRoom> rooms)
        {
            _camera = camera;
            _rooms = rooms;
            _follow = new CameraFollow(camera);
            _blend = new CameraBlend(camera);
        }

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>The internal in-room follow - tune its Stiffness/Deadzone/LookAhead/Snap for the in-room
        /// feel. Do NOT call its Update/Warp directly: RoomCamera drives it.</summary>
        public CameraFollow Follow => _follow;

        /// <summary>Index of the active room, or -1 until the first room is acquired.</summary>
        public int ActiveRoomIndex => _activeIndex;

        /// <summary>True while a region hand-off blend is running.</summary>
        public bool IsTransitioning { get; private set; }

        /// <summary>Duration (seconds) of the hand-off blend on a region change.</summary>
        public float BlendDuration { get; set; } = 0.4f;

        /// <summary>Easing curve for the hand-off blend.</summary>
        public Func<float, float> BlendEasing { get; set; } = Easing.SmoothStep;

        /// <summary>
        /// Resolves the active room from <paramref name="target"/>, hands off (blends) on a region change,
        /// otherwise follows within the active room. <paramref name="velocity"/> drives the follow look-ahead.
        /// </summary>
        public void Update(Vector2 target, Vector2 velocity, float dt, int viewportWidth, int viewportHeight)
        {
            int resolved = Resolve(target);
            if (resolved < 0) return;   // no room contains the target and none is active yet

            if (resolved != _activeIndex)
            {
                if (_activeIndex < 0)
                    SnapTo(resolved, target, viewportWidth, viewportHeight);
                else
                    BeginHandoff(resolved, target, viewportWidth, viewportHeight);
            }

            if (IsTransitioning)
            {
                _blend.Update(dt);
                if (!_blend.IsBlending)
                {
                    _follow.Warp(_camera.Position);   // resume following from the blended frame
                    IsTransitioning = false;
                }
                return;
            }

            // Settled (incl. the first-acquisition frame: SnapTo warped the follow, this resumes following).
            _follow.Update(target, velocity, dt, viewportWidth, viewportHeight, _rooms[_activeIndex].Bounds);
        }

        /// <summary>Convenience overload with zero velocity.</summary>
        public void Update(Vector2 target, float dt, int viewportWidth, int viewportHeight)
            => Update(target, Vector2.Zero, dt, viewportWidth, viewportHeight);

        /// <summary>Snaps instantly to the room containing <paramref name="target"/> (no blend); applies the
        /// room's zoom and positions the follow. No-op if no room contains the target.</summary>
        public void Warp(Vector2 target, int viewportWidth, int viewportHeight)
        {
            int resolved = Resolve(target);
            if (resolved < 0) return;
            SnapTo(resolved, target, viewportWidth, viewportHeight);
        }

        // Lowest-index room containing target; else the current active room; else -1.
        private int Resolve(Vector2 target)
        {
            for (int i = 0; i < _rooms.Count; i++)
                if (_rooms[i].Contains(target)) return i;
            return _activeIndex;
        }

        private void SnapTo(int index, Vector2 target, int vw, int vh)
        {
            CameraRoom room = _rooms[index];
            float zoom = room.Zoom ?? _camera.Zoom;
            _blend.Stop();   // cancel any in-flight hand-off; a snap supersedes a transition
            _camera.Zoom = zoom;
            _follow.Warp(_camera.ClampPosition(target, room.Bounds, vw, vh, zoom));
            _activeIndex = index;
            IsTransitioning = false;
        }

        private void BeginHandoff(int index, Vector2 target, int vw, int vh)
        {
            CameraRoom room = _rooms[index];
            float zoom = room.Zoom ?? _camera.Zoom;
            Vector2 pos = _camera.ClampPosition(target, room.Bounds, vw, vh, zoom);
            _blend.To(new CameraState(pos, zoom, _camera.Rotation), BlendDuration, BlendEasing);
            _activeIndex = index;
            IsTransitioning = true;
        }
    }
}
