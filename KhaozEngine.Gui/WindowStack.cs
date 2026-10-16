using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// The z-order of a consumer's non-modal windows. Register each window once with <see cref="Add"/>. The stack
    /// raises a window when it opens or is pressed, updates windows top to bottom so each sees only its uncovered
    /// area, and draws them bottom to top.
    /// </summary>
    /// <remarks>
    /// <para>Windows live in three bands, <see cref="WindowBand.Docked"/>, <see cref="WindowBand.Floating"/> and
    /// <see cref="WindowBand.Topmost"/>, bottom to top. No window rises above a higher band.</para>
    /// <para>A window that becomes open is raised to the front of its band. While the left button is held that
    /// raise waits until the gesture has ended. <see cref="Update"/> skips it on the release frame too and that
    /// frame's <see cref="Draw"/> applies it, so the release's taps and drops resolve against the order the player
    /// saw and a drag in progress keeps its window on top.</para>
    /// <para>A fresh left or right press raises the front-most open window containing its press origin, tested
    /// against where the windows were before this frame's updates. No press raises anything while a window
    /// captures the pointer, while the other mouse button is held from an earlier frame, or on a frame that
    /// receives no input.</para>
    /// <para>Each window updates with every window above it occluded on the pointer, so a covered area answers
    /// no hit query. A window that closes during its own update still occludes for the rest of that frame,
    /// because the player saw it, and is skipped from the next frame on. After <see cref="Update"/> every
    /// window that updated stays occluded and reserved with <see cref="Pointer.BlockRegion"/>, so the layers
    /// beneath treat windows as UI. Update the stack before
    /// anything beneath reads the pointer. <see cref="Draw"/> clears occlusion when it finishes, so content drawn
    /// after it, such as tooltips, reads the pointer as uncovered.</para>
    /// </remarks>
    public sealed class WindowStack
    {
        const int BandCount = 3;

        readonly List<IStackWindow>[] _bands =
        {
            new List<IStackWindow>(), new List<IStackWindow>(), new List<IStackWindow>(),
        };
        readonly Dictionary<IStackWindow, WindowBand> _bandOf = new();
        readonly HashSet<IStackWindow> _seenOpen = new();
        readonly List<IStackWindow> _pendingRaise = new();
        readonly List<IStackWindow> _order = new();
        readonly ReadOnlyCollection<IStackWindow> _orderView;
        readonly List<IStackWindow> _updateScratch = new();
        readonly List<IStackWindow> _drawScratch = new();
        readonly List<IStackWindow> _syncScratch = new();

        // The mode of the last Update, which Draw mirrors so draw-time hover agrees with it.
        UpdateMode _lastMode;
        IStackWindow? _lastCaptor;

        enum UpdateMode { Normal, Capture, NoInput }

        /// <summary>Build an empty stack.</summary>
        public WindowStack() => _orderView = _order.AsReadOnly();

        /// <summary>Every registered window, open or closed, bottom to top with the bands in order.</summary>
        public IReadOnlyList<IStackWindow> Order => _orderView;

        /// <summary>True when the last <see cref="Update"/> ran in capture mode, because an open window reported
        /// <see cref="IStackWindow.CapturesPointer"/> on a frame that received input. The captor's own popup owns
        /// that frame's keyboard, so gate global keys such as Escape on it.</summary>
        public bool IsCapturing => _lastMode == UpdateMode.Capture;

        /// <summary>Register <paramref name="window"/> at the front of <paramref name="band"/>. A window already
        /// registered moves to the front of the given band.</summary>
        public void Add(IStackWindow window, WindowBand band = WindowBand.Floating)
        {
            ArgumentNullException.ThrowIfNull(window);
            if ((uint)band >= BandCount) throw new ArgumentOutOfRangeException(nameof(band));
            if (_bandOf.TryGetValue(window, out var old)) _bands[(int)old].Remove(window);
            _bandOf[window] = band;
            _bands[(int)band].Add(window);
            _pendingRaise.Remove(window);
            // Already at the front, so a window that is open when added needs no raise on the next sync.
            if (window.IsOpen) _seenOpen.Add(window);
            else _seenOpen.Remove(window);
            RebuildOrder();
        }

        /// <summary>Unregister <paramref name="window"/>. Does nothing for a window that is not registered.</summary>
        public void Remove(IStackWindow window)
        {
            if (window is null || !_bandOf.Remove(window, out var band)) return;
            _bands[(int)band].Remove(window);
            _seenOpen.Remove(window);
            _pendingRaise.Remove(window);
            RebuildOrder();
        }

        /// <summary>Raise <paramref name="window"/> to the front of its band. Does nothing for a window that is
        /// not registered.</summary>
        public void BringToFront(IStackWindow window)
        {
            if (window is null || !_bandOf.TryGetValue(window, out var band)) return;
            var list = _bands[(int)band];
            if (list[^1] == window) return;
            list.Remove(window);
            list.Add(window);
            RebuildOrder();
        }

        /// <summary>
        /// Update every open window for this frame, top to bottom. Call after <see cref="Pointer.Update(InputState)"/>
        /// and before anything beneath the windows reads <paramref name="pointer"/>.
        /// </summary>
        /// <param name="pointer">The frame's pointer, in the space the windows are placed in.</param>
        /// <param name="input">The frame's input, handed to each window.</param>
        /// <param name="dt">Elapsed seconds since the last frame.</param>
        /// <param name="receivesInput">False when something above the stack took this frame's input. Every open
        /// window still updates, with the whole surface occluded and <see cref="WindowUpdate.ReceivesInput"/>
        /// false.</param>
        public void Update(Pointer pointer, InputState input, float dt, bool receivesInput = true)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            // The release frame's taps and drops resolve against the order the player saw, so a raise that waited
            // for the release is applied by that frame's Draw, not here.
            SyncOpenEdges(pointer.IsDown || pointer.IsJustReleased);

            var snapshot = _updateScratch;
            snapshot.Clear();
            for (int i = _order.Count - 1; i >= 0; i--)
                if (_order[i].IsOpen) snapshot.Add(_order[i]);

            _lastCaptor = null;
            if (!receivesInput)
            {
                _lastMode = UpdateMode.NoInput;
                pointer.OccludeAll();
                for (int i = 0; i < snapshot.Count; i++)
                    if (UpdateOne(snapshot, i, pointer, input, dt, false)) pointer.BlockRegion(snapshot[i].Bounds);
                snapshot.Clear();
                return;
            }

            int captor = -1;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].CapturesPointer) { captor = i; break; }

            if (captor < 0)
            {
                _lastMode = UpdateMode.Normal;
                RaiseOnPress(snapshot, pointer);
                snapshot.Clear();
                for (int i = _order.Count - 1; i >= 0; i--)
                    if (_order[i].IsOpen) snapshot.Add(_order[i]);

                // A window that closed during its own update still occludes: the tap that closed it must not
                // reach the window behind on the same frame.
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (!UpdateOne(snapshot, i, pointer, input, dt, true)) continue;
                    snapshot[i].Occlude(pointer);
                    pointer.BlockRegion(snapshot[i].Bounds);
                }
            }
            else
            {
                _lastMode = UpdateMode.Capture;
                _lastCaptor = snapshot[captor];
                if (UpdateOne(snapshot, captor, pointer, input, dt, true)) pointer.BlockRegion(snapshot[captor].Bounds);
                pointer.OccludeAll();
                for (int i = 0; i < snapshot.Count; i++)
                    if (i != captor && UpdateOne(snapshot, i, pointer, input, dt, true))
                        pointer.BlockRegion(snapshot[i].Bounds);
            }
            snapshot.Clear();
        }

        /// <summary>
        /// Draw every open window bottom to top. Before each window the pointer carries exactly the occlusion the
        /// last <see cref="Update"/> gave it, so draw-time hover agrees with the update: the occluders of the open
        /// windows above it on a normal frame, nothing for the captor and the whole surface for every other window
        /// after a capture frame, and the whole surface for every window after a frame that received no input.
        /// Occlusion is cleared when it finishes, so content drawn afterwards reads the pointer as uncovered.
        /// </summary>
        public void Draw(SpriteBatch batch, Pointer pointer)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            SyncOpenEdges(pointer.IsDown);

            var open = _drawScratch;
            open.Clear();
            foreach (var window in _order)
                if (window.IsOpen) open.Add(window);

            for (int i = 0; i < open.Count; i++)
            {
                var window = open[i];
                if (!IsLive(window)) continue;
                pointer.ClearOcclusion();
                if (_lastMode == UpdateMode.NoInput || (_lastMode == UpdateMode.Capture && window != _lastCaptor))
                {
                    pointer.OccludeAll();
                }
                else if (_lastMode == UpdateMode.Normal)
                {
                    for (int j = i + 1; j < open.Count; j++)
                        if (IsLive(open[j])) open[j].Occlude(pointer);
                }
                window.Draw(batch, pointer);
            }
            pointer.ClearOcclusion();
            open.Clear();
        }

        /// <summary>True when <paramref name="point"/> lies on any open window.</summary>
        public bool Covers(Vector2 point)
        {
            foreach (var window in _order)
                if (window.IsOpen && window.Contains(point)) return true;
            return false;
        }

        /// <summary>
        /// Close the top-most open window that closes on Escape, searching <see cref="WindowBand.Topmost"/>, then
        /// <see cref="WindowBand.Floating"/>, then <see cref="WindowBand.Docked"/>.
        /// </summary>
        /// <returns>Whether a window was closed.</returns>
        public bool CloseFront()
        {
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                var window = _order[i];
                if (!window.IsOpen || !window.ClosesOnEscape) continue;
                window.Close();
                return true;
            }
            return false;
        }

        // Raise windows that became open since the last sync, in the order they opened, unless the caller defers
        // the raise for a gesture in progress. A window that closes again before the raise applies drops out of
        // the queue.
        void SyncOpenEdges(bool defer)
        {
            var all = _syncScratch;
            all.Clear();
            all.AddRange(_order);
            foreach (var window in all)
            {
                if (window.IsOpen)
                {
                    if (_seenOpen.Add(window)) _pendingRaise.Add(window);
                }
                else if (_seenOpen.Remove(window))
                {
                    _pendingRaise.Remove(window);
                }
            }
            all.Clear();

            if (defer || _pendingRaise.Count == 0) return;
            foreach (var window in _pendingRaise) BringToFront(window);
            _pendingRaise.Clear();
        }

        // Rule 3: both targets are chosen from the order and places the player saw, before anything moves. A
        // press raises nothing while the other button is held from an earlier frame, so a gesture in progress
        // keeps its window on top.
        void RaiseOnPress(List<IStackWindow> topToBottom, Pointer pointer)
        {
            bool leftHeld = pointer.IsDown && !pointer.IsPressOriginFresh;
            bool rightHeld = pointer.IsRightDown && !pointer.IsRightPressOriginFresh;
            IStackWindow? left = pointer.IsPressOriginFresh && !rightHeld
                ? FrontMostAt(topToBottom, pointer.PressOrigin) : null;
            IStackWindow? right = pointer.IsRightPressOriginFresh && !leftHeld
                ? FrontMostAt(topToBottom, pointer.RightPressOrigin) : null;
            if (left is not null) BringToFront(left);
            if (right is not null) BringToFront(right);
        }

        static IStackWindow? FrontMostAt(List<IStackWindow> topToBottom, Vector2 point)
        {
            foreach (var window in topToBottom)
                if (window.Contains(point)) return window;
            return null;
        }

        // Open and still registered: a window another window closed or removed earlier this frame is skipped.
        bool IsLive(IStackWindow window) => window.IsOpen && _bandOf.ContainsKey(window);

        // Update one window unless another window closed or removed it earlier this frame. Reports whether it
        // updated.
        bool UpdateOne(List<IStackWindow> topToBottom, int index, Pointer pointer, InputState input, float dt,
            bool receivesInput)
        {
            var window = topToBottom[index];
            if (!IsLive(window)) return false;
            window.Update(new WindowUpdate(pointer, input, dt, receivesInput, IsFrontAt(topToBottom, index)));
            return true;
        }

        // Front of its band: no window above it in the same band is still open and registered. Only called for a
        // registered window.
        bool IsFrontAt(List<IStackWindow> topToBottom, int index)
        {
            var band = _bandOf[topToBottom[index]];
            for (int i = 0; i < index; i++)
            {
                var above = topToBottom[i];
                if (above.IsOpen && _bandOf.TryGetValue(above, out var aboveBand) && aboveBand == band) return false;
            }
            return true;
        }

        void RebuildOrder()
        {
            _order.Clear();
            foreach (var band in _bands) _order.AddRange(band);
        }
    }
}
