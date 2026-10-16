using System;
using System.Collections.Generic;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// A rectangle window for <see cref="WindowStack"/> facts. It records the order the stack updates and draws
    /// it in a log shared with its siblings, and what the pointer told it on its last update and draw.
    /// </summary>
    sealed class FakeStackWindow : IStackWindow
    {
        readonly List<string> _log;

        public FakeStackWindow(string name, Rect bounds, List<string> log, bool open = true)
        {
            Name = name;
            Bounds = bounds;
            Probe = bounds;
            IsOpen = open;
            _log = log;
        }

        public string Name { get; }
        public bool IsOpen { get; set; }
        public Rect Bounds { get; set; }
        public bool CapturesPointer { get; set; }
        public bool ClosesOnEscape { get; set; } = true;

        /// <summary>A second rect whose hover the window records, for reads outside its own bounds.</summary>
        public Rect Probe { get; set; }

        /// <summary>Runs inside <see cref="Update"/>, after the reads are recorded.</summary>
        public Action<WindowUpdate>? OnUpdate { get; set; }

        public int CloseCalls { get; private set; }
        public int UpdateCount { get; private set; }
        public bool IsFront { get; private set; }
        public bool ReceivesInput { get; private set; }
        public bool Hover { get; private set; }
        public bool Tap { get; private set; }
        public bool ProbeHover { get; private set; }
        public bool DrawHover { get; private set; }

        public void Close()
        {
            CloseCalls++;
            IsOpen = false;
        }

        public void Update(in WindowUpdate update)
        {
            _log.Add("update:" + Name);
            UpdateCount++;
            IsFront = update.IsFront;
            ReceivesInput = update.ReceivesInput;
            Hover = update.Pointer.IsHoveringIn(Bounds);
            Tap = update.Pointer.IsTapIn(Bounds);
            ProbeHover = update.Pointer.IsHoveringIn(Probe);
            OnUpdate?.Invoke(update);
        }

        public void Draw(SpriteBatch batch, Pointer pointer)
        {
            _log.Add("draw:" + Name);
            DrawHover = pointer.IsHoveringIn(Bounds);
        }

        public override string ToString() => Name;
    }
}
